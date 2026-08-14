using Microsoft.VisualStudio.Shell;
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
        private readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly SemaphoreSlim checking = new SemaphoreSlim(1, 1);
        private Timer timer;
        private string scheduledVersion;
        private bool disposed;

        internal LocalUpdateService(BridgeOptions options) { this.options = options; }

        internal void Start()
        {
            if (!EnabledForBuild) return;
            // Migrate the former default. A loopback-only feed does not need a 30-minute polling delay.
            if (options.UpdateCheckMinutes == 30)
            {
                options.UpdateCheckMinutes = 1;
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
            var interval = TimeSpan.FromMinutes(Math.Max(1, Math.Min(1440, options.UpdateCheckMinutes)));
            timer = new Timer(_ => CheckForUpdateAsync().Forget(), null, due, interval);
        }

        private async Task CheckForUpdateAsync()
        {
            if (disposed || !options.AutoUpdate || !await checking.WaitAsync(0).ConfigureAwait(false)) return;
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
                if (candidates == null || candidates.Version <= current || scheduledVersion == candidates.Version.ToString()) return;

                var packageUri = new Uri(feedUri, candidates.Source);
                if (!packageUri.IsLoopback) throw new InvalidOperationException("The update package must use a loopback address.");
                var bytes = await client.GetByteArrayAsync(packageUri).ConfigureAwait(false);
                if (bytes.Length < 1024) throw new InvalidDataException("The downloaded VSIX is unexpectedly small.");

                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Mirror", "FastVisualStudioBridge", "Updates");
                Directory.CreateDirectory(directory);
                var packagePath = Path.Combine(directory, "FastVisualStudioBridge-pending.vsix");
                var temporaryPath = packagePath + ".download";
                File.WriteAllBytes(temporaryPath, bytes);
                if (File.Exists(packagePath)) File.Delete(packagePath);
                File.Move(temporaryPath, packagePath);
                File.WriteAllText(Path.Combine(directory, "pending-version.txt"), candidates.Version.ToString());
                ScheduleInstaller(packagePath, candidates.Version);
                scheduledVersion = candidates.Version.ToString();
                ActivityLog.LogInformation(nameof(FastVisualStudioBridge),
                    "Downloaded update " + candidates.Version + "; it will install after Visual Studio exits.");
            }
            catch (Exception error)
            {
                ActivityLog.LogWarning(nameof(FastVisualStudioBridge), "Automatic update check failed: " + error.Message);
            }
            finally { checking.Release(); }
        }

        private static void ScheduleInstaller(string packagePath, Version version)
        {
            var process = Process.GetCurrentProcess();
            var ideDirectory = Path.GetDirectoryName(process.MainModule.FileName);
            var installer = Path.Combine(ideDirectory, "VSIXInstaller.exe");
            if (!File.Exists(installer)) throw new FileNotFoundException("VSIXInstaller.exe was not found.", installer);

            var helperDirectory = Path.GetDirectoryName(packagePath);
            var helperPath = Path.Combine(helperDirectory, "install-pending.ps1");
            var logPath = Path.Combine(helperDirectory, "install.log");
            var pendingVersionPath = Path.Combine(helperDirectory, "pending-version.txt");
            var legacyLockDirectory = Path.Combine(helperDirectory, "install-pending.lock");
            var script =
                "$ErrorActionPreference = 'Continue'\r\n" +
                "while (Get-Process -Name devenv -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(legacyLockDirectory) + "' -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
                "Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] Preparing update " + version + "')\r\n" +
                "for ($i = 0; $i -lt 8; $i++) {\r\n" +
                "  & '" + PowerShellLiteral(installer) + "' /quiet '/uninstall:" + ExtensionId + "' *>> '" + PowerShellLiteral(logPath) + "'\r\n" +
                "  $uninstallResult = $LASTEXITCODE\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] VSIX uninstall exit code: ' + $uninstallResult)\r\n" +
                "  if ($uninstallResult -ne 0) { break }\r\n" +
                "}\r\n" +
                "$result = -1\r\n" +
                "for ($attempt = 1; $attempt -le 3; $attempt++) {\r\n" +
                "  & '" + PowerShellLiteral(installer) + "' /quiet '" + PowerShellLiteral(packagePath) + "' *>> '" + PowerShellLiteral(logPath) + "'\r\n" +
                "  $result = $LASTEXITCODE\r\n" +
                "  Add-Content -LiteralPath '" + PowerShellLiteral(logPath) + "' -Value ('[' + (Get-Date) + '] VSIX install attempt ' + $attempt + ' exit code: ' + $result)\r\n" +
                "  if ($result -eq 0) { break }\r\n" +
                "  Start-Sleep -Seconds 3\r\n" +
                "}\r\n" +
                "if ($result -ne 0) { exit $result }\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(packagePath) + "' -Force -ErrorAction SilentlyContinue\r\n" +
                "Remove-Item -LiteralPath '" + PowerShellLiteral(pendingVersionPath) + "' -Force -ErrorAction SilentlyContinue\r\n";
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
