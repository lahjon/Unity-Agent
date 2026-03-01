using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HappyEngine.Dialogs;
using HappyEngine.Managers;
using HappyEngine.Models;

namespace HappyEngine
{
    public partial class MainWindow
    {
        // ── Orchestration ──────────────────────────────────────────

        private void AddActiveTask(AgentTask task)
        {
            // Guard against duplicate insertions (can happen during concurrent spawning)
            if (_activeTasks.Any(t => t.Id == task.Id))
                return;

            // Pin Row 0 to pixel height to prevent layout jitter when populating the task list
            var topRow = RootGrid.RowDefinitions[0];
            if (topRow.ActualHeight > 0)
                topRow.Height = new GridLength(topRow.ActualHeight);

            task.TaskNumber = _nextTaskNumber;
            _nextTaskNumber = _nextTaskNumber >= 9999 ? 1 : _nextTaskNumber + 1;

            // Register with group tracker if this task belongs to a group
            _taskGroupTracker.RegisterTask(task);

            if (task.IsSubTask)
            {
                // Find the parent and insert immediately after it (and its existing children)
                var parentIndex = -1;
                for (int i = 0; i < _activeTasks.Count; i++)
                {
                    if (_activeTasks[i].Id == task.ParentTaskId)
                    {
                        parentIndex = i;
                        // SpawnSubtask already increments SubTaskCounter and adds to ChildTaskIds;
                        // only do it here for children created outside SpawnSubtask (e.g. feature mode execution tasks).
                        if (!_activeTasks[i].ChildTaskIds.Contains(task.Id))
                        {
                            _activeTasks[i].SubTaskCounter++;
                            _activeTasks[i].ChildTaskIds.Add(task.Id);
                        }
                        task.Runtime.SubTaskIndex = _activeTasks[i].SubTaskCounter;
                        break;
                    }
                }

                if (parentIndex >= 0)
                {
                    // Insert after parent and all its existing children
                    int insertAfter = parentIndex + 1;
                    while (insertAfter < _activeTasks.Count &&
                           _activeTasks[insertAfter].ParentTaskId == task.ParentTaskId)
                        insertAfter++;
                    _activeTasks.Insert(insertAfter, task);
                    RefreshActivityDashboard();
                    RestoreStarRow();
                    return;
                }
            }

            // Insert below all finished tasks so finished stay on top
            int insertIndex = 0;
            while (insertIndex < _activeTasks.Count && _activeTasks[insertIndex].IsFinished)
                insertIndex++;
            _activeTasks.Insert(insertIndex, task);
            RefreshActivityDashboard();
            RestoreStarRow();
        }

