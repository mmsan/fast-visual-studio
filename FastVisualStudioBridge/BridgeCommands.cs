using EnvDTE;
using Microsoft.VisualStudio.Shell;
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

        internal sealed class ProjectInfo
        {
            internal string Id, Name, BasePath;
            internal IDictionary<string, object> ToDictionary() => new Dictionary<string, object>
            { ["projectId"] = Id, ["name"] = Name, ["basePath"] = BasePath };
        }

        internal static object Identity(DTE dte, string installationId, string instanceId, string connectionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hwnd = dte.MainWindow.HWnd;
            var handle = unchecked((ulong)hwnd.ToInt64()).ToString();
            return new Dictionary<string, object>
            {
                ["client"] = "fast-visual-studio-bridge", ["clientId"] = installationId,
                ["installationId"] = installationId, ["instanceId"] = instanceId, ["connectionId"] = connectionId,
                ["ide"] = "Microsoft Visual Studio", ["build"] = dte.Version,
                ["pluginVersion"] = Assembly.GetExecutingAssembly().GetName().Version.ToString(3),
                ["projects"] = Projects(dte).Select(p => p.ToDictionary()).ToList(),
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["nativeWindows"] = new Dictionary<string, object>
                {
                    ["supported"] = true, ["activeWindowHandle"] = handle,
                    ["windows"] = new[] { new Dictionary<string, object>
                    { ["handle"] = handle, ["handleHex"] = "0x" + hwnd.ToInt64().ToString("X"), ["active"] = GetForegroundWindow() == hwnd, ["title"] = dte.MainWindow.Caption } },
                    ["format"] = "unsigned decimal string; handleHex is provided for diagnostics"
                },
                ["capabilities"] = new[] { "state", "navigate", "select", "find", "requestFocus", "listProjects" }
            };
        }

        internal static object Source(DTE dte, string installationId, string instanceId, string connectionId, object payload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var source = new Dictionary<string, object>
            {
                ["installationId"] = installationId, ["instanceId"] = instanceId, ["connectionId"] = connectionId,
                ["projects"] = Projects(dte).Select(p => p.ToDictionary()).ToList()
            };
            try
            {
                var file = dte.ActiveDocument?.FullName;
                var project = ProjectForFile(dte, file);
                if (project != null) { source["projectId"] = project.Id; source["projectName"] = project.Name; source["projectBasePath"] = project.BasePath; }
            }
            catch { }
            return source;
        }

        internal static void Handle(string raw, BridgeService bridge, DTE dte, BridgeOptions options, FastBridgePackage package)
        {
            string id = null;
            try
            {
                var message = JObject.Parse(raw); id = (string)message["id"];
                var type = (string)message["type"] ?? "";
                var target = message["target"] as JObject ?? new JObject();
                var payload = message["payload"] as JObject ?? new JObject();
                var mutating = type == "command.navigate" || type == "command.select" || type == "command.find" || type == "command.requestFocus";
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
                            case "command.listProjects": data = Projects(dte).Select(p => p.ToDictionary()).ToList(); break;
                            case "command.navigate": data = Navigate(dte, payload, target, false); break;
                            case "command.select": data = Navigate(dte, payload, target, true); break;
                            case "command.find": data = Find(dte, payload, target); break;
                            case "command.requestFocus": data = RequestFocus(dte); break;
                            default: await bridge.SendAsync("command.result", Error(type, "UNKNOWN_COMMAND", "Unsupported command: " + type), commandId); return;
                        }
                        await bridge.SendAsync("command.result", Success(type, data), commandId);
                    }
                    catch (CommandException ex) { await bridge.SendAsync("command.result", Error(type, ex.Code, ex.Message), commandId); }
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
            dte.ItemOperations.OpenFile(resolved.path, Constants.vsViewKindTextView);
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
            dte.ItemOperations.OpenFile(path, Constants.vsViewKindTextView);
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
