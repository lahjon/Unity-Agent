using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AgenticEngine.Managers
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public static class AppLogger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticEngine", "logs");
        private static readonly string LogFile = Path.Combine(LogDir, "app.log");

        private const int MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
        private const int MaxMemoryEntries = 500;

        private static readonly object _lock = new();
        private static readonly LinkedList<string> _recentEntries = new();
        private static LogLevel _minLevel = LogLevel.Debug;

        public static LogLevel MinLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        public static void Initialize()
        {
            try { Directory.CreateDirectory(LogDir); }
            catch { /* can't log about failing to create the log dir */ }
        }

        public static void Debug(string source, string message)
            => Log(LogLevel.Debug, source, message);

        public static void Info(string source, string message)
            => Log(LogLevel.Info, source, message);

        public static void Warn(string source, string message, Exception? ex = null)
            => Log(LogLevel.Warning, source, message, ex);

        public static void Error(string source, string message, Exception? ex = null)
            => Log(LogLevel.Error, source, message, ex);

        public static void Log(LogLevel level, string source, string message, Exception? ex = null)
        {
            if (level < _minLevel) return;

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var levelTag = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "???"
            };

            var entry = $"[{timestamp}] [{levelTag}] [{source}] {message}";
            if (ex != null)
                entry += $"\n  Exception: {ex.GetType().Name}: {ex.Message}";

            lock (_lock)
            {
                _recentEntries.AddLast(entry);
                while (_recentEntries.Count > MaxMemoryEntries)
                    _recentEntries.RemoveFirst();
            }

            try
            {
                RotateIfNeeded();
                File.AppendAllText(LogFile, entry + "\n");
            }
            catch { /* last resort: don't crash the logger */ }
        }

        public static string[] GetRecentEntries()
        {
            lock (_lock)
            {
                var entries = new string[_recentEntries.Count];
                _recentEntries.CopyTo(entries, 0);
                return entries;
            }
        }

        public static string GetLogFilePath() => LogFile;

        public static string ReadLogFile()
        {
            try
            {
                if (!File.Exists(LogFile)) return "(No log file yet)";
                return File.ReadAllText(LogFile);
            }
            catch (Exception ex)
            {
                return $"(Error reading log file: {ex.Message})";
            }
        }

        public static void ClearMemory()
        {
            lock (_lock)
            {
                _recentEntries.Clear();
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(LogFile)) return;
                var info = new FileInfo(LogFile);
                if (info.Length < MaxFileSizeBytes) return;

                var rotated = LogFile + ".old";
                if (File.Exists(rotated))
                    File.Delete(rotated);
                File.Move(LogFile, rotated);
            }
            catch { /* rotation failure is non-critical */ }
        }
    }
}
