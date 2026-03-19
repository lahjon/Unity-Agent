using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Spritely.Constants;
using Spritely.Helpers;

namespace Spritely.Managers
{
    public class FileLockManager
    {
        private static readonly int MaxLocks = AppConstants.FileLockMaxLocks;
        private static readonly int MaxLocksPerTask = AppConstants.FileLockMaxLocksPerTask;
        private static readonly int LockWarningThreshold = (int)(AppConstants.FileLockMaxLocks * AppConstants.FileLockWarningThreshold);

        private readonly Dictionary<string, FileLock> _fileLocks = new();
        private readonly ObservableCollection<FileLock> _fileLocksView = new();
        private readonly Dictionary<string, HashSet<string>> _taskLockedFiles = new();
        private readonly Dictionary<string, QueuedTaskInfo> _queuedTaskInfo = new();
        private readonly Dictionary<string, FileLock> _waitingLocks = new();
        private readonly object _lockSync = new();
        private readonly Dispatcher _dispatcher;
        private bool _gitOperationInProgress = false;

        public event Action<string>? QueuedTaskResumed;
        public event Action<string>? TaskNeedsPause;
        public event Action? LocksChanged;

        public Dictionary<string, QueuedTaskInfo> QueuedTaskInfos => _queuedTaskInfo;
        public int LockCount { get { lock (_lockSync) { return _fileLocks.Count; } } }
        public ObservableCollection<FileLock> FileLocksView => _fileLocksView;

        public FileLockManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public bool TryAcquireOrConflict(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, Action<string, string> appendOutput)
        {
            // Validate file path - reject null, empty, "null" string, and /dev/null
            if (IsInvalidLockPath(filePath))
            {
                AppLogger.Warn("FileLockManager", $"Invalid file path in TryAcquireOrConflict: '{filePath}' for task {taskId}");
                return true; // Return true to not block the operation, just skip the lock
            }

            lock (_lockSync)
            {
                var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
                if (task?.IgnoreFileLocks == true)
                {
                    TryAcquireFileLockInternal(taskId, filePath, toolName, activeTasks, isIgnored: true);
                    return true;
                }
                if (!TryAcquireFileLockInternal(taskId, filePath, toolName, activeTasks))
                {
                    HandleFileLockConflictInternal(taskId, filePath, toolName, activeTasks, appendOutput);
                    return false;
                }
                return true;
            }
        }

        public bool TryAcquireFileLock(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, bool isIgnored = false)
        {
            lock (_lockSync)
            {
                return TryAcquireFileLockInternal(taskId, filePath, toolName, activeTasks, isIgnored);
            }
        }

