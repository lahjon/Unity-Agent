using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AgenticEngine
{
    /// <summary>
    /// Transient runtime state for an active agent task. Holds the OS process,
    /// overnight-mode timers, output buffer, and queue/planning bookkeeping.
    /// None of this state is persisted.
    /// </summary>
    public class RuntimeTaskContext
    {
        // Process management
        public Process? Process { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public StringBuilder OutputBuilder { get; } = new();

        // Overnight mode
        public System.Windows.Threading.DispatcherTimer? OvernightRetryTimer { get; set; }
        public System.Windows.Threading.DispatcherTimer? OvernightIterationTimer { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int LastIterationOutputStart { get; set; }

        // Git state captured at task start for completion summary diff
        public string? GitStartHash { get; set; }

        // Queue / dependency tracking
        public string? QueuedReason { get; set; }
        public string? BlockedByTaskId { get; set; }
        public List<string> DependencyTaskIds { get; set; } = new();

        // Plan-before-queue workflow
        public bool IsPlanningBeforeQueue { get; set; }
        public bool NeedsPlanRestart { get; set; }
        public string? PendingFileLockPath { get; set; }
        public string? PendingFileLockBlocker { get; set; }
    }
}
