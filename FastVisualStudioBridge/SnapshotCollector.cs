using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastVisualStudioBridge
{
    internal static class SnapshotCollector
    {
        private static string lastEditorKey;
        private static long lastEditorOpenedAt;

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

        internal static IDictionary<string, object> Collect(DTE dte, BridgeOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var editorOwnsFocus = ActiveEditorOwnsFocus(dte);
            return new Dictionary<string, object>
            {
                ["focus"] = DescribeFocus(editorOwnsFocus),
                ["activeEditor"] = DescribeEditor(dte, options, editorOwnsFocus),
                ["editorOpenedAt"] = EditorOpenedAt(dte),
                ["fullScreen"] = BridgeCommands.IsFullScreen(dte)
            };
        }

        internal static void MarkEditorOpened(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var key = CurrentEditorKey(dte);
            if (string.IsNullOrEmpty(key)) return;
            lastEditorKey = key;
            lastEditorOpenedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static long EditorOpenedAt(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var key = CurrentEditorKey(dte);
            if (string.IsNullOrEmpty(key)) return 0;
            if (!string.Equals(key, lastEditorKey, StringComparison.OrdinalIgnoreCase))
            {
                lastEditorKey = key;
                lastEditorOpenedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return lastEditorOpenedAt;
        }

        internal static long CurrentEditorOpenedAt(DTE dte) => EditorOpenedAt(dte);

        private static string CurrentEditorKey(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var path = dte.ActiveDocument?.FullName;
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            }
            catch { return null; }
        }

        private static object DescribeFocus(bool editorOwnsFocus)
        {
            var hwnd = GetFocus();
            if (hwnd == IntPtr.Zero) return new Dictionary<string, object> { ["type"] = "none" };
            var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            var result = new Dictionary<string, object>
            {
                ["type"] = editorOwnsFocus ? "editor" : "component", ["className"] = name.ToString(),
                ["nativeHandle"] = unchecked((ulong)hwnd.ToInt64()).ToString()
            };
            if (GetWindowRect(hwnd, out var r)) result["bounds"] = new Dictionary<string, object>
            { ["screenX"] = r.Left, ["screenY"] = r.Top, ["width"] = r.Right - r.Left, ["height"] = r.Bottom - r.Top };
            return result;
        }

        private static bool ActiveEditorOwnsFocus(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var focus = GetFocus();
                var editor = dte.ActiveDocument?.ActiveWindow?.HWnd ?? IntPtr.Zero;
                return focus != IntPtr.Zero && editor != IntPtr.Zero && IsDescendant(focus, editor);
            }
            catch { return false; }
        }

        private static bool IsDescendant(IntPtr child, IntPtr ancestor)
        {
            for (var current = child; current != IntPtr.Zero; current = GetParent(current))
                if (current == ancestor) return true;
            return false;
        }

        private static object DescribeEditor(DTE dte, BridgeOptions options, bool ownsFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Document document;
            TextDocument text;
            try { document = dte.ActiveDocument; text = document?.Object("TextDocument") as TextDocument; }
            catch { return null; }
            if (document == null || text == null) return null;
            var selection = text.Selection;
            var point = selection.ActivePoint;
            var anchor = selection.AnchorPoint;
            var path = document.FullName;
            var project = BridgeCommands.ProjectForFile(dte, path);
            var offset = Math.Max(0, point.AbsoluteCharOffset - 1);
            var startOffset = Math.Max(0, Math.Min(point.AbsoluteCharOffset, anchor.AbsoluteCharOffset) - 1);
            var endOffset = Math.Max(startOffset, Math.Max(point.AbsoluteCharOffset, anchor.AbsoluteCharOffset) - 1);
            var hasSelection = startOffset != endOffset;
            var editor = new Dictionary<string, object>
            {
                ["ownsFocus"] = ownsFocus,
                ["editable"] = !document.ReadOnly,
                ["file"] = new Dictionary<string, object>
                {
                    ["name"] = Path.GetFileName(path), ["path"] = path, ["url"] = new Uri(path).AbsoluteUri,
                    ["writable"] = !document.ReadOnly, ["fileType"] = Path.GetExtension(path).TrimStart('.'),
                    ["projectRelativePath"] = project == null ? null : BridgeCommands.RelativePath(project.BasePath, path)
                },
                ["caret"] = new Dictionary<string, object>
                {
                    ["offset"] = offset, ["line"] = point.Line - 1, ["column"] = point.LineCharOffset - 1,
                    ["lineNumber"] = point.Line, ["columnNumber"] = point.LineCharOffset,
                    ["visualLine"] = point.DisplayColumn < 0 ? point.Line - 1 : point.Line - 1,
                    ["visualColumn"] = Math.Max(0, point.DisplayColumn - 1)
                },
                ["selection"] = new Dictionary<string, object>
                {
                    ["hasSelection"] = hasSelection, ["startOffset"] = startOffset, ["endOffset"] = endOffset,
                    ["text"] = options.SendText && hasSelection ? selection.Text : null
                },
                ["carets"] = new[] { new Dictionary<string, object>
                {
                    ["primary"] = true, ["offset"] = offset, ["line"] = point.Line - 1,
                    ["column"] = point.LineCharOffset - 1, ["selectionStart"] = startOffset,
                    ["selectionEnd"] = endOffset, ["selectedText"] = options.SendText && hasSelection ? selection.Text : null
                }}
            };
            if (project != null) editor["project"] = project.ToDictionary();
            if (options.SendText)
            {
                try
                {
                    var all = text.StartPoint.CreateEditPoint().GetText(text.EndPoint);
                    var n = Math.Max(0, Math.Min(5000, options.ContextCharacters));
                    var safe = Math.Min(offset, all.Length);
                    var lines = all.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    editor["textContext"] = new Dictionary<string, object>
                    {
                        ["currentLine"] = point.Line > 0 && point.Line <= lines.Length ? lines[point.Line - 1] : "",
                        ["beforeCaret"] = all.Substring(Math.Max(0, safe - n), Math.Min(n, safe)),
                        ["afterCaret"] = all.Substring(safe, Math.Min(n, all.Length - safe))
                    };
                }
                catch { }
            }
            return editor;
        }
    }
}