        private bool TryAcquireFileLockInternal(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, bool isIgnored = false)
        {
            // Validate file path - reject null, empty, "null" string, and /dev/null
            if (IsInvalidLockPath(filePath))
            {
                AppLogger.Warn("FileLockManager", $"Invalid file path for lock acquisition: '{filePath}'");
                return false;
            }

            // Reject new lock acquisitions during git operations
            if (_gitOperationInProgress)
            {
                AppLogger.Info("FileLockManager", $"Rejecting lock acquisition for {filePath} - git operation in progress");
                return false;
            }

            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
            var basePath = task?.ProjectPath;
            var normalized = Helpers.FormatHelpers.NormalizePath(filePath, basePath);

            if (_fileLocks.TryGetValue(normalized, out var existing))
            {
                if (existing.OwnerTaskId == taskId)
                {
                    existing.ToolName = toolName;
                    existing.AcquiredAt = DateTime.Now;
                    existing.IsIgnored = isIgnored;
                    existing.OnPropertyChanged(nameof(FileLock.ToolName));
                    existing.OnPropertyChanged(nameof(FileLock.TimeText));
                    existing.OnPropertyChanged(nameof(FileLock.StatusText));
                    return true;
                }
                return false;
            }

            // Check if we've reached the maximum total lock count
            if (_fileLocks.Count >= MaxLocks)
            {
                AppLogger.Warn("FileLockManager", $"Maximum total lock count ({MaxLocks}) reached. Cannot acquire lock for {filePath} by task {taskId}");
                return false;
            }

            // Check if this task has reached its per-task lock limit
            if (_taskLockedFiles.TryGetValue(taskId, out var existingTaskFiles) && existingTaskFiles.Count >= MaxLocksPerTask)
            {
                AppLogger.Warn("FileLockManager", $"Task {taskId} has reached maximum lock count ({MaxLocksPerTask}). Cannot acquire lock for {filePath}");
                return false;
            }

            // Always store relative path for consistent display
            var relativePath = filePath;
            if (!string.IsNullOrEmpty(basePath) && Path.IsPathRooted(filePath))
            {
                try
                {
                    relativePath = Path.GetRelativePath(basePath, filePath);
                }
                catch
                {
                    // If GetRelativePath fails, fall back to original path
                    relativePath = filePath;
                }
            }

            var fileLock = new FileLock
            {
                NormalizedPath = normalized,
                OriginalPath = relativePath,
                OwnerTaskId = taskId,
                OwnerTaskNumber = task?.TaskNumber ?? 0,
                ToolName = toolName,
                AcquiredAt = DateTime.Now,
                IsIgnored = isIgnored
            };
            _fileLocks[normalized] = fileLock;

            // Warn when approaching the total lock limit
            if (_fileLocks.Count >= LockWarningThreshold)
            {
                AppLogger.Warn("FileLockManager", $"Lock count {_fileLocks.Count}/{MaxLocks} is above {AppConstants.FileLockWarningThreshold:P0} capacity");
            }

            if (!_taskLockedFiles.TryGetValue(taskId, out var files))
            {
                files = new HashSet<string>();
                _taskLockedFiles[taskId] = files;
            }
            files.Add(normalized);

            // Skip adding agent bus files to the UI view (but they're still tracked internally)
            bool isAgentBusFile = normalized.Contains("agent-bus", StringComparison.OrdinalIgnoreCase) ||
                                 filePath.Contains("agent-bus", StringComparison.OrdinalIgnoreCase);

            if (!isAgentBusFile)
            {
                _dispatcher.BeginInvoke(() =>
                {
                    _fileLocksView.Add(fileLock);
                    LocksChanged?.Invoke();
                });
            }
            else
            {
                _dispatcher.BeginInvoke(() =>
                {
                    LocksChanged?.Invoke();
                });
            }
            return true;
        }

        public void ReleaseTaskLocks(string taskId)
        {
            lock (_lockSync)
            {
                ReleaseTaskLocksInternal(taskId);
            }
        }

        private void ReleaseTaskLocksInternal(string taskId)
        {
            if (!_taskLockedFiles.TryGetValue(taskId, out var files))
            {
                // Even if no active locks, check for waiting locks
                if (_waitingLocks.TryGetValue(taskId, out var waitingLock))
                {
                    _waitingLocks.Remove(taskId);
                    _dispatcher.BeginInvoke(() =>
                    {
                        _fileLocksView.Remove(waitingLock);
                        LocksChanged?.Invoke();
                    });
                }
                return;
            }

            var removedLocks = new List<FileLock>();
            foreach (var path in files)
            {
                if (_fileLocks.TryGetValue(path, out var fl))
                {
                    _fileLocks.Remove(path);
                    removedLocks.Add(fl);
                }
            }
            _taskLockedFiles.Remove(taskId);

            // Also check for waiting locks
            if (_waitingLocks.TryGetValue(taskId, out var waiting))
            {
                _waitingLocks.Remove(taskId);
                removedLocks.Add(waiting);
            }

            _dispatcher.BeginInvoke(() =>
            {
                foreach (var fl in removedLocks)
                    _fileLocksView.Remove(fl);
                LocksChanged?.Invoke();
            });
        }

        public bool IsFileLocked(string normalizedPath)
        {
            lock (_lockSync)
            {
                return _fileLocks.ContainsKey(normalizedPath);
            }
        }

        /// <summary>
        /// Returns a snapshot of the normalized file paths currently locked by the given task.
        /// Call this BEFORE ReleaseTaskLocks to capture which files the task modified.
        /// </summary>
        public HashSet<string> GetTaskLockedFiles(string taskId)
        {
            lock (_lockSync)
            {
                if (_taskLockedFiles.TryGetValue(taskId, out var files))
                    return new HashSet<string>(files);
                return new HashSet<string>();
            }
        }

