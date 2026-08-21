using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FastVisualStudioBridge
{
    internal static class BridgeLog
    {
        private const string FilePrefix = "fast-visual-studio-bridge-";
        private const long MaxLogFileBytes = 5L * 1024 * 1024;
        private const int LogBackupCount = 2;
        private static readonly object Gate = new object();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mirror", "FastVisualStudioBridge", "Logs");

        internal static void Information(string message) => Write("INFO", message, null);

        internal static void Warning(string message, Exception error = null)
        {
            try { ActivityLog.LogWarning(nameof(FastVisualStudioBridge), Format(message, error)); }
            catch { }
            Write("WARN", message, error);
        }

        internal static void Error(string message, Exception error = null)
        {
            try { ActivityLog.LogError(nameof(FastVisualStudioBridge), Format(message, error)); }
            catch { }
            Write("ERROR", message, error);
        }

        private static void Write(string level, string message, Exception error)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    Prune();
                    var file = Path.Combine(LogDirectory,
                        FilePrefix + DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log");
                    var line = DateTimeOffset.UtcNow.ToString("O") + " [" + level + "] " +
                        Format(message, error) + Environment.NewLine;
                    RotateIfNeeded(file, line);
                    File.AppendAllText(file, line, Encoding.UTF8);
                }
            }
            catch
            {
                // ActivityLog remains the fallback when the plugin log cannot be written.
            }
        }

        private static void RotateIfNeeded(string file, string line)
        {
            if (!File.Exists(file) || new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) <= MaxLogFileBytes) return;
            var oldest = file + "." + LogBackupCount;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (var index = LogBackupCount - 1; index >= 1; index--)
            {
                var source = file + "." + index;
                if (File.Exists(source)) File.Move(source, file + "." + (index + 1));
            }
            File.Move(file, file + ".1");
        }

        private static void Prune()
        {
            var cutoff = DateTime.UtcNow.AddDays(-3);
            foreach (var file in Directory.GetFiles(LogDirectory, FilePrefix + "*.log*"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
        }

        private static string Format(string message, Exception error)
        {
            var text = Compact(message);
            if (error != null)
                text += " (" + error.GetType().Name + ": " + Compact(error.Message) + ")";
            return text;
        }

        private static string Compact(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = Regex.Replace(value, "(?i)bearer\\s+\\S+", "Bearer <redacted>")
                .Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500) + "...";
        }
    }
}
