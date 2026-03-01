using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
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
        /// <param name="onError">Optional callback invoked on failure with the error message, for surfacing write failures to the UI.</param>
        public static void WriteInBackground(string filePath, string content, string callerName, Action<string>? onError = null)
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
                    var errorMsg = $"Background save failed for {Path.GetFileName(filePath)}: {ex.Message}";
                    AppLogger.Warn(callerName, errorMsg, ex);
                    onError?.Invoke(errorMsg);
                }
                finally
                {
                    sem.Release();

                    // Clean up the semaphore if no one else is waiting, to prevent
                    // unbounded growth of _locks over long sessions.
                    if (sem.CurrentCount == 1 && _locks.TryRemove(filePath, out var removed))
                    {
                        // If another thread snuck in via GetOrAdd between our TryRemove
                        // and here, 'removed' is the old instance and the dictionary
                        // already has a fresh one — safe to dispose the old one.
                        removed.Dispose();
                    }

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
