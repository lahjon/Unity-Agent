using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Spritely.Managers
{
    /// <summary>
    /// Streams task output chunks to per-task log files on disk, enabling full output
    /// recovery without keeping everything in memory. Thread-safe for concurrent appends.
    /// Uses Channel-based async consumers to avoid thread pool starvation.
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

        /// <summary>Per-task write channels with dedicated async consumer loops.</summary>
        private readonly ConcurrentDictionary<string, TaskWriteChannel> _channels = new();

        /// <summary>Tracks total bytes written per task for diagnostics.</summary>
        private readonly ConcurrentDictionary<string, long> _bytesWritten = new();

        private sealed record WriteItem(string Text);

        private sealed class TaskWriteChannel
        {
            public Channel<WriteItem> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<WriteItem>(
                new UnboundedChannelOptions { SingleReader = true });
            public Task ConsumerTask { get; set; } = Task.CompletedTask;
        }

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
        /// Thread-safe; queues into a per-task Channel with an async consumer.
        /// </summary>
        public void WriteChunk(string taskId, string text)
        {
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(text)) return;

            var channel = _channels.GetOrAdd(taskId, id =>
            {
                var twc = new TaskWriteChannel();
                twc.ConsumerTask = ConsumeWritesAsync(id, twc.Channel);
                return twc;
            });

            // TryWrite on unbounded channel always succeeds
            channel.Channel.Writer.TryWrite(new WriteItem(text));
        }

        private async Task ConsumeWritesAsync(string taskId, Channel<WriteItem> channel)
        {
            var sem = _locks.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
            var filePath = GetLogPath(taskId);

            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                try
                {
                    await sem.WaitAsync();
                    try
                    {
                        await File.AppendAllTextAsync(filePath, item.Text);
                        _bytesWritten.AddOrUpdate(taskId, item.Text.Length, (_, prev) => prev + item.Text.Length);
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
            }
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
                // Complete the channel so the consumer drains and stops
                if (_channels.TryRemove(taskId, out var twc))
                {
                    twc.Channel.Writer.TryComplete();
                    // Best-effort wait for consumer to finish flushing
                    twc.ConsumerTask.Wait(TimeSpan.FromSeconds(2));
                }

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
            // Complete all channels so consumers stop
            foreach (var kvp in _channels)
                kvp.Value.Channel.Writer.TryComplete();

            // Wait briefly for consumers to drain
            foreach (var kvp in _channels)
            {
                try { kvp.Value.ConsumerTask.Wait(TimeSpan.FromSeconds(2)); }
                catch { /* best effort */ }
            }
            _channels.Clear();

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
