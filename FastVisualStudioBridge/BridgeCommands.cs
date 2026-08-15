using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;

namespace FastVisualStudioBridge
{
    internal static class BridgeCommands
    {
        private sealed class CommandException : Exception
        {
            internal readonly string Code;
            internal CommandException(string code, string message) : base(message) { Code = code; }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr ActiveWindow;
            public IntPtr FocusWindow;
            public IntPtr CaptureWindow;
            public IntPtr MenuOwner;
            public IntPtr MoveSizeWindow;
            public IntPtr Caret;
        }

        internal sealed class ProjectInfo
        {
            internal string Id, Name, BasePath;
            internal IDictionary<string, object> ToDictionary() => new Dictionary<string, object>
            { ["projectId"] = Id, ["name"] = Name, ["basePath"] = BasePath };
        }

        internal static object Identity(DTE dte, string instanceId, string connectionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new Dictionary<string, object>
            {
                ["client"] = "fast-visual-studio-bridge",
                ["instanceId"] = instanceId, ["connectionId"] = connectionId,
                ["projects"] = Projects(dte).Select(p => p.ToDictionary()).ToList(),
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["nativeWindows"] = NativeWindows(dte),
                ["fullScreen"] = IsFullScreen(dte),
                ["editorOpenedAt"] = SnapshotCollector.CurrentEditorOpenedAt(dte),
                ["activeProject"] = ProjectForFile(dte, dte.ActiveDocument?.FullName)?.ToDictionary(),
                ["capabilities"] = new[] { "state", "getInputable", "getObjectInfo", "getSelectedText", "navigate", "select", "find", "requestFocus", "fullScreen", "isFullScreen", "listProjects", "copyResourcePath", "pythonPackages" }
            };
        }

        internal static object Source(DTE dte, string instanceId, string connectionId, object payload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var source = new Dictionary<string, object>
            {
                ["instanceId"] = instanceId, ["connectionId"] = connectionId,
                ["projects"] = Projects(dte).Select(p => p.ToDictionary()).ToList()
            };
            source["nativeWindows"] = NativeWindows(dte);
            try
            {
                var file = dte.ActiveDocument?.FullName;
                var project = ProjectForFile(dte, file);
                if (project != null) { source["projectId"] = project.Id; source["projectName"] = project.Name; source["projectBasePath"] = project.BasePath; }
            }
            catch { }
            return source;
        }

        private static IDictionary<string, object> NativeWindows(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var foreground = GetForegroundWindow();
            var mainWindow = dte.MainWindow.HWnd;
            var windows = new List<IDictionary<string, object>>();
            var seen = new HashSet<IntPtr>();

            EnumWindows((hwnd, parameter) =>
            {
                GetWindowThreadProcessId(hwnd, out var ownerProcessId);
                if (ownerProcessId != processId || !IsWindowVisible(hwnd)) return true;
                seen.Add(hwnd);
                windows.Add(DescribeNativeWindow(hwnd));
                return true;
            }, IntPtr.Zero);

            if (mainWindow != IntPtr.Zero && !seen.Contains(mainWindow))
                windows.Add(DescribeNativeWindow(mainWindow));

            return new Dictionary<string, object>
            {
                ["activeWindowHandle"] = foreground == IntPtr.Zero ? null : UnsignedHandle(foreground),
                ["windows"] = windows
            };
        }

        private static IDictionary<string, object> DescribeNativeWindow(IntPtr hwnd)
        {
            return new Dictionary<string, object> { ["handle"] = UnsignedHandle(hwnd) };
        }

        private static string UnsignedHandle(IntPtr hwnd) => unchecked((ulong)hwnd.ToInt64()).ToString();

