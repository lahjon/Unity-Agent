using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
{
    /// <summary>
    /// Serializes file writes per-path and tracks pending operations so they can be
    /// flushed during application shutdown to prevent data loss.
    /// </summary>
    public static class SafeFileWriter
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
        private static int _pendingCount;
        private static readonly ManualResetEventSlim _allComplete = new(true);

        /// <summary>
        /// Queue a background write to the specified file path.
        /// Serializes writes to the same path and tracks completion for shutdown flush.
        /// </summary>
        public static void WriteInBackground(string filePath, string content, string callerName)
        {
            IncrementPending();
            var sem = _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

            Task.Run(async () =>
            {
                await sem.WaitAsync();
                try
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (dir != null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var tmpPath = filePath + ".tmp";
                    File.WriteAllText(tmpPath, content);
                    File.Move(tmpPath, filePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(callerName, $"Background save failed for {Path.GetFileName(filePath)}", ex);
                }
                finally
                {
                    sem.Release();
                    DecrementPending();
                }
            });
        }

        /// <summary>
        /// Blocks until all pending background writes have completed.
        /// Call this during application shutdown.
        /// </summary>
        public static bool FlushAll(int timeoutMs = 5000)
        {
            return _allComplete.Wait(timeoutMs);
        }

        private static void IncrementPending()
        {
            if (Interlocked.Increment(ref _pendingCount) == 1)
                _allComplete.Reset();
        }

        private static void DecrementPending()
        {
            if (Interlocked.Decrement(ref _pendingCount) == 0)
                _allComplete.Set();
        }
    }
}
