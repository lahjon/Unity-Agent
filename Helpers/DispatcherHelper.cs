using System;
using System.Windows.Threading;

namespace Spritely.Helpers
{
    /// <summary>
    /// Ensures ObservableCollection writes are performed under the correct lock
    /// and dispatched to the UI thread.
    /// </summary>
    public static class DispatcherHelper
    {
        /// <summary>
        /// Acquires <paramref name="lockObj"/>, then executes <paramref name="action"/>
        /// synchronously on the dispatcher thread.
        /// </summary>
        public static void DispatchWithLock(this Dispatcher dispatcher, object lockObj, Action action)
        {
            dispatcher.Invoke(() =>
            {
                lock (lockObj)
                {
                    action();
                }
            });
        }

        /// <summary>
        /// Acquires <paramref name="lockObj"/>, then executes <paramref name="action"/>
        /// asynchronously on the dispatcher thread via BeginInvoke.
        /// </summary>
        public static void BeginDispatchWithLock(this Dispatcher dispatcher, object lockObj, Action action)
        {
            dispatcher.BeginInvoke(() =>
            {
                lock (lockObj)
                {
                    action();
                }
            });
        }

        /// <summary>
        /// Acquires <paramref name="lockObj"/> and executes <paramref name="action"/>
        /// inline (caller is already on the dispatcher thread).
        /// </summary>
        public static void WithLock(object lockObj, Action action)
        {
            lock (lockObj)
            {
                action();
            }
        }
    }
}