        internal static void Handle(string raw, BridgeService bridge, DTE dte, BridgeOptions options, FastBridgePackage package)
        {
            string id = null;
            try
            {
                var message = JObject.Parse(raw); id = (string)message["id"];
                var type = (string)message["type"] ?? "";
                var target = message["target"] as JObject ?? new JObject();
                var payload = message["payload"] as JObject ?? new JObject();
                var mutating = type == "command.navigate" || type == "command.select" || type == "command.find" || type == "command.requestFocus" || type == "command.fullScreen" || type == "command.copyResourcePath" || type == "command.queryPythonPackages";
                var targetInstance = (string)target["instanceId"];
                if (mutating && string.IsNullOrWhiteSpace(targetInstance)) throw new ArgumentException("target.instanceId is required for this command");
                if (targetInstance != null && targetInstance != bridge.InstanceId) return;
                if (type == "command.ping") { _ = bridge.SendAsync("command.result", Success(type, null), id); return; }
                var commandId = id;
                package.JoinableTaskFactory.RunAsync(async () =>
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try
                    {
                        object data;
                        switch (type)
                        {
                            case "command.getState": data = SnapshotCollector.Collect(dte, options); break;
                            case "command.getInputable": data = SnapshotCollector.CurrentInputable(dte, options); break;
                            case "command.getObjectInfo": data = SnapshotCollector.CurrentObjectInfo(dte, options); break;
                            case "command.getSelectedText": data = CurrentSelectedText(dte, options); break;
                            case "command.isFullScreen": data = IsFullScreenState(dte); break;
                            case "command.listProjects": data = Projects(dte).Select(p => p.ToDictionary()).ToList(); break;
                            case "command.navigate": data = Navigate(dte, payload, target, false); break;
                            case "command.select": data = Navigate(dte, payload, target, true); break;
                            case "command.find": data = Find(dte, payload, target); break;
                            case "command.requestFocus": data = RequestFocus(dte); break;
                            case "command.fullScreen": data = FullScreen(dte); break;
                            case "command.copyResourcePath": data = CopyResourcePath(dte); break;
                            case "command.queryPythonPackages": data = await QueryPythonPackages(dte, payload, target); break;
                            default: await bridge.SendAsync("command.result", Error(type, "UNKNOWN_COMMAND", "Unsupported command: " + type), commandId); return;
                        }
                        await bridge.SendAsync("command.result", Success(type, data), commandId);
                    }
                    catch (CommandException ex) { await bridge.SendAsync("command.result", Error(type, ex.Code, ex.Message), commandId); }
                    catch (Exception ex) when (type == "command.queryPythonPackages") { await bridge.SendAsync("command.result", Error(type, "PYTHON_QUERY_FAILED", ex.Message), commandId); }
                    catch (FileNotFoundException ex) { await bridge.SendAsync("command.result", Error(type, type == "command.find" ? "FIND_FAILED" : "NAVIGATION_FAILED", ex.Message), commandId); }
                    catch (ArgumentException ex) when (type == "command.find" && (bool?)payload["regex"] == true) { await bridge.SendAsync("command.result", Error(type, "INVALID_QUERY", ex.Message), commandId); }
                    catch (Exception ex) { await bridge.SendAsync("command.result", Error(type, type == "command.find" ? "FIND_FAILED" : type.StartsWith("command.") ? "NAVIGATION_FAILED" : "INVALID_MESSAGE", ex.Message), commandId); }
                });
            }
            catch (Exception ex) { _ = bridge.SendAsync("command.result", Error("unknown", "INVALID_MESSAGE", ex.Message), id); }
        }

