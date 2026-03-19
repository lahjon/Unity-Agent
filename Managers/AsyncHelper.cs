using System;
using System.Threading.Tasks;

namespace Spritely.Managers
{
    /// <summary>
    /// Provides a safe wrapper for fire-and-forget async calls, ensuring
    /// unhandled exceptions are logged rather than silently crashing the app.
    /// </summary>
    public static class AsyncHelper
    {
        /// <summary>
        /// Executes an async action without awaiting, catching and logging any exceptions.
        /// Use this instead of async void to prevent unhandled exceptions from tearing down the process.
        /// </summary>
        public static async void FireAndForget(Func<Task> action, string context)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // Cancellations are expected — don't log as errors
            }
            catch (Exception ex)
            {
                AppLogger.Error("AsyncHelper", $"Unhandled exception in {context}", ex);
            }
        }
    }
}
