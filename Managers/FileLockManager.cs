using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    public class FileLockManager
    {
        private readonly Dictionary<string, FileLock> _fileLocks = new();
        private readonly ObservableCollection<FileLock> _fileLocksView = new();
        private readonly Dictionary<string, HashSet<string>> _taskLockedFiles = new();
        private readonly Dictionary<string, QueuedTaskInfo> _queuedTaskInfo = new();
        private readonly object _lockSync = new();
        private readonly TextBlock _fileLockBadge;
        private readonly Dispatcher _dispatcher;

        public event Action<string>? QueuedTaskResumed;

        public Dictionary<string, QueuedTaskInfo> QueuedTaskInfos => _queuedTaskInfo;
        public int LockCount { get { lock (_lockSync) { return _fileLocks.Count; } } }
        public ObservableCollection<FileLock> FileLocksView => _fileLocksView;

        public FileLockManager(TextBlock fileLockBadge, Dispatcher dispatcher)
        {
            _fileLockBadge = fileLockBadge;
            _dispatcher = dispatcher;
        }

        public bool TryAcquireOrConflict(string taskId, string filePath, string toolName,
            ObservableCollection<AgentTask> activeTasks, Action<string, string> appendOutput)
        {
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
            var task = activeTasks.FirstOrDefault(t => t.Id == taskId);
            var basePath = task?.ProjectPath;
            var normalized = TaskLauncher.NormalizePath(filePath, basePath);

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

            var fileLock = new FileLock
            {
                NormalizedPath = normalized,
                OriginalPath = filePath,
                OwnerTaskId = taskId,
                ToolName = toolName,
                AcquiredAt = DateTime.Now,
                IsIgnored = isIgnored
            };
            _fileLocks[normalized] = fileLock;
            _fileLocksView.Add(fileLock);

            if (!_taskLockedFiles.TryGetValue(taskId, out var files))
            {
                files = new HashSet<string>();
                _taskLockedFiles[taskId] = files;
            }
            files.Add(normalized);

            UpdateFileLockBadge();
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
            if (!_taskLockedFiles.TryGetValue(taskId, out var files)) return;

            foreach (var path in files)
            {
                if (_fileLocks.TryGetValue(path, out var fl))
                {
                    _fileLocks.Remove(path);
                    _fileLocksView.Remove(fl);
                }
            }
            _taskLockedFiles.Remove(taskId);
            UpdateFileLockBadge();
        }

        public bool IsFileLocked(string normalizedPath)
        {
            lock (_lockSync)
            {
                return _fileLocks.ContainsKey(normalizedPath);
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

            var normalized = TaskLauncher.NormalizePath(filePath, task.ProjectPath);
            var blockingLock = _fileLocks.GetValueOrDefault(normalized);
            var blockingTaskId = blockingLock?.OwnerTaskId ?? "unknown";
            var blockerTask = activeTasks.FirstOrDefault(t => t.Id == blockingTaskId);
            var blockerNum = blockerTask?.TaskNumber;

            KillProcess(task);
            ReleaseTaskLocksInternal(taskId);

            if (string.IsNullOrEmpty(task.StoredPrompt) && !task.IsPlanningBeforeQueue)
            {
                // No plan yet — restart in plan mode before queuing
                appendOutput(taskId, $"\n[HappyEngine] FILE LOCK CONFLICT: {Path.GetFileName(filePath)} is locked by task #{blockerNum}. Restarting in plan mode...\n");
                task.NeedsPlanRestart = true;
                task.PlanOnly = true;
                task.PendingFileLockPath = normalized;
                task.PendingFileLockBlocker = blockingTaskId;
            }
            else
            {
                // Already has a plan — queue immediately
                appendOutput(taskId, $"\n[HappyEngine] FILE LOCK CONFLICT: {Path.GetFileName(filePath)} is locked by task #{blockerNum} ({toolName})\n");
                appendOutput(taskId, $"[HappyEngine] Queuing task #{task.TaskNumber} for auto-resume...\n");

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
            }
        }

        public void CheckQueuedTasks(ObservableCollection<AgentTask> activeTasks)
        {
            List<(string taskId, QueuedTaskInfo qi)> toResume;
            lock (_lockSync)
            {
                toResume = new List<(string, QueuedTaskInfo)>();

                foreach (var kvp in _queuedTaskInfo)
                {
                    var qi = kvp.Value;
                    var allClear = true;
                    foreach (var blockerId in qi.BlockedByTaskIds)
                    {
                        var blocker = activeTasks.FirstOrDefault(t => t.Id == blockerId);
                        if (blocker != null && blocker.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused)
                        {
                            allClear = false;
                            break;
                        }
                    }

                    if (allClear && _fileLocks.ContainsKey(qi.ConflictingFilePath))
                        allClear = false;

                    if (allClear)
                        toResume.Add((kvp.Key, qi));
                }

                foreach (var (taskId, _) in toResume)
                    _queuedTaskInfo.Remove(taskId);
            }

            foreach (var (taskId, qi) in toResume)
            {
                var task = qi.Task;
                task.Status = AgentTaskStatus.Running;
                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.StartTime = DateTime.Now;

                QueuedTaskResumed?.Invoke(taskId);
            }
        }

        public void ForceStartQueuedTask(AgentTask task)
        {
            lock (_lockSync)
            {
                _queuedTaskInfo.Remove(task.Id);
            }
            task.Status = AgentTaskStatus.Running;
            task.QueuedReason = null;
            task.BlockedByTaskId = null;
            task.BlockedByTaskNumber = null;
            task.StartTime = DateTime.Now;

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
                _fileLocksView.Clear();
                _taskLockedFiles.Clear();
                _queuedTaskInfo.Clear();
            }
        }

        private void UpdateFileLockBadge()
        {
            var count = _fileLocks.Count;
            _fileLockBadge.Text = count.ToString();
            _fileLockBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        public static string? TryExtractFilePathFromPartial(string partialJson)
        {
            var match = Regex.Match(partialJson, @"""file_path""\s*:\s*""([^""]+)""");
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string? ExtractFilePath(JsonElement input)
        {
            if (input.TryGetProperty("file_path", out var fp))
                return fp.GetString();
            if (input.TryGetProperty("path", out var p))
                return p.GetString();
            return null;
        }
    }
}
