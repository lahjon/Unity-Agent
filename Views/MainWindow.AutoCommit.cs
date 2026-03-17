using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Auto-Commit & Git Commit ─────────────────────────────────

        private void PersistChangedFiles(AgentTask task, HashSet<string> lockedFiles)
            => _commitOrchestrator.PersistChangedFiles(task, lockedFiles);

        private Task PersistChangedFilesFromGitAsync(AgentTask task)
            => _commitOrchestrator.PersistChangedFilesFromGitAsync(task);

        /// <summary>
        /// Called after auto-commit completes (success or failure) to release file locks,
        /// transition from Committing to Completed, and perform the standard teardown.
        /// </summary>
        private void FinishTaskAfterCommit(AgentTask task, bool closeTab = true)
        {
            // Release all file locks now that commit is done (or failed)
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);
            task.Runtime.LockedFilesForCommit = null;
            task.Runtime.PendingCommitTask = null;

            // Persist changed files before releasing locks
            if (task.ChangedFiles.Count == 0)
            {
                var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                if (lockedFiles.Count > 0)
                    PersistChangedFiles(task, lockedFiles);
            }

            // Snapshot output so it survives restart
            if (string.IsNullOrEmpty(task.FullOutput) && task.OutputBuilder.Length > 0)
                task.FullOutput = task.OutputBuilder.ToString();

            // Transition from Committing to Completed
            task.Status = AgentTaskStatus.Completed;
            task.EndTime ??= DateTime.Now;
            _outputTabManager.UpdateTabHeader(task);

            // Keep task in active list for follow-up — don't call PerformTaskTeardown.
            // Resume queued/dependency-blocked tasks and update UI.
            RefreshActivityDashboard();
            UpdateStatus();
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            _taskOrchestrator.OnTaskCompleted(task.Id);
            DrainInitQueue();

            // Increment tray badge if app is not focused
            if (!IsActive)
                App.IncrementTrayBadge();
        }

        /// <summary>
        /// Releases file locks that were deferred for Auto-Commit.
        /// </summary>
        public void ReleaseTaskLocksAfterCommit(AgentTask task)
            => _commitOrchestrator.ReleaseTaskLocksAfterCommit(task, RefreshActivityDashboard);

        internal async void CommitTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: AgentTask task }) return;
            if (!task.IsFinished) return;

            if (task.IsCommitted)
            {
                // Uncommit - just mark as uncommitted
                task.IsCommitted = false;
                task.CommitHash = null;
                _historyManager.SaveHistory(_historyTasks);
                return;
            }

            // Commit the task changes
            var (success, errorMessage) = await CommitTaskAsync(task);
            if (!success)
            {
                Dialogs.DarkDialog.ShowAlert($"Failed to commit changes for task #{task.TaskNumber}\n\nError: {errorMessage}", "Commit Error");
            }
        }

        /// <summary>
        /// Commits task changes to git. Delegates to CommitOrchestrator.
        /// </summary>
        public Task<(bool success, string? errorMessage)> CommitTaskAsync(AgentTask task)
            => _commitOrchestrator.CommitTaskAsync(task);
    }
}
