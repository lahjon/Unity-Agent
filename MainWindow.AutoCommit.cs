using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// Persists the file lock paths as relative ChangedFiles on the task data
        /// so they survive across sessions even if the commit fails.
        /// </summary>
        private void PersistChangedFiles(AgentTask task, HashSet<string> lockedFiles)
        {
            var projectRoot = task.ProjectPath.TrimEnd('\\', '/').ToLowerInvariant() + "\\";
            var relativePaths = new List<string>();
            foreach (var absPath in lockedFiles)
            {
                var rel = absPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                    ? absPath.Substring(projectRoot.Length)
                    : absPath;
                if (!rel.Contains("..") && !Path.IsPathRooted(rel))
                    relativePaths.Add(rel.Replace('\\', '/'));
            }
            task.ChangedFiles = relativePaths;
        }

        /// <summary>
        /// Falls back to git diff to discover changed files when file locks are empty.
        /// Fire-and-forget — best effort to populate ChangedFiles for later manual commit.
        /// </summary>
        private async Task PersistChangedFilesFromGitAsync(AgentTask task)
        {
            try
            {
                var gitFiles = await _gitHelper.GetChangedFileNamesAsync(task.ProjectPath, task.GitStartHash);
                if (gitFiles != null && gitFiles.Count > 0 && task.ChangedFiles.Count == 0)
                    task.ChangedFiles = gitFiles;
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("Orchestration", $"Failed to persist changed files from git for task {task.Id}", ex);
            }
        }

        /// <summary>
        /// Called after auto-commit completes (success or failure) to release file locks,
        /// transition from Committing to Completed, and perform the standard teardown.
        /// </summary>
        private void FinishTaskAfterCommit(AgentTask task, bool closeTab = true)
        {
            // Release all file locks now that commit is done (or failed)
            _fileLockManager.ReleaseTaskLocks(task.Id);
            task.Runtime.LockedFilesForCommit = null;
            task.Runtime.PendingCommitTask = null;

            // Transition from Committing to Completed
            task.Status = AgentTaskStatus.Completed;
            task.EndTime ??= DateTime.Now;
            _outputTabManager.UpdateTabHeader(task);

            // Perform the standard teardown (move to history, resume queued tasks, etc.)
            PerformTaskTeardown(task, closeTab);
        }

        /// <summary>
        /// Releases file locks that were deferred for Auto-Commit.
        /// Called after git commit is complete to finally release the locks.
        /// Only used for manual commits on history tasks (not the Committing flow).
        /// </summary>
        public void ReleaseTaskLocksAfterCommit(AgentTask task)
        {
            // If task is in Committing status, FinishTaskAfterCommit handles lock release
            if (task.Status == AgentTaskStatus.Committing)
                return;

            if (task.Runtime.LockedFilesForCommit != null && task.Runtime.LockedFilesForCommit.Count > 0)
            {
                _fileLockManager.ReleaseTaskLocks(task.Id);
                task.Runtime.LockedFilesForCommit = null;

                // Check if any queued tasks can now proceed
                _fileLockManager.CheckQueuedTasks(_activeTasks);
            }

            // Clear pending commit task
            task.Runtime.PendingCommitTask = null;
            task.OnPropertyChanged(nameof(task.IsPendingCommit));
            RefreshActivityDashboard();
        }

        private async void CommitTask_Click(object sender, RoutedEventArgs e)
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
        /// Commits task changes to git. Can be called from UI or auto-commit.
        /// </summary>
        /// <param name="task">The task to commit</param>
        /// <returns>Tuple of (success, errorMessage) - success is true if commit was successful, errorMessage contains details if failed</returns>
        public async Task<(bool success, string? errorMessage)> CommitTaskAsync(AgentTask task)
        {
            if (task == null || task.IsCommitted)
            {
                if (task == null) return (false, "Task is null");
                if (task.IsCommitted) return (false, "Task is already committed");
                return (false, "Unknown pre-condition failure");
            }

            // Allow commit from Committing status (auto-commit flow) or any finished status (manual commit)
            if (!task.IsFinished && !task.IsCommitting)
            {
                return (false, "Task is not in a committable state");
            }

            try
            {
                // Use CompletionSummary (the task's AI-generated summary) as commit message when available,
                // falling back to Summary, then Description
                var summary = !string.IsNullOrWhiteSpace(task.CompletionSummary)
                    ? task.CompletionSummary
                    : !string.IsNullOrWhiteSpace(task.Summary)
                        ? task.Summary
                        : task.Description;
                var commitMessage = $"Task #{task.TaskNumber}: {summary}";

                // Get the files locked by this task for scoped commit
                var lockedFiles = task.Runtime.LockedFilesForCommit;
                if (lockedFiles == null || lockedFiles.Count == 0)
                {
                    // If not set in runtime (manual commit), get from file lock manager
                    lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                    if (lockedFiles == null || lockedFiles.Count == 0)
                    {
                        // Fall back to persisted ChangedFiles if locks were already released
                        if (task.ChangedFiles.Count > 0)
                        {
                            lockedFiles = new HashSet<string>(task.ChangedFiles.Select(f =>
                                Path.IsPathRooted(f) ? f : Path.Combine(task.ProjectPath, f)));
                        }
                    }
                    if (lockedFiles != null && lockedFiles.Count > 0)
                        task.Runtime.LockedFilesForCommit = lockedFiles;
                }

                // Build relative paths from the project root for the git commands
                var projectRoot = task.ProjectPath.TrimEnd('\\', '/').ToLowerInvariant() + "\\";
                var relativePaths = new List<string>();

                if (lockedFiles != null && lockedFiles.Count > 0)
                {
                    foreach (var absPath in lockedFiles)
                    {
                        var rel = absPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                            ? absPath.Substring(projectRoot.Length)
                            : absPath;

                        if (rel.Contains("..") || Path.IsPathRooted(rel))
                        {
                            AppLogger.Warn("TaskExecution", $"Rejected suspicious path during git operation: {rel}");
                            continue;
                        }

                        relativePaths.Add(rel.Replace('\\', '/'));
                    }
                }

                // Fall back to git diff against GitStartHash when file locks didn't capture changes
                if (relativePaths.Count == 0 && !string.IsNullOrEmpty(task.GitStartHash))
                {
                    var gitChangedFiles = await _gitHelper.GetChangedFileNamesAsync(
                        task.ProjectPath, task.GitStartHash);
                    if (gitChangedFiles != null && gitChangedFiles.Count > 0)
                    {
                        foreach (var f in gitChangedFiles)
                        {
                            if (!f.Contains("..") && !Path.IsPathRooted(f))
                                relativePaths.Add(f.Replace('\\', '/'));
                        }
                    }
                }

                if (relativePaths.Count == 0)
                    return (false, "No files were modified by this task to commit");

                // Persist changed files on the task if not already set
                if (task.ChangedFiles.Count == 0 && relativePaths.Count > 0)
                    task.ChangedFiles = new List<string>(relativePaths);

                // Use GitOperationGuard to serialize git operations
                var (success, errorMessage) = await _gitOperationGuard.ExecuteGitOperationAsync(async () =>
                {
                    // Stage only the locked files (handles new/untracked files)
                    var pathArgs = string.Join(" ", relativePaths.Select(GitHelper.EscapeGitPath));
                    var addResult = await _gitHelper.RunGitCommandAsync(task.ProjectPath, $"add -- {pathArgs}");

                    // Check if git add failed
                    if (addResult == null)
                    {
                        Debug.WriteLine($"CommitTaskAsync failed: Failed to stage files");
                        return (false, $"Failed to stage files for commit. Git add command failed for paths: {pathArgs}");
                    }

                    // Capture diff summary before committing so the task has a record of what changed
                    var diffResult = await _gitHelper.RunGitCommandAsync(
                        task.ProjectPath, $"diff --cached --stat -- {pathArgs}");
                    if (diffResult.IsSuccess && !string.IsNullOrWhiteSpace(diffResult.Output))
                        task.CommitDiff = diffResult.Output.Trim();

                    // Commit only these specific files — the pathspec ensures no other
                    // staged changes from concurrent tasks leak into this commit
                    var result = await _gitHelper.CommitSecureAsync(
                        task.ProjectPath,
                        commitMessage,
                        pathArgs);

                    if (result.IsSuccess)
                    {
                        // Get the commit hash to verify commit succeeded
                        var hash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath);
                        if (hash != null)
                        {
                            task.IsCommitted = true;
                            task.CommitHash = hash;
                            _historyManager.SaveHistory(_historyTasks);

                            // Mark git panel as dirty to refresh
                            _gitPanelManager?.MarkDirty();

                            // Release deferred file locks if any (no-op during auto-commit Committing status)
                            ReleaseTaskLocksAfterCommit(task);

                            return (true, (string?)null);
                        }
                        else
                        {
                            return (false, "Git commit succeeded but failed to capture commit hash");
                        }
                    }
                    else
                    {
                        return (false, $"Git commit failed: {result.GetErrorMessage()}");
                    }
                }, $"commit for task #{task.TaskNumber}");

                return success ? (true, (string?)null) : (false, errorMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitTaskAsync failed: {ex.Message}");
                return (false, $"Unexpected error during commit: {ex.Message}");
            }
        }
    }
}
