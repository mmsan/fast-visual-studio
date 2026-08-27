using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FastVisualStudioBridge
{
    internal sealed class LocalUpdateService : IDisposable
    {
#if FAST_LOCAL_UPDATE
        private static readonly bool EnabledForBuild = true;
#else
        private static readonly bool EnabledForBuild = false;
#endif
        private const string ExtensionId = "FastVisualStudioBridge.27861355-A640-4497-911A-EA99B558E93F";
        private readonly BridgeOptions options;
        private readonly EnvDTE.DTE dte;
        private readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly SemaphoreSlim checking = new SemaphoreSlim(1, 1);
        private Timer timer;
        private string scheduledVersion;
        private bool disposed;

        internal LocalUpdateService(BridgeOptions options, EnvDTE.DTE dte)
        {
            this.options = options;
            this.dte = dte;
        }

        internal void Start()
        {
            if (!EnabledForBuild) return;
            // Migrate older minute-based values to the new 10-second local polling minimum.
            if (options.UpdateCheckMinutes < 10)
            {
                options.UpdateCheckMinutes = 10;
                options.SaveSettingsToStorage();
            }
            ResetTimer(TimeSpan.FromSeconds(10));
        }
        internal void SettingsChanged()
        {
            if (EnabledForBuild) ResetTimer(TimeSpan.Zero);
        }

        private void ResetTimer(TimeSpan due)
        {
            if (disposed) return;
            timer?.Dispose();
            if (!options.AutoUpdate) { timer = null; return; }
            var interval = TimeSpan.FromSeconds(Math.Max(10, Math.Min(3600, options.UpdateCheckMinutes)));
            timer = new Timer(_ => CheckForUpdateAsync().Forget(), null, due, interval);
        }

        private async Task CheckForUpdateAsync()
        {
            if (disposed || !options.AutoUpdate || !await checking.WaitAsync(0).ConfigureAwait(false)) return;
            string ownedUpdateLockPath = null;
            try
            {
                if (!Uri.TryCreate(options.UpdateFeedUrl, UriKind.Absolute, out var feedUri) || !feedUri.IsLoopback)
                    throw new InvalidOperationException("The automatic update feed must use a loopback address.");

                // Parse the response as raw XML bytes. Some simple local HTTP servers send
                // UTF-8 XML with an incorrect/default HTTP charset; GetStringAsync then turns
                // the BOM into the visible characters "ï»¿" and XDocument.Parse rejects it.
                var feedBytes = await client.GetByteArrayAsync(feedUri).ConfigureAwait(false);
                XDocument document;
                using (var feedStream = new MemoryStream(feedBytes, writable: false))
                    document = XDocument.Load(feedStream);
                var candidates = document.Descendants().Where(element => element.Name.LocalName == "entry")
                    .Select(entry => new
                    {
                        Entry = entry,
                        Vsix = entry.Descendants().FirstOrDefault(element => element.Name.LocalName == "Vsix"),
                        Content = entry.Elements().FirstOrDefault(element => element.Name.LocalName == "content")
                    })
                    .Where(item => item.Vsix != null &&
                        item.Vsix.Elements().FirstOrDefault(element => element.Name.LocalName == "Id")?.Value == ExtensionId)
                    .Select(item => new
                    {
                        VersionText = item.Vsix.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value,
                        Source = item.Content?.Attribute("src")?.Value
                    })
                    .Where(item => Version.TryParse(item.VersionText, out _) && !string.IsNullOrWhiteSpace(item.Source))
                    .Select(item => new { Version = Version.Parse(item.VersionText), item.Source })
                    .OrderByDescending(item => item.Version)
                    .FirstOrDefault();

                var current = Assembly.GetExecutingAssembly().GetName().Version;
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Mirror", "FastVisualStudioBridge", "Updates");
                if (candidates == null || candidates.Version <= current || scheduledVersion == candidates.Version.ToString() ||
                    string.Equals(ReadDeclinedVersion(directory), candidates.Version.ToString(), StringComparison.Ordinal)) return;

                var packageUri = new Uri(feedUri, candidates.Source);
                if (!packageUri.IsLoopback) throw new InvalidOperationException("The update package must use a loopback address.");
                var bytes = await client.GetByteArrayAsync(packageUri).ConfigureAwait(false);
                if (bytes.Length < 1024) throw new InvalidDataException("The downloaded VSIX is unexpectedly small.");

                Directory.CreateDirectory(directory);
                ownedUpdateLockPath = TryAcquireUpdateLock(directory, candidates.Version);
                if (ownedUpdateLockPath == null) return;
                var packagePath = Path.Combine(directory, "FastVisualStudioBridge-pending.vsix");
                var temporaryPath = packagePath + ".download";
                File.WriteAllBytes(temporaryPath, bytes);
                if (File.Exists(packagePath)) File.Delete(packagePath);
                File.Move(temporaryPath, packagePath);
                File.WriteAllText(Path.Combine(directory, "pending-version.txt"), candidates.Version.ToString());
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var solutionPath = dte?.Solution?.FullName;
                var answer = VsShellUtilities.ShowMessageBox(
                    FastBridgePackage.Instance,
                    "Fast Visual Studio Bridge " + candidates.Version + " 已下载。是否立即重启 Visual Studio 并安装更新？",
                    "Fast Visual Studio Bridge 更新",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                if (answer != (int)VSConstants.MessageBoxResult.IDYES)
                {
                    File.WriteAllText(Path.Combine(directory, "declined-version.txt"), candidates.Version.ToString());
                    File.Delete(ownedUpdateLockPath);
                    ownedUpdateLockPath = null;
                    return;
                }
                ScheduleInstaller(packagePath, candidates.Version, solutionPath, ownedUpdateLockPath);
                ownedUpdateLockPath = null;
                scheduledVersion = candidates.Version.ToString();
                ActivityLog.LogInformation(nameof(FastVisualStudioBridge),
                    "Downloaded update " + candidates.Version + "; Visual Studio will restart to install it.");
                dte?.Quit();
            }
            catch (Exception error)
            {
                if (ownedUpdateLockPath != null) File.Delete(ownedUpdateLockPath);
                ActivityLog.LogWarning(nameof(FastVisualStudioBridge), "Automatic update check failed: " + error.Message);
            }
            finally { checking.Release(); }
        }

        private static string TryAcquireUpdateLock(string directory, Version version)
        {
            var lockPath = Path.Combine(directory, "install-pending.lock");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream)) writer.Write(version);
                    return lockPath;
                }
                catch (IOException)
                {
                    if (attempt != 0 || !File.Exists(lockPath) ||
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromMinutes(10)) return null;
                    File.Delete(lockPath);
                }
            }
            return null;
        }

        private static string ReadDeclinedVersion(string directory)
        {
            try { return File.ReadAllText(Path.Combine(directory, "declined-version.txt")).Trim(); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void ScheduleInstaller(string packagePath, Version version, string solutionPath, string updateLockPath)
        {
            var process = Process.GetCurrentProcess();
            var ideDirectory = Path.GetDirectoryName(process.MainModule.FileName);
            var installer = Path.Combine(ideDirectory, "VSIXInstaller.exe");
            if (!File.Exists(installer)) throw new FileNotFoundException("VSIXInstaller.exe was not found.", installer);
            var visualStudioRoot = Directory.GetParent(ideDirectory)?.Parent?.FullName;
            var msbuildRoot = string.IsNullOrWhiteSpace(visualStudioRoot)
                ? null
                : Path.Combine(visualStudioRoot, "MSBuild");
            if (string.IsNullOrWhiteSpace(msbuildRoot)) throw new InvalidOperationException("Could not determine the Visual Studio installation root.");

            var helperDirectory = Path.GetDirectoryName(packagePath);
            var helperPath = Path.Combine(helperDirectory, "install-pending.ps1");
            var logPath = Path.Combine(helperDirectory, "install.log");
            var pendingVersionPath = Path.Combine(helperDirectory, "pending-version.txt");
            var script =
                "$ErrorActionPreference = 'Continue'\r\n" +
                "Get-Process -Name devenv -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }\r\n" +
                "$graceDeadline = (Get-Date).AddSeconds(30)\r\n" +
                "while ((Get-Process -Name devenv -ErrorAction SilentlyContinue) -and (Get-Date) -lt $graceDeadline) { Start-Sleep -Seconds 2 }\r\n" +
                "$headless = Get-Process -Name devenv -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -eq 0 }\r\n" +
                "if ($headless) {\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Stopping headless devenv processes after graceful-close timeout: ' + (($headless.Id) -join ','))\r\n" +
                "  $headless | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "}\r\n" +
                "$visible = Get-Process -Name devenv -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }\r\n" +
                "if ($visible) { Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Visual Studio did not exit after the graceful-close timeout; waiting for the user instead of forcing close.'); Remove-Item -LiteralPath '" + PowerShellLiteral(updateLockPath) + "' -Force -ErrorAction SilentlyContinue; exit 2004 }\r\n" +
                "$msbuildRoot = '" + PowerShellLiteral(msbuildRoot) + "'\r\n" +
                "function Get-FastVisualStudioMsBuild { Get-Process -Name MSBuild -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($msbuildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } }\r\n" +
                "$msbuildDeadline = (Get-Date).AddSeconds(30)\r\n" +
                "while ((@(Get-FastVisualStudioMsBuild)).Count -gt 0 -and (Get-Date) -lt $msbuildDeadline) { Start-Sleep -Seconds 2 }\r\n" +
                "$msbuild = @(Get-FastVisualStudioMsBuild)\r\n" +
                "if ($msbuild) {\r\n" +
                "  $visibleMsBuild = @($msbuild | Where-Object { $_.MainWindowHandle -ne 0 })\r\n" +
                "  if ($visibleMsBuild) { Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Visual Studio MSBuild is still running with a visible window: ' + (($visibleMsBuild.Id) -join ',') + '; retrying later.'); Remove-Item -LiteralPath '" + PowerShellLiteral(updateLockPath) + "' -Force -ErrorAction SilentlyContinue; exit 2004 }\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Stopping Visual Studio MSBuild processes blocking VSIX installation: ' + (($msbuild.Id) -join ','))\r\n" +
                "  $msbuild | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "  Start-Sleep -Seconds 2\r\n" +
                "}\r\n" +
                "Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Preparing update " + version + "')\r\n" +
                "$result = -1\r\n" +
                "for ($attempt = 1; $attempt -le 3; $attempt++) {\r\n" +
                "  $installProcess = Start-Process -FilePath '" + PowerShellLiteral(installer) + "' -ArgumentList @('/quiet', '" + PowerShellLiteral(packagePath) + "') -WindowStyle Hidden -Wait -PassThru\r\n" +
                "  $result = $installProcess.ExitCode\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] VSIX install attempt ' + $attempt + ' exit code: ' + $result)\r\n" +
                "  if ($result -eq 0) { break }\r\n" +
                "  Start-Sleep -Seconds 3\r\n" +
                "}\r\n" +
                "if ($result -ne 0) { Remove-Item -LiteralPath '" + PowerShellLiteral(updateLockPath) + "' -Force -ErrorAction SilentlyContinue; exit $result }\r\n" +
                "Start-Sleep -Seconds 3\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(packagePath) + "' -Force -ErrorAction SilentlyContinue\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(pendingVersionPath) + "' -Force -ErrorAction SilentlyContinue\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(updateLockPath) + "' -Force -ErrorAction SilentlyContinue\r\n" +
                (string.IsNullOrWhiteSpace(solutionPath)
                    ? "$launchArgs = @()\r\n"
                    : "$launchArgs = @('" + PowerShellLiteral(solutionPath) + "')\r\n") +
                "$started = Start-Process -FilePath '" + PowerShellLiteral(process.MainModule.FileName) + "' -ArgumentList $launchArgs -PassThru\r\n" +
                "$windowDeadline = (Get-Date).AddSeconds(15)\r\n" +
                "do { Start-Sleep -Seconds 1; $started.Refresh() } while (-not $started.HasExited -and $started.MainWindowHandle -eq 0 -and (Get-Date) -lt $windowDeadline)\r\n" +
                "if (-not $started.HasExited -and $started.MainWindowHandle -eq 0) {\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Relaunching after headless startup; pid=' + $started.Id)\r\n" +
                "  Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue\r\n" +
                "  Start-Sleep -Seconds 2\r\n" +
                "  Start-Process -FilePath '" + PowerShellLiteral(process.MainModule.FileName) + "' -ArgumentList $launchArgs\r\n" +
                "}\r\n";
            File.WriteAllText(helperPath, script, new System.Text.UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + helperPath + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        private static string PowerShellLiteral(string value) => (value ?? "").Replace("'", "''");

        public void Dispose()
        {
            disposed = true;
            timer?.Dispose();
            client.Dispose();
            checking.Dispose();
        }
    }

    internal static class UpdateTaskExtensions
    {
        internal static async void Forget(this Task task)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
    }
}