        /// <summary>Restores Row 0 to star sizing after layout settles, preventing resize jitter.</summary>
        private void RestoreStarRow()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            });
        }

        private void AnimateRemoval(FrameworkElement sender, Action onComplete)
        {
            // Walk up from the button to find the card Border (the DataTemplate root)
            FrameworkElement? card = sender;
            while (card != null && card is not ContentPresenter)
                card = VisualTreeHelper.GetParent(card) as FrameworkElement;
            if (card is ContentPresenter cp && VisualTreeHelper.GetChildrenCount(cp) > 0)
                card = VisualTreeHelper.GetChild(cp, 0) as FrameworkElement;

            if (card == null) { onComplete(); return; }

            card.IsHitTestVisible = false;

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var scaleY = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(100),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            card.RenderTransformOrigin = new Point(0.5, 0);
            card.RenderTransform = new ScaleTransform(1, 1);

            scaleY.Completed += (_, _) => onComplete();

            card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }

        /// <summary>
        /// Consolidates all task teardown: releases locks, removes queued/streaming state,
        /// moves from active list to history, closes the output tab, and resumes any
        /// queued or dependency-blocked tasks. Every code path that finishes a task
        /// should funnel through here.
        /// </summary>
        private void FinalizeTask(AgentTask task, bool closeTab = true)
        {
            // If this was a plan-only task that completed successfully, create a stored task
            if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                CreateStoredTaskFromPlan(task);

            // Release all resources associated with this task
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);

            // Move from active to history
            _activeTasks.Remove(task);
            _historyTasks.Insert(0, task);

            if (closeTab)
                _outputTabManager.CloseTab(task);

            _historyManager.SaveHistory(_historyTasks);
            RefreshActivityDashboard();
            RefreshFilterCombos();
            RefreshInlineProjectStats();
            UpdateStatus();

            // Resume tasks that were waiting on file locks or dependencies
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            _taskOrchestrator.OnTaskCompleted(task.Id);

            // Notify group tracker
            _taskGroupTracker.OnTaskCompleted(task);

            // Check if this task is a child of a feature mode coordinator.
            // Defer via BeginInvoke to prevent deeply nested FinalizeTask calls
            // (CheckFeatureModePhaseCompletion may call moveToHistory → FinalizeTask
            // for the parent, which would execute heavy UI work within this same
            // synchronous call stack and freeze the UI).
            if (task.ParentTaskId != null)
            {
                var parentId = task.ParentTaskId;
                Dispatcher.BeginInvoke(() =>
                {
                    var featureParent = _activeTasks.FirstOrDefault(t => t.Id == parentId);
                    if (featureParent is { IsFeatureMode: true, Status: AgentTaskStatus.Running })
                    {
                        _taskExecutionManager.CheckFeatureModePhaseCompletion(
                            featureParent, _activeTasks, _historyTasks, MoveToHistory);
                    }
                });
            }

            // Launch init-queued tasks now that a slot may be free
            DrainInitQueue();
        }

        // Keep MoveToHistory as a thin alias for callers that pass it as a delegate
        private void MoveToHistory(AgentTask task) => FinalizeTask(task);

        /// <summary>
        /// Transitions a task from a queued/waiting state to Running and starts its process.
        /// Consolidates the repeated pattern of setting status, recording start time,
        /// appending a status message, updating the tab header, and launching the process.
        /// </summary>
        private void LaunchTaskProcess(AgentTask task, string statusMessage)
        {
            task.Status = AgentTaskStatus.Running;
            task.StartTime = DateTime.Now;
            _outputTabManager.AppendOutput(task.Id, statusMessage, _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
        }

        /// <summary>
        /// Counts tasks that have an active Claude session (Running or Paused — not Queued/InitQueued/finished).
        /// </summary>
        private int CountActiveSessionTasks()
        {
            return _activeTasks.Count(t => t.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused);
        }

        /// <summary>
        /// Launches InitQueued tasks when slots become available under MaxConcurrentTasks.
        /// </summary>
        private void DrainInitQueue()
        {
            var max = _settingsManager.MaxConcurrentTasks;
            var toStart = new List<AgentTask>();

            foreach (var task in _activeTasks.OrderByDescending(t => (int)t.PriorityLevel).ThenByDescending(t => t.Priority))
            {
                if (task.Status != AgentTaskStatus.InitQueued) continue;
                if (CountActiveSessionTasks() >= max) break;

                toStart.Add(task);
            }

            foreach (var task in toStart)
            {
                task.QueuedReason = null;
                LaunchTaskProcess(task, $"[HappyEngine] Slot available — starting task #{task.TaskNumber}...\n\n");
            }

            if (toStart.Count > 0) UpdateStatus();
        }

        /// <summary>
        /// Called by the TaskOrchestrator when a task's dependencies are all resolved
        /// and it is ready to run.
        /// </summary>
        private void OnOrchestratorTaskReady(AgentTask task)
        {
            Dispatcher.Invoke(() =>
            {
                // Gather context from completed dependencies before clearing them
                var depSnapshot = task.DependencyTaskIds;
                if (depSnapshot.Count > 0)
                {
                    task.DependencyContext = _promptBuilder.BuildDependencyContext(
                        depSnapshot, _activeTasks, _historyTasks);
                }

                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.ClearDependencyTaskIds();
                task.DependencyTaskNumbers.Clear();

                if (task.Process is { HasExited: false })
                {
                    // Task was suspended via drag-drop — resume its process
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[HappyEngine] All dependencies resolved — resuming task #{task.TaskNumber}.\n\n",
                        _activeTasks, _historyTasks);
                }
                else
                {
                    LaunchTaskProcess(task, $"\n[HappyEngine] All dependencies resolved — starting task #{task.TaskNumber}...\n\n");
                }

                _outputTabManager.UpdateTabHeader(task);
                UpdateStatus();
            });
        }

        private void OnQueuedTaskResumed(string taskId)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            _outputTabManager.AppendOutput(taskId, $"\n[HappyEngine] Resuming task #{task.TaskNumber} (blocking task finished)...\n\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            UpdateStatus();
        }

        private void OnTaskProcessCompleted(string taskId)
        {
            // Move finished task to top of active list
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task is { IsFinished: true })
            {
                var idx = _activeTasks.IndexOf(task);
                if (idx > 0)
                    _activeTasks.Move(idx, 0);
            }

            // Auto-recovery: spawn a diagnostic child task for failed tasks
            if (task is { Status: AgentTaskStatus.Failed })
                _failureRecoveryManager.TrySpawnRecoveryTask(task, _activeTasks, _historyTasks);

            _taskOrchestrator.OnTaskCompleted(taskId);

            // Check if this completed/cancelled task is a child of a feature mode coordinator.
            // This is critical: without this check, the parent task never advances phases
            // because FinalizeTask (which also checks this) isn't called until much later.
            // Uses BeginInvoke to match FinalizeTask's deferred pattern and avoid reentrancy.
            if (task?.ParentTaskId != null)
            {
                var parentId = task.ParentTaskId;
                Dispatcher.BeginInvoke(() =>
                {
                    var featureParent = _activeTasks.FirstOrDefault(t => t.Id == parentId);
                    if (featureParent is { IsFeatureMode: true, Status: AgentTaskStatus.Running })
                    {
                        _taskExecutionManager.CheckFeatureModePhaseCompletion(
                            featureParent, _activeTasks, _historyTasks, MoveToHistory);
                    }
                });
            }
        }

        private void OnTaskGroupCompleted(object? sender, GroupCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var state = e.GroupState;
                var statusWord = state.FailedCount > 0 ? "with failures" : "successfully";
                DarkDialog.ShowAlert(
                    $"Task group \"{state.GroupName}\" completed {statusWord}.\n" +
                    $"{state.CompletedCount} completed, {state.FailedCount} failed out of {state.TotalCount} tasks.",
                    "Group Completed");
                GroupSummaryDialog.Show(state);
            });
        }

        private void OnSubTaskSpawned(AgentTask parent, AgentTask child)
        {
            Dispatcher.Invoke(() =>
            {
                AddActiveTask(child);
                _outputTabManager.CreateTab(child);

                _outputTabManager.AppendOutput(parent.Id,
                    $"\n[HappyEngine] Spawned subtask #{child.TaskNumber}: {child.Description}\n",
                    _activeTasks, _historyTasks);

                _outputTabManager.AppendOutput(child.Id,
                    $"[HappyEngine] Subtask of #{parent.TaskNumber}: {parent.Description}\n",
                    _activeTasks, _historyTasks);

                _outputTabManager.UpdateTabHeader(child);

                // If the child isn't queued, start it immediately;
                // otherwise register with the orchestrator so it starts when dependencies resolve.
                if (child.Status != AgentTaskStatus.Queued)
                {
                    _ = _taskExecutionManager.StartProcess(child, _activeTasks, _historyTasks, MoveToHistory);
                }
                else if (child.DependencyTaskIds.Count > 0)
                {
                    _taskOrchestrator.AddTask(child, child.DependencyTaskIds.ToList());
                }

                RefreshFilterCombos();
                UpdateStatus();
            });
        }

        private void OnMcpInvestigationRequested(AgentTask task)
        {
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);
            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            RefreshFilterCombos();
            UpdateStatus();
        }

        /// <summary>
        /// Creates a new task from a historic task's configuration and launches it.
        /// Works for any history task (failed, cancelled, or completed).
        /// </summary>
        private void RetryTask(AgentTask historicTask)
        {
            var prompt = historicTask.StoredPrompt ?? historicTask.Description;
            var newTask = _taskFactory.CreateTask(
                prompt,
                historicTask.ProjectPath,
                historicTask.SkipPermissions,
                historicTask.RemoteSession,
                historicTask.Headless,
                historicTask.IsFeatureMode,
                historicTask.IgnoreFileLocks,
                historicTask.UseMcp,
                historicTask.SpawnTeam,
                historicTask.ExtendedPlanning,
                historicTask.NoGitWrite,
                historicTask.PlanOnly,
                historicTask.UseMessageBus,
                model: historicTask.Model);
            newTask.ProjectColor = historicTask.ProjectColor;
            newTask.ProjectDisplayName = historicTask.ProjectDisplayName;
            newTask.AdditionalInstructions = historicTask.AdditionalInstructions;
            newTask.Summary = _taskFactory.GenerateLocalSummary(prompt);

            AddActiveTask(newTask);
            _outputTabManager.CreateTab(newTask);
            _outputTabManager.AppendOutput(newTask.Id,
                $"[HappyEngine] Re-running task #{historicTask.TaskNumber}\n", _activeTasks, _historyTasks);

            _ = _taskExecutionManager.StartProcess(newTask, _activeTasks, _historyTasks, MoveToHistory);
            UpdateStatus();
        }

        /// <summary>
        /// Consolidates the post-creation launch sequence shared by Execute_Click
        /// and the suggestion execution handler.  Adds the task to the active list,
        /// creates its output tab, handles dependency registration and concurrency
        /// limits, and either starts the process immediately or queues it.
        /// </summary>
        private void LaunchTask(AgentTask task, List<AgentTask>? dependencies = null)
        {
            task.Summary ??= _taskFactory.GenerateLocalSummary(task.Description);
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            var activeDeps = dependencies?.Where(d => !d.IsFinished).ToList();

            if (activeDeps is { Count: > 0 })
            {
                task.DependencyTaskIds = activeDeps.Select(d => d.Id).ToList();
                task.DependencyTaskNumbers = activeDeps.Select(d => d.TaskNumber).ToList();

                // Register with orchestrator so it tracks the DAG edges
                _taskOrchestrator.AddTask(task, task.DependencyTaskIds.ToList());

                if (!task.PlanOnly)
                {
                    // Start in plan mode first, then queue when planning completes
                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    task.Status = AgentTaskStatus.Planning;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Dependencies pending ({string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}) — starting in plan mode...\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else
                {
                    // User explicitly wants plan-only — queue as before
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Waiting for dependencies";
                    task.BlockedByTaskId = activeDeps[0].Id;
                    task.BlockedByTaskNumber = activeDeps[0].TaskNumber;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[HappyEngine] Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
            }
            else if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
            {
                // Max concurrent sessions reached — init-queue (no Claude session yet)
                task.Status = AgentTaskStatus.InitQueued;
                task.QueuedReason = "Max concurrent tasks reached";
                _outputTabManager.AppendOutput(task.Id,
                    $"[HappyEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            RefreshFilterCombos();
            UpdateStatus();
        }

        private void OnProjectRenamed(string projectPath, string newName)
        {
            foreach (var task in _activeTasks.Concat(_historyTasks).Concat(_storedTasks)
                         .Where(t => t.ProjectPath == projectPath))
            {
                task.ProjectDisplayName = newName;
            }
            _historyManager.SaveHistory(_historyTasks);
            _historyManager.SaveStoredTasks(_storedTasks);
        }
    }
}
