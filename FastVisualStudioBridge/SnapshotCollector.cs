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
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

        internal static IDictionary<string, object> Collect(DTE dte, BridgeOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new Dictionary<string, object>
            {
                ["focus"] = DescribeFocus(),
                ["activeEditor"] = DescribeEditor(dte, options)
            };
        }

        private static object DescribeFocus()
        {
            var hwnd = GetFocus();
            if (hwnd == IntPtr.Zero) return new Dictionary<string, object> { ["type"] = "none" };
            var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            var result = new Dictionary<string, object>
            {
                ["type"] = "component", ["className"] = name.ToString(),
                ["nativeHandle"] = unchecked((ulong)hwnd.ToInt64()).ToString()
            };
            if (GetWindowRect(hwnd, out var r)) result["bounds"] = new Dictionary<string, object>
            { ["screenX"] = r.Left, ["screenY"] = r.Top, ["width"] = r.Right - r.Left, ["height"] = r.Bottom - r.Top };
            return result;
        }

        private static object DescribeEditor(DTE dte, BridgeOptions options)
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
                ["ownsFocus"] = true,
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
