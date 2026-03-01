using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace HappyEngine
{
    /// <summary>
    /// Transient runtime state for an active agent task. Holds the OS process,
    /// feature-mode timers, output buffer, and queue/planning bookkeeping.
    /// None of this state is persisted.
    /// </summary>
    public class RuntimeTaskContext : IDisposable
    {
        // Process management
        public Process? Process { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public StringBuilder OutputBuilder { get; } = new();

        // Feature mode
        public System.Windows.Threading.DispatcherTimer? FeatureModeRetryTimer { get; set; }
        public System.Windows.Threading.DispatcherTimer? FeatureModeIterationTimer { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int LastIterationOutputStart { get; set; }

        // Token limit retry (all task types)
        public System.Windows.Threading.DispatcherTimer? TokenLimitRetryTimer { get; set; }

        // Queue / dependency tracking
        public string? QueuedReason { get; set; }
        public string? BlockedByTaskId { get; set; }
        public int? BlockedByTaskNumber { get; set; }

        // Thread-safe dependency tracking
        private readonly object _depLock = new();
        private List<string> _dependencyTaskIds = new();

        /// <summary>
        /// Gets a snapshot (copy) of the dependency task IDs for safe iteration,
        /// or replaces the entire list under lock.
        /// </summary>
        public List<string> DependencyTaskIds
        {
            get { lock (_depLock) return _dependencyTaskIds.ToList(); }
            set { lock (_depLock) _dependencyTaskIds = value ?? new List<string>(); }
        }

        public void AddDependencyTaskId(string id) { lock (_depLock) _dependencyTaskIds.Add(id); }
        public bool RemoveDependencyTaskId(string id) { lock (_depLock) return _dependencyTaskIds.Remove(id); }
        public bool ContainsDependencyTaskId(string id) { lock (_depLock) return _dependencyTaskIds.Contains(id); }
        public int DependencyTaskIdCount { get { lock (_depLock) return _dependencyTaskIds.Count; } }
        public void ClearDependencyTaskIds() { lock (_depLock) _dependencyTaskIds.Clear(); }

        public List<int> DependencyTaskNumbers { get; set; } = new();

        // Parent-child hierarchy
        public int SubTaskCounter { get; set; }
        public int SubTaskIndex { get; set; }

        // Plan-before-queue workflow
        public bool IsPlanningBeforeQueue { get; set; }
        public bool NeedsPlanRestart { get; set; }
        public string? PendingFileLockPath { get; set; }
        public string? PendingFileLockBlocker { get; set; }

        // Auto-Commit file lock management - Thread-safe
        private readonly object _lockedFilesLock = new();
        private HashSet<string>? _lockedFilesForCommit;

        /// <summary>
        /// Gets or sets the locked files for commit. Thread-safe property that handles
        /// concurrent access from stream-parsing thread and process-exit callback thread.
        /// </summary>
        public HashSet<string>? LockedFilesForCommit
        {
            get
            {
                lock (_lockedFilesLock)
                    return _lockedFilesForCommit != null ? new HashSet<string>(_lockedFilesForCommit) : null;
            }
            set
            {
                lock (_lockedFilesLock)
                    _lockedFilesForCommit = value;
            }
        }

        public void Dispose()
        {
            FeatureModeRetryTimer?.Stop();
            FeatureModeRetryTimer = null;
            FeatureModeIterationTimer?.Stop();
            FeatureModeIterationTimer = null;
            TokenLimitRetryTimer?.Stop();
            TokenLimitRetryTimer = null;

            try { Cts?.Cancel(); } catch (ObjectDisposedException) { }
            Cts?.Dispose();
            Cts = null;

            try { if (Process is { HasExited: false }) Process.Kill(entireProcessTree: true); } catch { }
            try { Process?.Dispose(); } catch (Exception ex) { Managers.AppLogger.Debug("RuntimeTaskContext", $"Failed to dispose process: {ex.Message}"); }
            Process = null;
        }
    }
}
