using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spritely.Models;
using Spritely.Services;

namespace Spritely.Managers
{
    /// <summary>
    /// Owns the full auto-commit workflow: guard check, staging known files,
    /// building a commit message, calling GitHelper, and releasing file locks.
    /// Centralizes the invariant that no commit may happen while unrelated file locks are active.
    /// </summary>
    public class CommitOrchestrator
    {
        private readonly IGitHelper _gitHelper;
        private readonly GitOperationGuard _gitOperationGuard;
        private readonly FileLockManager _fileLockManager;
        private readonly HistoryManager _historyManager;
        private readonly Func<ObservableCollection<AgentTask>> _getHistoryTasks;
        private readonly Func<ObservableCollection<AgentTask>> _getActiveTasks;
        private readonly Action? _onGitPanelDirty;

        public CommitOrchestrator(
            IGitHelper gitHelper,
            GitOperationGuard gitOperationGuard,
            FileLockManager fileLockManager,
            HistoryManager historyManager,
            Func<ObservableCollection<AgentTask>> getHistoryTasks,
            Func<ObservableCollection<AgentTask>> getActiveTasks,
            Action? onGitPanelDirty = null)
        {
            _gitHelper = gitHelper ?? throw new ArgumentNullException(nameof(gitHelper));
            _gitOperationGuard = gitOperationGuard ?? throw new ArgumentNullException(nameof(gitOperationGuard));
            _fileLockManager = fileLockManager ?? throw new ArgumentNullException(nameof(fileLockManager));
            _historyManager = historyManager ?? throw new ArgumentNullException(nameof(historyManager));
            _getHistoryTasks = getHistoryTasks ?? throw new ArgumentNullException(nameof(getHistoryTasks));
            _getActiveTasks = getActiveTasks ?? throw new ArgumentNullException(nameof(getActiveTasks));
            _onGitPanelDirty = onGitPanelDirty;
        }

        /// <summary>
        /// Commits task changes to git. Can be called from UI (manual) or auto-commit flow.
        /// </summary>
        public async Task<(bool success, string? errorMessage)> CommitTaskAsync(AgentTask task)
        {
            if (task == null) return (false, "Task is null");
            if (task.IsCommitted) return (false, "Task is already committed");

            if (!task.IsFinished && !task.IsCommitting)
                return (false, "Task is not in a committable state");

            try
            {
                var commitMessage = ResolveCommitMessage(task);
                var relativePaths = await ResolveFilePaths(task);

                if (relativePaths.Count == 0)
                    return (false, "No files were modified by this task to commit");

                // Persist changed files on the task if not already set
                if (task.ChangedFiles.Count == 0)
                    task.ChangedFiles = new List<string>(relativePaths);

                return await ExecuteCommit(task, commitMessage, relativePaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CommitTaskAsync failed: {ex.Message}");
                return (false, $"Unexpected error during commit: {ex.Message}");
            }
        }

        /// <summary>
        /// Persists the file lock paths as relative ChangedFiles on the task
        /// so they survive across sessions even if the commit fails.
        /// </summary>
        public void PersistChangedFiles(AgentTask task, HashSet<string> lockedFiles)
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
        /// </summary>
        public async Task PersistChangedFilesFromGitAsync(AgentTask task)
        {
            try
            {
                var gitFiles = await _gitHelper.GetChangedFileNamesAsync(task.ProjectPath, task.GitStartHash);
                if (gitFiles != null && gitFiles.Count > 0 && task.ChangedFiles.Count == 0)
                    task.ChangedFiles = gitFiles;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CommitOrchestrator", $"Failed to persist changed files from git for task {task.Id}", ex);
            }
        }

        /// <summary>
        /// Releases file locks that were deferred for Auto-Commit.
        /// Called after git commit is complete to finally release the locks.
        /// Only used for manual commits on history tasks (not the Committing flow).
        /// </summary>
        public void ReleaseTaskLocksAfterCommit(AgentTask task, Action? onRefreshDashboard = null)
        {
            // If task is in Committing status, FinishTaskAfterCommit handles lock release
            if (task.Status == AgentTaskStatus.Committing)
                return;

            if (task.Runtime.LockedFilesForCommit != null && task.Runtime.LockedFilesForCommit.Count > 0)
            {
                _fileLockManager.ReleaseTaskLocks(task.Id);
                task.Runtime.LockedFilesForCommit = null;

                // Check if any queued tasks can now proceed
                _fileLockManager.CheckQueuedTasks(_getActiveTasks());
            }

            task.Runtime.PendingCommitTask = null;
            task.OnPropertyChanged(nameof(task.IsPendingCommit));
            onRefreshDashboard?.Invoke();
        }

        // ── Private helpers ──────────────────────────────────────────

        private string ResolveCommitMessage(AgentTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                return task.CompletionSummary;
            if (!string.IsNullOrWhiteSpace(task.Summary))
                return task.Summary;
            return task.Description;
        }

        private async Task<List<string>> ResolveFilePaths(AgentTask task)
        {
            var lockedFiles = ResolveLockedFiles(task);
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
                        AppLogger.Warn("CommitOrchestrator", $"Rejected suspicious path during git operation: {rel}");
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
                if (gitChangedFiles != null)
                {
                    foreach (var f in gitChangedFiles)
                    {
                        if (!f.Contains("..") && !Path.IsPathRooted(f))
                            relativePaths.Add(f.Replace('\\', '/'));
                    }
                }
            }

            return relativePaths;
        }

