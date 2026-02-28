using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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

        // Lock only guards the in-memory buffer
        private static readonly object _lock = new();
        private static readonly LinkedList<string> _recentEntries = new();
        private static LogLevel _minLevel = LogLevel.Debug;

        // Write-batching: ConcurrentQueue handles thread safety for the file-write path
        private static readonly ConcurrentQueue<string> _writeQueue = new();
        private static Timer? _flushTimer;

        public static LogLevel MinLevel
        {
            get => _minLevel;
            set => _minLevel = value;
        }

        public static void Initialize()
        {
            try { Directory.CreateDirectory(LogDir); }
            catch { /* can't log about failing to create the log dir */ }

            // Start background flush timer: drains the queue every 500ms
            _flushTimer = new Timer(_ => DrainQueue(), null, 500, 500);
        }

        public static void Debug(string source, string message, Exception? ex = null)
            => Log(LogLevel.Debug, source, message, ex);

        public static void Info(string source, string message, Exception? ex = null)
            => Log(LogLevel.Info, source, message, ex);

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
                entry += $"\n  Exception: {ex.ToString()}";

            // Add to in-memory buffer (guarded by lock)
            lock (_lock)
            {
                _recentEntries.AddLast(entry);
                while (_recentEntries.Count > MaxMemoryEntries)
                    _recentEntries.RemoveFirst();
            }

            // Enqueue for batched file write (ConcurrentQueue is thread-safe)
            _writeQueue.Enqueue(entry);
        }

        /// <summary>
        /// Immediately drains the write queue to disk.
        /// Call during app shutdown to ensure no log entries are lost.
        /// </summary>
        public static void Flush()
        {
            // Stop the timer so it doesn't race with this final drain
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            DrainQueue();
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

        /// <summary>
        /// Drains all queued entries and writes them to the log file in a single I/O call.
        /// Called by the background timer (~every 500ms) and by Flush() on shutdown.
        /// </summary>
        private static void DrainQueue()
        {
            try
            {
                var batch = new List<string>();
                while (_writeQueue.TryDequeue(out var line))
                    batch.Add(line);

                if (batch.Count == 0) return;

                RotateIfNeeded();
                File.AppendAllLines(LogFile, batch);
            }
            catch { /* last resort: don't crash the logger */ }
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