        public void HandleFileLockConflict(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, Action<string, string> appendOutput)
        {
            lock (_lockSync)
            {
                HandleFileLockConflictInternal(taskId, filePath, toolName, activeTasks, appendOutput);
            }
        }

        private void HandleFileLockConflictInternal(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, Action<string, string> appendOutput)
        {
            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            // Validate file path - don't handle conflicts for invalid paths
            if (IsInvalidLockPath(filePath))
            {
                AppLogger.Warn("FileLockManager", $"Invalid file path in HandleFileLockConflictInternal: '{filePath}'");
                return;
            }

            var normalized = Helpers.FormatHelpers.NormalizePath(filePath, task.ProjectPath);
            var blockingLock = _fileLocks.GetValueOrDefault(normalized);
            var blockingTaskId = blockingLock?.OwnerTaskId ?? "unknown";

            // Detect self-blocking: if the task is conflicting with its own lock, skip queuing
            if (blockingTaskId == taskId)
            {
                AppLogger.Warn("FileLockManager", $"Task {taskId} attempted to self-block on {filePath} - skipping conflict");
                return;
            }

            var blockerTask = activeTasks.FirstOrDefault(t => t.Id == blockingTaskId);
            // Use the task number stored on the lock as fallback when the blocking task
            // is no longer in activeTasks (already completed/removed)
            var blockerNum = blockerTask?.TaskNumber
                ?? (blockingLock?.OwnerTaskNumber is > 0 ? blockingLock.OwnerTaskNumber : (int?)null);

            // Important: Do NOT kill the process - we want to preserve the conversation state
            // The process will be paused/suspended so it can resume later
            AppLogger.Info("FileLockManager", $"File lock conflict for task #{task.TaskNumber} - preserving process for later resume");

            // Release any locks this task might have acquired before the conflict
            ReleaseTaskLocksInternal(taskId);

            // Queue immediately when file is locked
            appendOutput(taskId, $"\nFILE LOCK CONFLICT: {Path.GetFileName(filePath)} is locked by task #{blockerNum} ({toolName})\n");
            appendOutput(taskId, $"Pausing and queuing task #{task.TaskNumber} for auto-resume...\n");

            // First pause the task if it has a running process
            if (task.Process is { HasExited: false })
            {
                TaskNeedsPause?.Invoke(taskId);
            }

            task.Status = AgentTaskStatus.Queued;
            task.QueuedReason = $"File locked: {Path.GetFileName(filePath)} by #{blockerNum}";
            task.BlockedByTaskId = blockingTaskId;
            task.BlockedByTaskNumber = blockerNum;

            var blockedByIds = new HashSet<string> { blockingTaskId };
            _queuedTaskInfo[taskId] = new QueuedTaskInfo
            {
                Task = task,
                ConflictingFilePath = normalized,
                BlockingTaskId = blockingTaskId,
                BlockedByTaskIds = blockedByIds
            };

            // Always store relative path for consistent display
            var relativePath = filePath;
            var basePath = task?.ProjectPath;
            if (!string.IsNullOrEmpty(basePath) && Path.IsPathRooted(filePath))
            {
                try
                {
                    relativePath = Path.GetRelativePath(basePath, filePath);
                }
                catch
                {
                    // If GetRelativePath fails, fall back to original path
                    relativePath = filePath;
                }
            }

            // Create a "Waiting" file lock entry for the queued task
            var waitingLock = new FileLock
            {
                NormalizedPath = normalized,
                OriginalPath = relativePath,
                OwnerTaskId = taskId,
                OwnerTaskNumber = task?.TaskNumber ?? 0,
                ToolName = $"{toolName} (waiting for #{blockerNum})",
                AcquiredAt = DateTime.Now,
                IsWaiting = true
            };

            // Track the waiting lock
            _waitingLocks[taskId] = waitingLock;

            // Add to UI view to show in file locks panel
            bool isAgentBusFile = normalized.Contains("agent-bus", StringComparison.OrdinalIgnoreCase) ||
                                 filePath.Contains("agent-bus", StringComparison.OrdinalIgnoreCase);

            _dispatcher.BeginInvoke(() =>
            {
                if (!isAgentBusFile)
                    _fileLocksView.Add(waitingLock);
                LocksChanged?.Invoke();
            });
        }