        private HashSet<string>? ResolveLockedFiles(AgentTask task)
        {
            var lockedFiles = task.Runtime.LockedFilesForCommit;
            if (lockedFiles == null || lockedFiles.Count == 0)
            {
                lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                if (lockedFiles == null || lockedFiles.Count == 0)
                {
                    if (task.ChangedFiles.Count > 0)
                    {
                        lockedFiles = new HashSet<string>(task.ChangedFiles.Select(f =>
                            Path.IsPathRooted(f) ? f : Path.Combine(task.ProjectPath, f)));
                    }
                }
                if (lockedFiles != null && lockedFiles.Count > 0)
                    task.Runtime.LockedFilesForCommit = lockedFiles;
            }
            return lockedFiles;
        }

        private async Task<(bool success, string? errorMessage)> ExecuteCommit(
            AgentTask task, string commitMessage, List<string> relativePaths)
        {
            return await _gitOperationGuard.ExecuteGitOperationAsync(async () =>
            {
                var pathArgs = string.Join(" ", relativePaths.Select(GitHelper.EscapeGitPath));

                // Stage only the locked files
                var addResult = await _gitHelper.RunGitCommandAsync(task.ProjectPath, $"add -- {pathArgs}");
                if (addResult == null)
                {
                    Debug.WriteLine("CommitTaskAsync failed: Failed to stage files");
                    return (false, (string?)$"Failed to stage files for commit. Git add command failed for paths: {pathArgs}");
                }

                // Capture diff summary before committing
                var diffResult = await _gitHelper.RunGitCommandAsync(
                    task.ProjectPath, $"diff --cached --stat -- {pathArgs}");
                if (diffResult.IsSuccess && !string.IsNullOrWhiteSpace(diffResult.Output))
                    task.CommitDiff = diffResult.Output.Trim();

                // Commit with pathspec to prevent leaking other staged changes
                var result = await _gitHelper.CommitSecureAsync(
                    task.ProjectPath, commitMessage, pathArgs);

                if (result.IsSuccess)
                {
                    var hash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath);
                    if (hash != null)
                    {
                        task.IsCommitted = true;
                        task.CommitHash = hash;
                        _historyManager.SaveHistory(_getHistoryTasks());
                        _onGitPanelDirty?.Invoke();

                        // Release deferred file locks (no-op during auto-commit Committing status)
                        ReleaseTaskLocksAfterCommit(task);

                        return (true, (string?)null);
                    }
                    return (false, (string?)"Git commit succeeded but failed to capture commit hash");
                }
                return (false, (string?)$"Git commit failed: {result.GetErrorMessage()}");
            }, $"commit for task #{task.TaskNumber}");
        }
    }
}
