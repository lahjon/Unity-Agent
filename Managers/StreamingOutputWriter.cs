using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers
{
    /// <summary>
    /// Streams task output chunks to per-task log files on disk, enabling full output
    /// recovery without keeping everything in memory. Thread-safe for concurrent appends.
    /// </summary>
    public sealed class StreamingOutputWriter : IDisposable
    {
        private static readonly string OutputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spritely", "output");

        /// <summary>Max age before old log files are cleaned up on startup.</summary>
        private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

        /// <summary>Per-file locks to serialize writes to the same task log.</summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        /// <summary>Tracks total bytes written per task for diagnostics.</summary>
        private readonly ConcurrentDictionary<string, long> _bytesWritten = new();

        public StreamingOutputWriter()
        {
            try
            {
                Directory.CreateDirectory(OutputDir);
                CleanupOldFiles();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StreamingOutputWriter", $"Failed to initialize output directory: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends a text chunk to the task's log file on disk.
        /// Thread-safe; serializes writes per task.
        /// </summary>
        public void WriteChunk(string taskId, string text)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text)) return;

            var sem = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
            // Fire-and-forget on thread pool to avoid blocking callers
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    sem.Wait();
                    try
                    {
                        var filePath = GetLogPath(taskId);
                        File.AppendAllText(filePath, text);
                        _bytesWritten.AddOrUpdate(taskId, text.Length, (_, prev) => prev + text.Length);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("StreamingOutputWriter", $"Failed to write chunk for task {taskId}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Reads a page of lines from the task's log file.
        /// Returns the lines and total line count for pagination.
        /// </summary>
        /// <param name="taskId">Task ID</param>
        /// <param name="pageNumber">0-based page number</param>
        /// <param name="pageSize">Lines per page</param>
        /// <param name="totalLines">Total line count in the file</param>
        /// <returns>Lines for the requested page, or empty if file doesn't exist.</returns>
        public async Task<(IReadOnlyList<string> Lines, int TotalLines)> ReadPageAsync(string taskId, int pageNumber, int pageSize)
        {
            var filePath = GetLogPath(taskId);
            if (!File.Exists(filePath))
                return (Array.Empty<string>(), 0);

            var sem = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                var allLines = await File.ReadAllLinesAsync(filePath);
                int totalLines = allLines.Length;

                int skip = pageNumber * pageSize;
                if (skip >= totalLines)
                    return (Array.Empty<string>(), totalLines);

                return (allLines.Skip(skip).Take(pageSize).ToList(), totalLines);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("StreamingOutputWriter", $"Failed to read page for task {taskId}: {ex.Message}");
                return (Array.Empty<string>(), 0);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// Reads the full output from the task's log file.
        /// </summary>
        public async Task<string> ReadAllAsync(string taskId)
        {
            var filePath = GetLogPath(taskId);
            if (!File.Exists(filePath))
                return string.Empty;

            var sem = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                return await File.ReadAllTextAsync(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("StreamingOutputWriter", $"Failed to read all for task {taskId}: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>Returns the total bytes written for a task.</summary>
        public long GetBytesWritten(string taskId)
            => _bytesWritten.TryGetValue(taskId, out var bytes) ? bytes : 0;

        /// <summary>Returns whether a log file exists for the given task.</summary>
        public bool HasLogFile(string taskId)
            => File.Exists(GetLogPath(taskId));

        /// <summary>Deletes the log file for a completed/cancelled task.</summary>
        public void DeleteLog(string taskId)
        {
            try
            {
                var filePath = GetLogPath(taskId);
                if (File.Exists(filePath))
                    File.Delete(filePath);
                _locks.TryRemove(taskId, out _);
                _bytesWritten.TryRemove(taskId, out _);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("StreamingOutputWriter", $"Failed to delete log for task {taskId}: {ex.Message}");
            }
        }

        /// <summary>Gets the file path for a task's output log.</summary>
        public static string GetLogPath(string taskId)
            => Path.Combine(OutputDir, $"{taskId}.log");

        public void Dispose()
        {
            foreach (var kvp in _locks)
                kvp.Value.Dispose();
            _locks.Clear();
        }

        private static void CleanupOldFiles()
        {
            try
            {
                var cutoff = DateTime.UtcNow - RetentionPeriod;
                foreach (var file in Directory.GetFiles(OutputDir, "*.log"))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        try { File.Delete(file); }
                        catch { /* best effort */ }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("StreamingOutputWriter", $"Cleanup failed: {ex.Message}");
            }
        }
    }
}