        public void CheckQueuedTasks(ObservableCollection<AgentTask> activeTasks)
        {
            List<(string taskId, AgentTask task)> toResume;
            lock (_lockSync)
            {
                toResume = new List<(string, AgentTask)>();

                foreach (var kvp in _queuedTaskInfo)
                {
                    var qi = kvp.Value;
                    var allClear = true;
                    foreach (var blockerId in qi.BlockedByTaskIds)
                    {
                        var blocker = activeTasks.FirstOrDefault(t => t.Id == blockerId);
                        if (blocker != null && blocker.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning or AgentTaskStatus.Paused or AgentTaskStatus.Committing)
                        {
                            allClear = false;
                            break;
                        }
                    }

                    if (allClear && _fileLocks.ContainsKey(qi.ConflictingFilePath))
                        allClear = false;

                    if (allClear)
                        toResume.Add((kvp.Key, qi.Task));
                }

                // Don't remove from _queuedTaskInfo yet - wait until after successful resume
            }

            // Invoke the event outside the lock with validation
            foreach (var (taskId, task) in toResume)
            {
                // Validate task status immediately before invoking the event
                if (task.Status != AgentTaskStatus.Queued)
                {
                    AppLogger.Warn("FileLockManager", $"Task {taskId} status changed to {task.Status} before resume could be invoked");
                    continue;
                }

                // Only remove from queued info after confirming status is still Queued
                lock (_lockSync)
                {
                    _queuedTaskInfo.Remove(taskId);

                    // Remove the waiting lock from UI if it exists
                    if (_waitingLocks.TryGetValue(taskId, out var waitingLock))
                    {
                        _waitingLocks.Remove(taskId);
                        _dispatcher.BeginInvoke(() =>
                        {
                            _fileLocksView.Remove(waitingLock);
                            LocksChanged?.Invoke();
                        });
                    }
                }

                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                QueuedTaskResumed?.Invoke(taskId);
            }
        }

        public void ForceStartQueuedTask(AgentTask task)
        {
            lock (_lockSync)
            {
                _queuedTaskInfo.Remove(task.Id);

                // Remove the waiting lock from UI if it exists
                if (_waitingLocks.TryGetValue(task.Id, out var waitingLock))
                {
                    _waitingLocks.Remove(task.Id);
                    _dispatcher.BeginInvoke(() =>
                    {
                        _fileLocksView.Remove(waitingLock);
                        LocksChanged?.Invoke();
                    });
                }
            }

            // Validate task status before invoking the event
            if (task.Status != AgentTaskStatus.Queued)
                return;

            // Auto-bypass future file lock conflicts for force-started tasks
            task.IgnoreFileLocks = true;

            task.QueuedReason = null;
            task.BlockedByTaskId = null;
            QueuedTaskResumed?.Invoke(task.Id);
        }

        public void AddQueuedTaskInfo(string taskId, QueuedTaskInfo info)
        {
            lock (_lockSync)
            {
                _queuedTaskInfo[taskId] = info;
            }
        }

        public void RemoveQueuedInfo(string taskId)
        {
            lock (_lockSync)
            {
                _queuedTaskInfo.Remove(taskId);
            }
        }

        public void ClearAll()
        {
            lock (_lockSync)
            {
                _fileLocks.Clear();
                _taskLockedFiles.Clear();
                _queuedTaskInfo.Clear();
                _waitingLocks.Clear();
            }
            _dispatcher.BeginInvoke(() =>
            {
                _fileLocksView.Clear();
                LocksChanged?.Invoke();
            });
        }


        /// <summary>
        /// Sets the git operation in progress flag to prevent new lock acquisitions.
        /// This method is intended to be called by GitOperationGuard.
        /// </summary>
        public void SetGitOperationInProgress(bool inProgress)
        {
            lock (_lockSync)
            {
                _gitOperationInProgress = inProgress;
            }
        }

