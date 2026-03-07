using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Spritely.Dialogs;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Named handlers for anonymous lambdas (needed for cleanup) ──

        private void OnProjectSwapStarted() => LoadingOverlay.Visibility = Visibility.Visible;
        private void OnProjectSwapCompleted()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;

            _gitPanelManager.MarkDirty();
            _activityDashboard.MarkDirty();

            _gitPanelManager.RefreshIfNeeded(GitTabContent);

            if (StatisticsTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent, _projectManager.ProjectPath);

            UpdateFileLocks();
            LoadTasksForDisplay();
            UpdateNoProjectState();

            _ = ReloadTemplatesForProjectAsync();
            _ = LoadSkillsAsync();
            _ = LoadFeaturesAsync();
        }

        private async Task ReloadTemplatesForProjectAsync()
        {
            await _settingsManager.LoadTemplatesAsync(_projectManager.ProjectPath);
            RenderTemplateCombo();
        }

        private void UpdateFileLocks()
        {
            if (_fileLocksView != null)
            {
                var currentProjectPath = _projectManager.ProjectPath;
                _fileLocksView.Filter = (obj) =>
                {
                    if (obj is FileLock fileLock)
                    {
                        if (fileLock.OriginalPath.Equals("null", StringComparison.OrdinalIgnoreCase))
                            return false;

                        var ownerTask = _activeTasks.FirstOrDefault(t => t.Id == fileLock.OwnerTaskId);
                        if (ownerTask != null)
                        {
                            return NormalizePath(ownerTask.ProjectPath) == NormalizePath(currentProjectPath);
                        }
                    }
                    return false;
                };
                _fileLocksView.Refresh();

                var visibleCount = 0;
                foreach (var item in _fileLocksView)
                {
                    visibleCount++;
                }

                FileLockBadge.Text = visibleCount.ToString();
                FileLockBadge.Visibility = visibleCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        private void OnCollectionChangedUpdateTabs(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateTabCounts();
            if (sender == _activeTasks)
                UpdateFileLocks();
        }

        // ── Window Close ───────────────────────────────────────────

        private void UnsubscribeAllEvents()
        {
            _fileLockManager.QueuedTaskResumed -= OnQueuedTaskResumed;
            _fileLockManager.TaskNeedsPause -= OnTaskNeedsPause;

            _outputTabManager.TabCloseRequested -= OnTabCloseRequested;
            _outputTabManager.TabStoreRequested -= OnTabStoreRequested;
            _outputTabManager.TabResumeRequested -= OnTabResumeRequested;
            _outputTabManager.TabExportRequested -= OnTabExportRequested;
            _outputTabManager.InputSent -= OnTabInputSent;
            _outputTabManager.InterruptInputSent -= OnTabInterruptInputSent;

            _projectManager.McpInvestigationRequested -= OnMcpInvestigationRequested;
            _projectManager.McpOutputChanged -= OnMcpOutputChanged;
            _projectManager.ProjectSwapStarted -= OnProjectSwapStarted;
            _projectManager.ProjectSwapCompleted -= OnProjectSwapCompleted;
            _projectManager.ProjectRenamed -= OnProjectRenamed;

            _helperManager.GenerationStarted -= OnHelperGenerationStarted;
            _helperManager.GenerationCompleted -= OnHelperGenerationCompleted;
            _helperManager.GenerationFailed -= OnHelperGenerationFailed;

            _taskExecutionManager.TaskCompleted -= OnTaskProcessCompleted;
            _taskExecutionManager.SubTaskSpawned -= OnSubTaskSpawned;

            _taskOrchestrator.TaskReady -= OnOrchestratorTaskReady;

            _taskGroupTracker.GroupCompleted -= OnTaskGroupCompleted;

            _activeTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;
            _historyTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;
            _storedTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;

            MainTabs.SelectionChanged -= MainTabs_SelectionChanged;
            StatisticsTabs.SelectionChanged -= StatisticsTabs_SelectionChanged;
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            var runningCount = _activeTasks.Count(t =>
                t.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused);

            if (runningCount > 0)
            {
                if (!DarkDialog.ShowConfirm(
                    $"There are {runningCount} task(s) still running.\n\n" +
                    "Closing will terminate all of them. Continue?",
                    "Active Tasks Running"))
                {
                    e.Cancel = true;
                    return;
                }
            }

            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            UnsubscribeAllEvents();

            _statusTimer.Stop();
            _periodicSaveTimer.Stop();
            _helperAnimTimer?.Stop();

            _windowCts.Cancel();
            _chatManager.Dispose();

            _helperManager?.Dispose();

            _projectManager.SaveProjects();
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
            _historyManager.SaveHistory(_historyTasks);
            _historyManager.SaveActiveQueue(_activeTasks, _activeTasksLock);
            PersistSavedPrompts();

            Managers.SafeFileWriter.FlushAll(timeoutMs: 5000);

            _projectManager.StopAllMcpServers();
            _mcpHealthMonitor?.Stop();
            _mcpHealthMonitor?.Dispose();

            foreach (var task in _activeTasks)
            {
                TaskExecutionManager.KillProcess(task);
                task.Runtime.Dispose();
            }

            _messageBusManager.Dispose();
            _fileLockManager.ClearAll();
            _taskExecutionManager.StreamingToolState.Clear();

            _terminalManager?.Dispose();

            _claudeService.Dispose();
            _geminiService.Dispose();

            _windowCts.Dispose();
        }
    }
}
