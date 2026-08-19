using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FastVisualStudioBridge
{
    internal static class SnapshotCollector
    {
        private static string lastEditorKey;
        private static long lastEditorOpenedAt;

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
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

        internal static IDictionary<string, object> CollectStatus(DTE dte, BridgeOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = Collect(dte, options);
            var activeEditor = snapshot["activeEditor"];
            return new Dictionary<string, object>
            {
                ["inputable"] = IsInputable(snapshot["focus"], activeEditor),
                ["focus"] = snapshot["focus"],
                ["activeEditor"] = activeEditor,
                ["objectInfo"] = ObjectInfo(activeEditor)
            };
        }

        internal static bool CurrentInputable(DTE dte, BridgeOptions options)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var editorOwnsFocus = ActiveEditorOwnsFocus(dte);
            var hwnd = GetFocus();
            if (hwnd == IntPtr.Zero) return false;
            var input = DescribeFocusedInput(hwnd, editorOwnsFocus);
            return input != null && input.TryGetValue("editable", out var editable) && Convert.ToBoolean(editable);
        }

        internal static bool CurrentEditorOwnsFocus(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ActiveEditorOwnsFocus(dte);
        }

        internal static string CurrentContext(DTE dte, BridgeOptions options)
        {
            var snapshot = Collect(dte, options);
            var editor = snapshot["activeEditor"] as IDictionary<string, object>;
            return editor != null && editor.TryGetValue("context", out var context) ? context as string : null;
        }

        internal static object CurrentObjectInfo(DTE dte, BridgeOptions options)
        {
            return CollectStatus(dte, options)["objectInfo"];
        }

        private static bool IsInputable(object focusValue, object activeEditorValue)
        {
            var focus = focusValue as IDictionary<string, object>;
            if (focus == null || !IsAvailable(focus)) return false;
            var input = focus.TryGetValue("input", out var inputValue)
                ? inputValue as IDictionary<string, object> : null;
            if (input != null && input.TryGetValue("editable", out var editable)) return Convert.ToBoolean(editable);

            var type = focus.TryGetValue("type", out var focusType) ? focusType as string : null;

            var editor = activeEditorValue as IDictionary<string, object>;
            if (!string.Equals(type, "editor", StringComparison.Ordinal) || editor == null) return false;
            var ownsFocus = editor.TryGetValue("ownsFocus", out var owns) && Convert.ToBoolean(owns);
            var editableEditor = editor.TryGetValue("editable", out var editorEditable) && Convert.ToBoolean(editorEditable);
            var writable = true;
            if (editor.TryGetValue("file", out var fileValue) && fileValue is IDictionary<string, object> file &&
                file.TryGetValue("writable", out var fileWritable)) writable = Convert.ToBoolean(fileWritable);
            return ownsFocus && (editableEditor || !editor.ContainsKey("editable") && writable);
        }

        private static bool IsAvailable(IDictionary<string, object> focus)
        {
            return (!focus.TryGetValue("enabled", out var enabled) || Convert.ToBoolean(enabled)) &&
                   (!focus.TryGetValue("visible", out var visible) || Convert.ToBoolean(visible)) &&
                   (!focus.TryGetValue("showing", out var showing) || Convert.ToBoolean(showing));
        }

        private static object ObjectInfo(object activeEditorValue)
        {
            var editor = activeEditorValue as IDictionary<string, object>;
            if (editor == null) return null;
            var result = new Dictionary<string, object>();
            foreach (var key in new[] { "project", "file", "context", "expressionContext" })
                if (editor.ContainsKey(key)) result[key] = editor[key];
            return result;
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
            var input = DescribeFocusedInput(hwnd, editorOwnsFocus);
            var result = new Dictionary<string, object>
            {
                ["type"] = editorOwnsFocus ? "editor" : input != null ? "textInput" : "component", ["className"] = name.ToString(),
                ["nativeHandle"] = unchecked((ulong)hwnd.ToInt64()).ToString()
            };
            if (input != null) result["input"] = input;
            if (GetWindowRect(hwnd, out var r)) result["bounds"] = new Dictionary<string, object>
            { ["screenX"] = r.Left, ["screenY"] = r.Top, ["width"] = r.Right - r.Left, ["height"] = r.Bottom - r.Top };
            return result;
        }

        private static bool ActiveEditorOwnsFocus(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var textView = FastBridgePackage.Instance?.Bridge?.CurrentActiveTextView;
                if (textView != null && !textView.IsClosed && textView.HasAggregateFocus) return true;
                var focus = GetFocus();
                var editor = dte.ActiveDocument?.ActiveWindow?.HWnd ?? IntPtr.Zero;
                return focus != IntPtr.Zero && editor != IntPtr.Zero && IsDescendant(focus, editor);
            }
            catch { return false; }
        }

        private static IDictionary<string, object> DescribeFocusedInput(IntPtr hwnd, bool editorOwnsFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (editorOwnsFocus)
            {
                var document = FastBridgePackage.Instance?.Dte?.ActiveDocument;
                return new Dictionary<string, object> { ["editable"] = document != null && !document.ReadOnly, ["kind"] = "editor" };
            }

            var wpfInput = FocusedWpfInput();
            if (wpfInput != null) return wpfInput;

            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (!string.Equals(className.ToString(), "Edit", StringComparison.OrdinalIgnoreCase)) return null;
            var editable = IsWindowVisible(hwnd) && IsWindowEnabled(hwnd) &&
                SendMessage(hwnd, 0x00CF, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero; // EM_GETREADONLY
            return new Dictionary<string, object> { ["editable"] = editable, ["kind"] = "nativeEdit" };
        }

        private static IDictionary<string, object> FocusedWpfInput()
        {
            var current = Keyboard.FocusedElement as DependencyObject;
            while (current != null)
            {
                if (current is TextBoxBase textBox)
                    return new Dictionary<string, object> { ["editable"] = textBox.IsEnabled && textBox.IsVisible && !textBox.IsReadOnly, ["kind"] = "textBox" };
                if (current is PasswordBox passwordBox)
                    return new Dictionary<string, object> { ["editable"] = passwordBox.IsEnabled && passwordBox.IsVisible, ["kind"] = "passwordBox" };
                if (current is ComboBox comboBox && comboBox.IsEditable)
                    return new Dictionary<string, object> { ["editable"] = comboBox.IsEnabled && comboBox.IsVisible, ["kind"] = "comboBox" };
                var parent = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current) : null;
                current = parent ?? LogicalTreeHelper.GetParent(current);
            }
            return null;
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
            string allText = null;
            try { allText = text.StartPoint.CreateEditPoint().GetText(text.EndPoint); } catch { }
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
            editor["context"] = SyntaxContextAt(allText ?? string.Empty, offset, Path.GetExtension(path));
            var expression = ExtractExpression(allText ?? string.Empty, offset);
            var codeProject = document.ProjectItem?.ContainingProject;
            var expressionContext = ExpressionContext(dte, document, codeProject, expression, point);
            if (expressionContext != null) editor["expressionContext"] = expressionContext;
            if (options.SendText)
            {
                try
                {
                    var all = allText ?? string.Empty;
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

        private static IDictionary<string, object> ExpressionContext(
            DTE dte, Document document, Project project, string expression, TextPoint point)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            var result = new Dictionary<string, object>
            {
                ["expression"] = expression,
                ["kind"] = "reference"
            };
            try
            {
                var parts = expression.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                var resolved = ResolveExpression(project, document, parts, point);
                if (resolved == null) return result;

                result["kind"] = ElementKind(resolved);
                var type = TypeText(resolved);
                if (!string.IsNullOrWhiteSpace(type)) result["type"] = type;
                if (resolved is CodeFunction function)
                {
                    if (!string.IsNullOrWhiteSpace(type)) result["returnType"] = type;
                    var parameters = Parameters(function);
                    if (parameters.Count > 0) result["parameters"] = parameters;
                }
                var typeElement = TypeElement(resolved, project);
                var members = Members(typeElement);
                if (members.Count > 0) result["members"] = members;
            }
            catch
            {
                // CodeModel support differs by language and project system. Keep the
                // expression fallback when a language model is unavailable.
            }
            return result;
        }

        private static CodeElement ResolveExpression(Project project, Document document, string[] parts, TextPoint point)
        {
            if (project == null || parts == null || parts.Length == 0) return null;
            var current = FindNamedElement(project, document, parts[0], point);
            if (current == null) return null;
            for (var index = 1; index < parts.Length; index++)
            {
                var next = FindMember(TypeElement(current, project), parts[index]);
                if (next == null) break;
                current = next;
            }
            return current;
        }

        private static CodeElement FindNamedElement(Project project, Document document, string name, TextPoint point)
        {
            if (project == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                var containing = FindContaining(project.CodeModel?.CodeElements, point);
                var containingMatch = FindNamed(containing == null ? null : Children(containing), name);
                if (containingMatch != null) return containingMatch;
                return FindNamed(project.CodeModel?.CodeElements, name);
            }
            catch { return null; }
        }

        private static CodeElement FindContaining(CodeElements elements, TextPoint point)
        {
            if (elements == null || point == null) return null;
            CodeElement result = null;
            foreach (CodeElement element in elements)
            {
                if (!Contains(element, point)) continue;
                result = FindContaining(Children(element), point) ?? element;
            }
            return result;
        }

        private static bool Contains(CodeElement element, TextPoint point)
        {
            try
            {
                return element?.StartPoint != null && element.EndPoint != null &&
                    element.StartPoint.AbsoluteCharOffset <= point.AbsoluteCharOffset &&
                    point.AbsoluteCharOffset <= element.EndPoint.AbsoluteCharOffset;
            }
            catch { return false; }
        }

        private static CodeElement FindNamed(CodeElements elements, string name)
        {
            if (elements == null) return null;
            foreach (CodeElement element in elements)
            {
                if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase)) return element;
                var nested = FindNamed(Children(element), name);
                if (nested != null) return nested;
            }
            return null;
        }

        private static CodeElement FindMember(CodeElement type, string name)
        {
            var clean = Regex.Replace(name ?? string.Empty, @"\(.*\)$", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean)) return null;
            return FindNamed(Children(type), clean);
        }

        private static CodeElements Children(CodeElement element)
        {
            try { return element?.Children; }
            catch { return null; }
        }

        private static CodeElement TypeElement(CodeElement element, Project project)
        {
            if (element == null) return null;
            if (IsTypeElement(element)) return element;
            CodeTypeRef type = null;
            try
            {
                if (element is CodeFunction function) type = function.Type;
                else if (element is CodeProperty property) type = property.Type;
                else if (element is CodeVariable variable) type = variable.Type;
                else if (element is CodeParameter parameter) type = parameter.Type;
            }
            catch { }
            try
            {
                var codeType = type?.CodeType as CodeElement;
                if (codeType != null) return codeType;
            }
            catch { }
            var typeName = TypeText(type);
            if (string.IsNullOrWhiteSpace(typeName) || project?.CodeModel == null) return null;
            var simpleName = typeName.Split(new[] { '.', '<', '[', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
            return FindNamed(project.CodeModel.CodeElements, simpleName);
        }

        private static bool IsTypeElement(CodeElement element)
        {
            var kind = element?.Kind.ToString().ToLowerInvariant() ?? string.Empty;
            return kind.Contains("class") || kind.Contains("interface") || kind.Contains("struct") ||
                   kind.Contains("enum") || kind.Contains("delegate");
        }

        private static string ElementKind(CodeElement element)
        {
            var kind = element?.Kind.ToString().ToLowerInvariant() ?? string.Empty;
            if (kind.Contains("function")) return "method";
            if (kind.Contains("property")) return "property";
            if (kind.Contains("field")) return "property";
            if (kind.Contains("variable") || kind.Contains("parameter")) return "variable";
            if (kind.Contains("class") || kind.Contains("interface") || kind.Contains("struct") ||
                kind.Contains("enum") || kind.Contains("delegate")) return "type";
            return "reference";
        }

        private static string TypeText(CodeElement element)
        {
            try
            {
                if (element is CodeFunction function) return TypeText(function.Type);
                if (element is CodeProperty property) return TypeText(property.Type);
                if (element is CodeVariable variable) return TypeText(variable.Type);
                if (element is CodeParameter parameter) return TypeText(parameter.Type);
                if (IsTypeElement(element)) return element.FullName;
            }
            catch { }
            return null;
        }

        private static string TypeText(CodeTypeRef type)
        {
            try { return type?.AsString; }
            catch { return null; }
        }

        private static List<Dictionary<string, object>> Members(CodeElement type)
        {
            var result = new List<Dictionary<string, object>>();
            var children = Children(type);
            if (children == null) return result;
            foreach (CodeElement member in children)
            {
                var kind = ElementKind(member);
                if (kind == "reference" || result.Count >= 15) continue;
                var item = new Dictionary<string, object> { ["name"] = member.Name, ["kind"] = kind };
                var typeText = TypeText(member);
                if (!string.IsNullOrWhiteSpace(typeText)) item[kind == "method" ? "returnType" : "type"] = typeText;
                if (member is CodeFunction function)
                {
                    var parameters = Parameters(function);
                    if (parameters.Count > 0) item["parameters"] = parameters;
                }
                result.Add(item);
            }
            return result;
        }

        private static List<Dictionary<string, object>> Parameters(CodeFunction function)
        {
            var result = new List<Dictionary<string, object>>();
            try
            {
                foreach (CodeParameter parameter in function.Parameters)
                {
                    if (result.Count >= 15) break;
                    var item = new Dictionary<string, object> { ["name"] = parameter.Name };
                    var type = TypeText(parameter.Type);
                    if (!string.IsNullOrWhiteSpace(type)) item["type"] = type;
                    result.Add(item);
                }
            }
            catch { }
            return result;
        }

        private static string SyntaxContextAt(string text, int offset, string extension)
        {
            var stack = new Stack<string>();
            var quote = '\0';
            var escaped = false;
            var lineComment = false;
            var blockComment = false;
            var safe = Math.Max(0, Math.Min(offset, text.Length));
            for (var index = 0; index < safe; index++)
            {
                var current = text[index];
                var next = index + 1 < text.Length ? text[index + 1] : '\0';
                if (lineComment) { if (current == '\n') lineComment = false; continue; }
                if (blockComment) { if (current == '*' && next == '/') { blockComment = false; index++; } continue; }
                if (quote != '\0')
                {
                    if (escaped) { escaped = false; continue; }
                    if (current == '\\') { escaped = true; continue; }
                    if (current == quote) quote = '\0';
                    continue;
                }
                if (current == '/' && next == '/') { lineComment = true; index++; continue; }
                if (current == '/' && next == '*') { blockComment = true; index++; continue; }
                if (current == '#') { lineComment = true; continue; }
                if (current == '\'' || current == '"' || current == '`') { quote = current; continue; }
                if (current == '{') stack.Push(IsObjectBrace(text, index, extension) ? "braces" : "block");
                else if (current == '(') stack.Push("parentheses");
                else if (current == '[') stack.Push("bracket");
                else if (current == '}' || current == ')' || current == ']') if (stack.Count > 0) stack.Pop();
            }
            if (quote == '\'') return "singleQuote";
            if (quote == '"') return "doubleQuote";
            if (quote == '`') return "backtick";
            var currentContext = stack.Count == 0 ? "none" : stack.Peek();
            return currentContext == "braces" || currentContext == "parentheses" ? currentContext : "none";
        }

        private static bool IsObjectBrace(string text, int index, string extension)
        {
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)) return true;
            var start = Math.Max(0, index - 16);
            var before = text.Substring(start, index - start).TrimEnd();
            if (Regex.IsMatch(before, @"\b(if|for|foreach|while|switch|catch|try|finally|class|struct|namespace|function)\s*[^{}]*$")) return false;
            return before.EndsWith("=") || before.EndsWith(":") || before.EndsWith(",") || before.EndsWith("(") || before.EndsWith("[") || before.EndsWith("=>");
        }

        private static string ExtractExpression(string text, int offset)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var cursor = Math.Max(0, Math.Min(offset, text.Length));
            var start = cursor;
            while (start > 0 && IsIdentifierPart(text[start - 1])) start--;
            var end = cursor;
            while (end < text.Length && IsIdentifierPart(text[end])) end++;
            while (start > 0)
            {
                var index = start - 1;
                while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
                if (index < 0 || text[index] != '.') break;
                index--;
                while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
                while (index >= 0 && IsIdentifierPart(text[index])) index--;
                start = index + 1;
            }
            var value = text.Substring(start, end - start).Replace(" ", string.Empty);
            return Regex.IsMatch(value, @"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)*\.?$") ? value : string.Empty;
        }

        private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '$';
    }
}