        /// <summary>
        /// Executes an action while ensuring no file locks can be acquired during the operation.
        /// Returns (success, errorMessage) where success indicates if the operation was executed.
        /// </summary>
        public async Task<(bool success, string? errorMessage)> ExecuteWhileNoLocksHeldAsync(Func<Task> action, string operationName)
        {
            lock (_lockSync)
            {
                // Check if any locks are active
                if (_fileLocks.Count > 0)
                {
                    return (false, $"Cannot {operationName} while file locks are active");
                }

                // Set flag to prevent new locks
                _gitOperationInProgress = true;
            }

            try
            {
                // Execute the action outside the lock to avoid blocking other operations
                await action();
                return (true, null);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FileLockManager", $"{operationName} failed", ex);
                return (false, $"{operationName} failed: {ex.Message}");
            }
            finally
            {
                lock (_lockSync)
                {
                    _gitOperationInProgress = false;
                }
            }
        }

        private static void KillProcess(AgentTask task)
        {
            try
            {
                if (task.Process is { HasExited: false })
                    task.Process.Kill(true);
            }
            catch (Exception ex) { AppLogger.Warn("FileLockManager", $"Failed to kill process for task {task.Id}", ex); }
        }

        private static bool IsInvalidLockPath(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return true;
            if (filePath.Equals("null", StringComparison.OrdinalIgnoreCase))
                return true;
            if (filePath.Contains("/dev/null", StringComparison.OrdinalIgnoreCase))
                return true;
            // Catch Windows-normalized forms like \dev\null
            if (filePath.EndsWith("\\dev\\null", StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals("dev\\null", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static string? TryExtractFilePathFromPartial(string partialJson)
        {
            var match = Regex.Match(partialJson, @"""file_path""\s*:\s*""([^""]+)""");
            if (match.Success)
            {
                var path = match.Groups[1].Value;
                // Reject "null" string as a valid file path
                if (path.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return path;
            }
            return null;
        }

        public static string? ExtractFilePath(JsonElement input)
        {
            if (input.TryGetProperty("file_path", out var fp))
            {
                var filePath = fp.GetString();
                // Reject "null" string as a valid file path
                if (filePath != null && filePath.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return filePath;
            }
            if (input.TryGetProperty("path", out var p))
            {
                var path = p.GetString();
                // Reject "null" string as a valid file path
                if (path != null && path.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return null;
                return path;
            }

            // For Bash commands, try to extract file paths from common file-modifying patterns
            if (input.TryGetProperty("command", out var cmd))
            {
                var command = cmd.GetString();
                if (!string.IsNullOrEmpty(command))
                {
                    // Match common patterns for file output redirection
                    // Handles: echo > file.txt, cat > file.txt, command >> file.txt
                    // Also handles quoted paths: > "file with spaces.txt", > 'file with spaces.txt'
                    var redirectMatch = Regex.Match(command, @">{1,2}\s*""([^""]+)""");
                    if (!redirectMatch.Success)
                        redirectMatch = Regex.Match(command, @">{1,2}\s*'([^']+)'");
                    if (!redirectMatch.Success)
                        redirectMatch = Regex.Match(command, @">{1,2}\s*([^\s;|&]+)");
                    if (redirectMatch.Success)
                    {
                        var redirectPath = redirectMatch.Groups[1].Value;
                        if (!redirectPath.Equals("/dev/null", StringComparison.OrdinalIgnoreCase))
                            return redirectPath;
                    }

                    // Match sed -i (in-place edit)
                    var sedMatch = Regex.Match(command, @"sed\s+.*-i[^\s]*\s+.*?['""]?([^\s'""]+)['""]?\s*$");
                    if (sedMatch.Success)
                        return sedMatch.Groups[1].Value;

                    // Match common file write patterns with explicit file paths
                    var fileMatch = Regex.Match(command, @"(?:echo|cat|printf|tee)\s+.*?['""]?([^\s|;&>]+\.[a-zA-Z0-9]+)['""]?");
                    if (fileMatch.Success && !fileMatch.Groups[1].Value.StartsWith("-"))
                        return fileMatch.Groups[1].Value;
                }
            }

            return null;
        }
    }
}