        private static object Navigate(DTE dte, JObject payload, JObject target, bool select)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var resolved = Resolve(dte, (string)payload["path"], target);
            dte.ItemOperations.OpenFile(resolved.path, EnvDTE.Constants.vsViewKindTextView);
            var text = dte.ActiveDocument?.Object("TextDocument") as TextDocument ?? throw new InvalidOperationException("File is not a text editor");
            var selection = text.Selection;
            var coordinateBase = (int?)payload["coordinateBase"] ?? 0;
            if (select)
            {
                var start = payload["start"] as JObject ?? throw new ArgumentException("Selection start is required");
                var end = payload["end"] as JObject ?? throw new ArgumentException("Selection end is required");
                selection.MoveToLineAndOffset(Math.Max(1, ((int?)start["line"] ?? coordinateBase) - coordinateBase + 1), Math.Max(1, ((int?)start["column"] ?? coordinateBase) - coordinateBase + 1), false);
                selection.MoveToLineAndOffset(Math.Max(1, ((int?)end["line"] ?? coordinateBase) - coordinateBase + 1), Math.Max(1, ((int?)end["column"] ?? coordinateBase) - coordinateBase + 1), true);
            }
            else if (payload["line"] != null || payload["column"] != null)
            {
                var line = payload["line"] == null ? selection.ActivePoint.Line : Math.Max(1, (int)payload["line"] - coordinateBase + 1);
                var column = payload["column"] == null ? selection.ActivePoint.LineCharOffset : Math.Max(1, (int)payload["column"] - coordinateBase + 1);
                selection.MoveToLineAndOffset(line, column, false);
            }
            FocusMainWindow(dte);
            return new Dictionary<string, object> { ["path"] = resolved.path, ["line"] = selection.ActivePoint.Line - 1, ["column"] = selection.ActivePoint.LineCharOffset - 1 };
        }

        private const string PythonPackageQuery =
            "import importlib.util,importlib.metadata,json,os,sys\n" +
            "items={}\n" +
            "for import_name,distribution_name in json.load(sys.stdin):\n" +
            " installed=False\n" +
            " version=None\n" +
            " try: installed=importlib.util.find_spec(import_name) is not None\n" +
            " except (ImportError,ModuleNotFoundError,ValueError): pass\n" +
            " try: version=importlib.metadata.version(distribution_name); installed=True\n" +
            " except importlib.metadata.PackageNotFoundError: pass\n" +
            " items[import_name]={'installed':installed,**({'version':version} if version else {})}\n" +
            " environment_type='conda' if os.environ.get('CONDA_PREFIX') else ('venv' if sys.prefix != getattr(sys,'base_prefix',sys.prefix) else 'system')\n" +
            " print(json.dumps({'interpreter':sys.executable,'version':'.'.join(str(part) for part in sys.version_info[:3]),'environmentType':environment_type,'packages':items}))";

        private static Task<object> QueryPythonPackages(DTE dte, JObject payload, JObject target)
        {
            var requested = payload["packages"] as JArray;
            var packages = new List<Tuple<string, string>>();
            if (requested != null)
            {
                foreach (var item in requested)
                {
                    var importName = item.Type == JTokenType.String ? (string)item : (string)item["importName"];
                    var distributionName = item.Type == JTokenType.String ? importName : (string)item["distributionName"] ?? importName;
                    if (string.IsNullOrWhiteSpace(importName) || string.IsNullOrWhiteSpace(distributionName)
                        || !Regex.IsMatch(importName, @"^[A-Za-z_][A-Za-z0-9_.-]*$")
                        || !Regex.IsMatch(distributionName, @"^[A-Za-z_][A-Za-z0-9_.-]*$")
                        || packages.Any(value => value.Item1 == importName)) continue;
                    packages.Add(Tuple.Create(importName, distributionName));
                }
            }
            if (packages.Count == 0) throw new CommandException("INVALID_QUERY", "payload.packages must contain at least one package name");

            var project = ResolveProjectForQuery(dte, target);
            var interpreter = FindPythonInterpreter(dte, project);
            if (interpreter == null) throw new CommandException("PYTHON_INTERPRETER_NOT_FOUND", "The targeted project has no configured Python interpreter");
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(packages.Select(value => new[] { value.Item1, value.Item2 }));
            return Task.Run<object>(() => RunPythonQuery(interpreter, project?.BasePath, json));
        }

        private static ProjectInfo ResolveProjectForQuery(DTE dte, JObject target)
        {
            var requestedId = (string)target["projectId"];
            var requestedBase = (string)target["projectBasePath"];
            var projects = Projects(dte);
            var selected = projects.FirstOrDefault(project =>
                (string.IsNullOrWhiteSpace(requestedId) || project.Id == requestedId) &&
                (string.IsNullOrWhiteSpace(requestedBase) || string.Equals(Path.GetFullPath(project.BasePath), Path.GetFullPath(requestedBase), StringComparison.OrdinalIgnoreCase)));
            if (selected != null) return selected;
            return ProjectForFile(dte, dte.ActiveDocument?.FullName);
        }

        private static string FindPythonInterpreter(DTE dte, ProjectInfo project)
        {
            var containingProject = dte.ActiveDocument?.ProjectItem?.ContainingProject;
            foreach (var propertyName in new[] { "PythonInterpreterPath", "InterpreterPath", "PythonPath" })
            {
                try
                {
                    var value = containingProject?.Properties?.Item(propertyName)?.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(value) && File.Exists(value)) return value;
                }
                catch { }
            }
            if (project == null) return null;
            foreach (var candidate in new[]
            {
                Path.Combine(project.BasePath, ".venv", "Scripts", "python.exe"),
                Path.Combine(project.BasePath, "venv", "Scripts", "python.exe"),
                Path.Combine(project.BasePath, ".venv", "bin", "python"),
                Path.Combine(project.BasePath, "venv", "bin", "python")
            }) if (File.Exists(candidate)) return candidate;
            return null;
        }

        private static JObject RunPythonQuery(string interpreter, string workingDirectory, string packageJson)
        {
            var encodedQuery = Convert.ToBase64String(Encoding.UTF8.GetBytes(PythonPackageQuery));
            var startInfo = new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = "-c \"import base64;exec(base64.b64decode('" + encodedQuery + "'))\"",
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
            {
                if (!process.Start()) throw new InvalidOperationException("Unable to start the configured Python interpreter");
                process.StandardInput.Write(packageJson);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Python package query timed out");
                }
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Python interpreter returned no package result");
                return JObject.Parse(output.Trim());
            }
        }

        private static object Find(DTE dte, JObject payload, JObject target)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var query = (string)payload["query"];
            if (string.IsNullOrEmpty(query)) throw new ArgumentException("payload.query is required");
            string path;
            if (string.IsNullOrWhiteSpace((string)payload["path"])) path = dte.ActiveDocument?.FullName;
            else path = Resolve(dte, (string)payload["path"], target).path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("File was not found inside an open project");
            var content = File.ReadAllText(path);
            var start = Math.Max(0, Math.Min((int?)payload["startOffset"] ?? 0, content.Length));
            var sensitive = (bool?)payload["caseSensitive"] ?? false;
            var regex = (bool?)payload["regex"] ?? false;
            int index, length;
            if (regex)
            {
                var match = Regex.Match(content.Substring(start), query, sensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                if (!match.Success) throw new CommandException("NOT_FOUND", "No match found");
                index = start + match.Index; length = match.Length;
            }
            else { index = content.IndexOf(query, start, sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase); length = query.Length; }
            if (index < 0) throw new CommandException("NOT_FOUND", "No match found");
            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            var text = (TextDocument)dte.ActiveDocument.Object("TextDocument");
            var a = text.StartPoint.CreateEditPoint(); a.MoveToAbsoluteOffset(index + 1);
            var b = text.StartPoint.CreateEditPoint(); b.MoveToAbsoluteOffset(index + length + 1);
            var selection = text.Selection; selection.MoveToPoint(a, false); selection.MoveToPoint(b, (bool?)payload["select"] ?? false);
            var line = a.Line - 1; var column = a.LineCharOffset - 1;
            FocusMainWindow(dte);
            return new Dictionary<string, object> { ["path"] = path, ["startOffset"] = index, ["endOffset"] = index + length, ["line"] = line, ["column"] = column, ["lineNumber"] = line + 1, ["columnNumber"] = column + 1, ["caseSensitive"] = sensitive, ["regex"] = regex, ["selected"] = (bool?)payload["select"] ?? false };
        }

        private static object RequestFocus(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte.ActiveDocument == null) throw new InvalidOperationException("No active text editor");
            FocusMainWindow(dte); dte.ActiveDocument.Activate();
            return new Dictionary<string, object> { ["project"] = ProjectForFile(dte, dte.ActiveDocument.FullName)?.Name };
        }

        internal static bool? IsFullScreen(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var command = dte.Commands.Item("View.FullScreen");
                try
                {
                    var checkedValue = command.GetType().InvokeMember(
                        "IsChecked", BindingFlags.GetProperty, null, command, null);
                    if (checkedValue is bool isChecked) return isChecked;
                }
                catch { }
                var statusValue = command.GetType().InvokeMember(
                    "OleStatus", BindingFlags.GetProperty, null, command, null);
                var status = Convert.ToInt32(statusValue);
                if ((status & 1) == 0) return null;
                // OLECMDF_LATCHED (0x4) means the toggle command is currently on.
                return (status & 4) != 0;
            }
            catch { return null; }
        }

        private static object FullScreen(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var fullScreen = IsFullScreen(dte);
            if (fullScreen == false)
            {
                dte.ExecuteCommand("View.FullScreen");
                fullScreen = IsFullScreen(dte);
            }
            return new Dictionary<string, object> { ["fullScreen"] = fullScreen };
        }

        private static object IsFullScreenState(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new Dictionary<string, object> { ["fullScreen"] = IsFullScreen(dte) };
        }

        private static string CurrentSelectedText(DTE dte, BridgeOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!options.SendText) return null;
            var text = dte.ActiveDocument?.Object("TextDocument") as TextDocument;
            return text?.Selection?.Text ?? (text == null ? null : string.Empty);
        }

        private static object CopyResourcePath(DTE dte)
        {
            var paths = SelectedSolutionExplorerFiles(dte, out var reason);
            if (paths.Count == 0)
                return new Dictionary<string, object> { ["handled"] = false, ["reason"] = reason };
            var result = new Dictionary<string, object> { ["handled"] = true, ["paths"] = paths };
            if (paths.Count == 1) result["path"] = paths[0];
            return result;
        }

        private static List<string> SelectedSolutionExplorerFiles(DTE dte, out string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            reason = "NO_RESOURCE";
            if (!IsSolutionExplorerFocused(dte))
            {
                reason = "NOT_SOLUTION_EXPLORER_FOCUS";
                return new List<string>();
            }

            try
            {
                var solutionWindow = dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer);
                var hierarchy = solutionWindow?.Object as UIHierarchy;
                var selectedHierarchyItems = hierarchy?.SelectedItems as Array;
                var paths = new List<string>();
                if (selectedHierarchyItems != null)
                {
                    foreach (var selectedItem in selectedHierarchyItems)
                    {
                        var projectItem = (selectedItem as UIHierarchyItem)?.Object as ProjectItem;
                        if (projectItem == null || projectItem.FileCount < 1) continue;
                        var path = ExistingFilePath(projectItem.FileNames[1]);
                        if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
                    }
                }
                if (paths.Count > 0) return paths;

                var selected = dte.SelectedItems;
                if (selected != null)
                {
                    for (var index = 1; index <= selected.Count; index++)
                    {
                        var item = selected.Item(index)?.ProjectItem;
                        if (item == null || item.FileCount < 1) continue;
                        var path = ExistingFilePath(item.FileNames[1]);
                        if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
                    }
                }
                if (paths.Count > 0) return paths;

                var hierarchyPath = SelectedHierarchyFile();
                if (!string.IsNullOrWhiteSpace(hierarchyPath)) paths.Add(hierarchyPath);
                return paths;
            }
            catch { return new List<string>(); }
        }

        private static string ExistingFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) || Directory.Exists(fullPath) ? fullPath : null;
            }
            catch { return null; }
        }

        private static string SelectedHierarchyFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var monitor = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            if (monitor == null) return null;

            IntPtr hierarchyPointer = IntPtr.Zero;
            IntPtr selectionContainerPointer = IntPtr.Zero;
            try
            {
                var hr = monitor.GetCurrentSelection(
                    out hierarchyPointer,
                    out var itemId,
                    out var multiItemSelect,
                    out selectionContainerPointer);
                if (hr != VSConstants.S_OK || hierarchyPointer == IntPtr.Zero ||
                    itemId == VSConstants.VSITEMID_NIL ||
                    itemId == VSConstants.VSITEMID_ROOT ||
                    itemId == VSConstants.VSITEMID_SELECTION) return null;

                if (multiItemSelect != null) return null;
                var hierarchy = Marshal.GetObjectForIUnknown(hierarchyPointer) as IVsHierarchy;
                if (hierarchy == null) return null;

                if (hierarchy is IVsProject project &&
                    project.GetMkDocument(itemId, out var projectPath) == VSConstants.S_OK)
                {
                    var existingProjectPath = ExistingFilePath(projectPath);
                    if (existingProjectPath != null) return existingProjectPath;
                }

                return hierarchy.GetCanonicalName(itemId, out var canonicalPath) == VSConstants.S_OK
                    ? ExistingFilePath(canonicalPath)
                    : null;
            }
            catch { return null; }
            finally
            {
                if (hierarchyPointer != IntPtr.Zero) Marshal.Release(hierarchyPointer);
                if (selectionContainerPointer != IntPtr.Zero) Marshal.Release(selectionContainerPointer);
            }
        }

        private static bool IsSolutionExplorerFocused(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var activeWindow = dte.ActiveWindow;
                var solutionWindow = dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer);
                if (activeWindow != null && solutionWindow != null &&
                    (activeWindow == solutionWindow ||
                     string.Equals(activeWindow.ObjectKind, solutionWindow.ObjectKind, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }

            try
            {
                var focus = GetFocusedWindow();
                var explorer = dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer).HWnd;
                return focus != IntPtr.Zero && explorer != IntPtr.Zero &&
                    (focus == explorer || IsChild(explorer, focus) || IsDescendant(focus, explorer));
            }
            catch { return false; }
        }

        private static IntPtr GetFocusedWindow()
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                var threadId = GetWindowThreadProcessId(foreground, out _);
                var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
                if (threadId != 0 && GetGUIThreadInfo(threadId, ref info) && info.FocusWindow != IntPtr.Zero)
                    return info.FocusWindow;
            }
            return GetFocus();
        }

        private static bool IsDescendant(IntPtr child, IntPtr ancestor)
        {
            for (var current = child; current != IntPtr.Zero; current = GetParent(current))
                if (current == ancestor) return true;
            return false;
        }

        private static void FocusMainWindow(DTE dte) { ThreadHelper.ThrowIfNotOnUIThread(); var hwnd = dte.MainWindow.HWnd; ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); dte.MainWindow.Activate(); }

        private static (string path, ProjectInfo project) Resolve(DTE dte, string requested, JObject target)
        {
            if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("payload.path is required");
            if (Uri.TryCreate(requested, UriKind.Absolute, out var uri) && uri.IsFile) requested = uri.LocalPath;
            foreach (var project in Projects(dte))
            {
                if (target["projectId"] != null && (string)target["projectId"] != project.Id) continue;
                if (target["projectBasePath"] != null && !SamePath((string)target["projectBasePath"], project.BasePath)) continue;
                var candidate = Path.IsPathRooted(requested) ? Path.GetFullPath(requested) : Path.GetFullPath(Path.Combine(project.BasePath, requested));
                if (!IsInside(project.BasePath, candidate) || !File.Exists(candidate)) continue;
                return (candidate, project);
            }
            throw new FileNotFoundException("File was not found inside an open project");
        }

        internal static ProjectInfo ProjectForFile(DTE dte, string path) { ThreadHelper.ThrowIfNotOnUIThread(); return string.IsNullOrWhiteSpace(path) ? null : Projects(dte).OrderByDescending(p => p.BasePath.Length).FirstOrDefault(p => IsInside(p.BasePath, path)); }

        internal static List<ProjectInfo> Projects(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<ProjectInfo>();
            try
            {
                foreach (Project project in dte.Solution.Projects)
                {
                    string file = null; try { file = project.FullName; } catch { }
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    var root = Directory.Exists(file) ? file : Path.GetDirectoryName(file);
                    if (!string.IsNullOrWhiteSpace(root)) Add(result, project.Name, root);
                }
                var solution = dte.Solution.FullName;
                if (!string.IsNullOrWhiteSpace(solution)) Add(result, Path.GetFileNameWithoutExtension(solution), Path.GetDirectoryName(solution));
            }
            catch { }
            return result.GroupBy(p => p.BasePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        private static void Add(List<ProjectInfo> list, string name, string root) { root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); list.Add(new ProjectInfo { Id = ProjectId(root), Name = name, BasePath = root }); }
        internal static string RelativePath(string root, string path) => new Uri(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(path)).ToString().Replace('/', Path.DirectorySeparatorChar);
        private static bool IsInside(string root, string path) { var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; var p = Path.GetFullPath(path); return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) || SamePath(root, path); }
        private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static string ProjectId(string path)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
                bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
                Swap(bytes, 0, 3); Swap(bytes, 1, 2); Swap(bytes, 4, 5); Swap(bytes, 6, 7);
                return new Guid(bytes).ToString();
            }
        }
        private static void Swap(byte[] b, int x, int y) { var t = b[x]; b[x] = b[y]; b[y] = t; }
        private static object Success(string command, object data) => new Dictionary<string, object> { ["success"] = true, ["command"] = command, ["data"] = data };
        private static object Error(string command, string code, string message) => new Dictionary<string, object> { ["success"] = false, ["command"] = command ?? "unknown", ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message ?? "Unknown error" } };
    }
}
