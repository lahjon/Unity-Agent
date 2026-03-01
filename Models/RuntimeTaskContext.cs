using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        public int ConsecutiveTokenLimitRetries { get; set; }
        public int LastIterationOutputStart { get; set; }

        // Token limit retry (all task types)
        public System.Windows.Threading.DispatcherTimer? TokenLimitRetryTimer { get; set; }
        public int TokenLimitRetryCount { get; set; }
        public long LastPromptTokenEstimate { get; set; }

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
        public bool PlanPhaseReady { get; set; }  // Set when ExitPlanMode is detected
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

        // Pending commit tracking
        public Task? PendingCommitTask { get; set; }

        // Message queue for handling mid-task input
        private readonly object _messageQueueLock = new();
        private Queue<string> _pendingMessages = new();
        private bool _isProcessingMessage = false;
        private bool _allowInterrupts = true; // Enable/disable interrupt capability
        private Queue<string> _interruptMessages = new(); // High-priority interrupt messages

        /// <summary>
        /// Queue for storing messages that arrive while the task is busy processing.
        /// Thread-safe property that handles concurrent access.
        /// </summary>
        public Queue<string> PendingMessages
        {
            get { lock (_messageQueueLock) return new Queue<string>(_pendingMessages); }
        }

        /// <summary>
        /// Adds a message to the pending message queue. Thread-safe.
        /// </summary>
        public void EnqueueMessage(string message)
        {
            lock (_messageQueueLock)
            {
                _pendingMessages.Enqueue(message);
            }
        }

        /// <summary>
        /// Attempts to dequeue a message from the pending message queue. Thread-safe.
        /// Returns null if no messages are available.
        /// </summary>
        public string? DequeueMessage()
        {
            lock (_messageQueueLock)
            {
                return _pendingMessages.Count > 0 ? _pendingMessages.Dequeue() : null;
            }
        }

        /// <summary>
        /// Gets the count of pending messages. Thread-safe.
        /// </summary>
        public int PendingMessageCount
        {
            get { lock (_messageQueueLock) return _pendingMessages.Count; }
        }

        /// <summary>
        /// Gets or sets whether the task is currently processing a message. Thread-safe.
        /// </summary>
        public bool IsProcessingMessage
        {
            get { lock (_messageQueueLock) return _isProcessingMessage; }
            set { lock (_messageQueueLock) _isProcessingMessage = value; }
        }

        /// <summary>
        /// Gets or sets whether the task allows interrupt messages. Thread-safe.
        /// </summary>
        public bool AllowInterrupts
        {
            get { lock (_messageQueueLock) return _allowInterrupts; }
            set { lock (_messageQueueLock) _allowInterrupts = value; }
        }

        /// <summary>
        /// Adds an interrupt message that should be processed immediately. Thread-safe.
        /// </summary>
        public void EnqueueInterruptMessage(string message)
        {
            lock (_messageQueueLock)
            {
                _interruptMessages.Enqueue(message);
            }
        }

        /// <summary>
        /// Attempts to dequeue an interrupt message. Thread-safe.
        /// Returns null if no interrupt messages are available.
        /// </summary>
        public string? DequeueInterruptMessage()
        {
            lock (_messageQueueLock)
            {
                return _interruptMessages.Count > 0 ? _interruptMessages.Dequeue() : null;
            }
        }

        /// <summary>
        /// Gets the count of pending interrupt messages. Thread-safe.
        /// </summary>
        public int InterruptMessageCount
        {
            get { lock (_messageQueueLock) return _interruptMessages.Count; }
        }

        /// <summary>
        /// Checks if there are any interrupt messages waiting. Thread-safe.
        /// </summary>
        public bool HasInterruptMessages
        {
            get { lock (_messageQueueLock) return _interruptMessages.Count > 0; }
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
