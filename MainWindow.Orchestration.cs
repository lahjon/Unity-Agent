using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Spritely.Dialogs;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Orchestration ──────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private void AddActiveTask(AgentTask task)
        {
            // Guard against duplicate insertions (can happen during concurrent spawning)
            if (_activeTasks.Any(t => t.Id == task.Id))
                return;

            PinRowHeights();

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
                    RestoreStarRows();
                    return;
                }
            }

            // Insert below all finished tasks so finished stay on top
            int insertIndex = 0;
            while (insertIndex < _activeTasks.Count && _activeTasks[insertIndex].IsFinished)
                insertIndex++;
            _activeTasks.Insert(insertIndex, task);
            RefreshActivityDashboard();
            RestoreStarRows();
        }

        /// <summary>
        /// Pins both star rows (0 and 2) to their current pixel heights to prevent
        /// layout jitter during operations that add/remove items or toggle visibility.
        /// Call <see cref="RestoreStarRows"/> afterwards to restore proportional sizing.
        /// </summary>
        private void PinRowHeights()
        {
            var topRow = RootGrid.RowDefinitions[0];
            var bottomRow = RootGrid.RowDefinitions[2];
            if (topRow.ActualHeight > 0)
                topRow.Height = new GridLength(topRow.ActualHeight);
            if (bottomRow.ActualHeight > 0)
                bottomRow.Height = new GridLength(bottomRow.ActualHeight);
        }

        /// <summary>Restores both star rows to proportional sizing after layout settles, preserving their current ratio.</summary>
        private void RestoreStarRows()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var topRow = RootGrid.RowDefinitions[0];
                var bottomRow = RootGrid.RowDefinitions[2];
                double totalHeight = topRow.ActualHeight + bottomRow.ActualHeight;
                if (totalHeight <= 0) return;
                topRow.Height = new GridLength(topRow.ActualHeight / totalHeight, GridUnitType.Star);
                bottomRow.Height = new GridLength(bottomRow.ActualHeight / totalHeight, GridUnitType.Star);
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
            // Recommendation tasks stay in the active list so the user can click Continue
            // without restarting a new session. The task's ConversationId is preserved for --resume.
            if (task.Status == AgentTaskStatus.Recommendation)
            {
                // Release file locks so other tasks aren't blocked, but keep the task active
                _fileLockManager.ReleaseTaskLocks(task.Id);
                _fileLockManager.RemoveQueuedInfo(task.Id);
                _taskExecutionManager.RemoveStreamingState(task.Id);
                _outputTabManager.UpdateTabHeader(task);
                RefreshActivityDashboard();
                UpdateStatus();

                // Show notification if window is in background
                try
                {
                    var foregroundWindow = GetForegroundWindow();
                    var currentWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (foregroundWindow != currentWindowHandle && foregroundWindow != IntPtr.Zero)
                    {
                        var title = $"Task #{task.TaskNumber} has recommendations";
                        var message = task.Description.Length > 100
                            ? task.Description.Substring(0, 97) + "..."
                            : task.Description;
                        App.ShowBalloonNotification(title, message, System.Windows.Forms.ToolTipIcon.Info);
                    }
                }
                catch { /* notification is best-effort */ }

                // Resume tasks waiting on file locks or dependencies
                _fileLockManager.CheckQueuedTasks(_activeTasks);
                DrainInitQueue();
                return;
            }

            // If this was a plan-only task that completed successfully, create a stored task
            if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                CreateStoredTaskFromPlan(task);

            // Check if we should enter the Committing phase for Auto-Commit
            var shouldAutoCommit = _settingsManager.AutoCommit &&
                                   task.Status == AgentTaskStatus.Completed &&
                                   !task.IsCommitted;

            if (shouldAutoCommit)
            {
                // Capture the locked files before any cleanup
                var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                if (lockedFiles.Count > 0)
                {
                    // Store locked files in task runtime state for the commit
                    task.Runtime.LockedFilesForCommit = lockedFiles;

                    // Persist changed file paths on the task so they survive across sessions
                    PersistChangedFiles(task, lockedFiles);
                }

                // Even without file locks, attempt commit if the task has a valid GitStartHash —
                // file locks may be empty when changes are made via Bash or if streaming parser missed tools
                bool hasChangesToCommit = lockedFiles.Count > 0 || !string.IsNullOrEmpty(task.GitStartHash);

                if (hasChangesToCommit)
                {
                    // Transition to Committing — task stays in _activeTasks with locks held
                    task.Status = AgentTaskStatus.Committing;
                    _fileLockManager.RemoveQueuedInfo(task.Id);
                    _taskExecutionManager.RemoveStreamingState(task.Id);
                    _outputTabManager.UpdateTabHeader(task);
                    RefreshActivityDashboard();

                    // Run commit on background thread
                    task.Runtime.PendingCommitTask = Task.Run(async () =>
                    {
                        try
                        {
                            var (success, errorMessage) = await CommitTaskAsync(task);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!success)
                                {
                                    task.CommitError = errorMessage ?? "Failed to commit changes";
                                }
                                // Whether commit succeeded or failed, finish the task
                                FinishTaskAfterCommit(task, closeTab);
                            });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Auto-commit failed for task {task.Id}: {ex.Message}");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                task.CommitError = $"Commit error: {ex.Message}";
                                FinishTaskAfterCommit(task, closeTab);
                            });
                        }
                    });
                    return; // Don't proceed to teardown yet — FinishTaskAfterCommit will handle it
                }
                // else: No files modified and no git start hash — fall through to normal teardown
            }

            // Normal teardown: release locks and move to history
            PerformTaskTeardown(task, closeTab);
        }

        /// <summary>
        /// Shared teardown logic: releases remaining resources, moves task from active to history,
        /// fires notifications, and resumes queued/dependency-blocked tasks.
        /// </summary>
        private void PerformTaskTeardown(AgentTask task, bool closeTab = true)
        {
            // Persist changed files before releasing locks so manual commit can find them later
            if (task.ChangedFiles.Count == 0)
            {
                var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                if (lockedFiles.Count > 0)
                {
                    PersistChangedFiles(task, lockedFiles);
                }
                else if (!string.IsNullOrEmpty(task.GitStartHash))
                {
                    // Fall back to git diff when file locks didn't capture changes
                    _ = PersistChangedFilesFromGitAsync(task);
                }
            }

            // Snapshot output so it survives restart (OutputBuilder is in-memory only)
            if (string.IsNullOrEmpty(task.FullOutput) && task.OutputBuilder.Length > 0)
                task.FullOutput = task.OutputBuilder.ToString();

            // Release all resources associated with this task
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);

            // Move from active to history (skip if already present to avoid duplicates)
            _activeTasks.Remove(task);
            if (!_historyTasks.Contains(task))
                _historyTasks.Insert(0, task);

            if (closeTab)
                _outputTabManager.CloseTab(task);

            _historyManager.SaveHistory(_historyTasks);
            RefreshActivityDashboard();
            RefreshFilterCombos();
            RefreshInlineProjectStats();
            UpdateStatus();

            // Show notification if window is not in foreground
            try
            {
                var foregroundWindow = GetForegroundWindow();
                var currentWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

                if (foregroundWindow != currentWindowHandle && foregroundWindow != IntPtr.Zero)
                {
                    // Window is in background, show notification
                    var title = $"Task #{task.TaskNumber} {task.Status}";
                    var message = task.Description.Length > 100
                        ? task.Description.Substring(0, 97) + "..."
                        : task.Description;

                    var icon = task.Status == AgentTaskStatus.Completed
                        ? System.Windows.Forms.ToolTipIcon.Info
                        : task.Status == AgentTaskStatus.Failed
                            ? System.Windows.Forms.ToolTipIcon.Error
                            : System.Windows.Forms.ToolTipIcon.Warning;

                    App.ShowBalloonNotification(title, message, icon);
                }
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("Orchestration", $"Failed to show task completion notification: {ex.Message}");
            }

            // Resume tasks that were waiting on file locks or dependencies
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            _taskOrchestrator.OnTaskCompleted(task.Id);

            // Clean up orchestrator node now that dependents have been unblocked
            _taskOrchestrator.RemoveTask(task.Id);

            // Notify group tracker
            _taskGroupTracker.OnTaskCompleted(task);

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
                LaunchTaskProcess(task, $"Slot available — starting task #{task.TaskNumber}...\n\n");
            }

            // Update queue positions for remaining InitQueued tasks
            UpdateQueuePositions();

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
                    // Persist the dependency context so it survives app restart
                    task.Data.DependencyContext = task.DependencyContext;
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
                        $"\nAll dependencies resolved — resuming task #{task.TaskNumber}.\n\n",
                        _activeTasks, _historyTasks);
                }
                else
                {
                    LaunchTaskProcess(task, $"\nAll dependencies resolved — starting task #{task.TaskNumber}...\n\n");
                }

                _outputTabManager.UpdateTabHeader(task);
                UpdateStatus();
            });
        }

        private void OnQueuedTaskResumed(string taskId)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            AppLogger.Debug("OnQueuedTaskResumed", $"Task #{task.TaskNumber} - ConversationId: {task.ConversationId}, Process: {task.Process?.Id}, HasExited: {task.Process?.HasExited}");

            _outputTabManager.AppendOutput(taskId, $"\nResuming task #{task.TaskNumber} (blocking task finished)...\n\n", _activeTasks, _historyTasks);

            var resumed = false;

            // Try to resume existing suspended process
            if (task.Process is { HasExited: false })
            {
                AppLogger.Info("OnQueuedTaskResumed", $"Resuming existing process for task #{task.TaskNumber}");
                _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);

                // ResumeTask may fail if process exited between our check and the resume attempt.
                // If status changed away from Queued, the resume succeeded.
                resumed = task.Status != AgentTaskStatus.Queued;
                if (resumed)
                {
                    task.QueuedReason = null;
                    task.BlockedByTaskId = null;
                    task.BlockedByTaskNumber = null;
                    if (task.StartTime == DateTime.MinValue)
                        task.StartTime = DateTime.Now;
                    _outputTabManager.UpdateTabHeader(task);
                }
                else
                {
                    AppLogger.Warn("OnQueuedTaskResumed", $"ResumeTask did not change status for task #{task.TaskNumber} — falling through to new process");
                }
            }

            // Start a new process if resume didn't work or no process existed
            if (!resumed)
            {
                AppLogger.Info("OnQueuedTaskResumed", $"Starting new process for task #{task.TaskNumber}");
                task.Status = AgentTaskStatus.Running;
                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.StartTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);

                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            UpdateStatus();
        }

        private void OnTaskNeedsPause(string taskId)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            AppLogger.Debug("OnTaskNeedsPause", $"Pausing task #{task.TaskNumber} due to file lock conflict");
            _taskExecutionManager.PauseTask(task);
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

            // Auto-finalize when auto-commit is on: trigger git commit and release file locks.
            // Without this, FinalizeTask only runs when the user manually dismisses the card,
            // so auto-commit never fires and locks stay held indefinitely.
            // Recommendation tasks are always finalized (to release locks and stay in active list).
            if (task is { IsFinished: true } && (_settingsManager.AutoCommit || task.Status == AgentTaskStatus.Recommendation))
            {
                FinalizeTask(task, closeTab: false);
            }
            else if (task is { IsFinished: true })
            {
                // Without auto-commit, release file locks immediately so queued tasks can proceed.
                // The task card stays in the active list for the user to review/dismiss manually.
                _fileLockManager.ReleaseTaskLocks(task.Id);
                _fileLockManager.CheckQueuedTasks(_activeTasks);
            }

            _taskOrchestrator.OnTaskCompleted(taskId);
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
                    $"\nSpawned subtask #{child.TaskNumber}: {child.Description}\n",
                    _activeTasks, _historyTasks);

                _outputTabManager.AppendOutput(child.Id,
                    $"Subtask of #{parent.TaskNumber}: {parent.Description}\n",
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

        private void OnMcpOutputChanged(string projectPath)
        {
            // Only update if it's for the current project
            if (projectPath != _projectManager.ProjectPath) return;

            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry?.McpOutput != null)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    McpOutputTextBox.Text = entry.McpOutput.ToString();

                    // Auto-scroll to bottom
                    McpOutputScrollViewer.ScrollToEnd();
                });
            }

            // Handle MCP health monitoring
            if (entry != null)
            {
                if (entry.McpStatus == Models.McpStatus.Connected)
                {
                    // Start health monitoring if not already started
                    if (_mcpHealthMonitor == null || _mcpHealthMonitor.McpStatus == McpHealthStatus.Disconnected)
                    {
                        _mcpHealthMonitor?.Dispose();
                        _mcpHealthMonitor = new McpHealthMonitor(projectPath, _projectManager);
                        _mcpHealthMonitor.McpStatusChanged += OnMcpHealthStatusChanged;
                        _mcpHealthMonitor.Start();
                    }
                }
                else if (entry.McpStatus == Models.McpStatus.NotConnected || entry.McpStatus == Models.McpStatus.Disabled)
                {
                    // Stop health monitoring
                    _mcpHealthMonitor?.Stop();
                    _mcpHealthMonitor?.Dispose();
                    _mcpHealthMonitor = null;
                    UpdateMcpHealthIndicator(McpHealthStatus.Disconnected, "");
                }
            }
        }

        private void OnMcpHealthStatusChanged(McpHealthStatus status, string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateMcpHealthIndicator(status, message);

                // Log the status change
                AppLogger.Info("McpHealthMonitor", $"MCP health status: {status} - {message}");
            });
        }

        private void UpdateMcpHealthIndicator(McpHealthStatus status, string message)
        {
            if (McpHealthIndicator == null) return;

            var (color, tooltip) = status switch
            {
                McpHealthStatus.Connected => (FindResource("SuccessGreen") as System.Windows.Media.Brush, "MCP connected"),
                McpHealthStatus.Reconnecting => (FindResource("WarningAmber") as System.Windows.Media.Brush, "MCP reconnecting..."),
                McpHealthStatus.Disconnected => (FindResource("TextMuted") as System.Windows.Media.Brush, "MCP disconnected"),
                _ => (FindResource("TextMuted") as System.Windows.Media.Brush, "MCP status unknown")
            };

            McpHealthIndicator.Fill = color ?? System.Windows.Media.Brushes.Gray;
            McpHealthIndicator.ToolTip = string.IsNullOrEmpty(message) ? tooltip : $"{tooltip}: {message}";

            // Add pulsing animation for reconnecting state
            if (status == McpHealthStatus.Reconnecting)
            {
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0.3,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromSeconds(1)),
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                McpHealthIndicator.BeginAnimation(UIElement.OpacityProperty, animation);
            }
            else
            {
                McpHealthIndicator.BeginAnimation(UIElement.OpacityProperty, null);
                McpHealthIndicator.Opacity = 1.0;
            }
        }

        /// <summary>
        /// Creates a new task from a historic task's configuration and launches it.
        /// Works for any history task (failed, cancelled, or completed).
        /// </summary>
        private void RetryTask(AgentTask historicTask)
        {
            var originalPrompt = historicTask.StoredPrompt ?? historicTask.Description;
            var retryPrompt = $"RETRY: {originalPrompt}";

            // Build additional instructions with previous completion summary
            var additionalInstructions = historicTask.AdditionalInstructions ?? "";
            if (!string.IsNullOrWhiteSpace(historicTask.CompletionSummary))
            {
                if (!string.IsNullOrWhiteSpace(additionalInstructions))
                    additionalInstructions += "\n\n";
                additionalInstructions += $"Previous attempt summary:\n{historicTask.CompletionSummary}";
            }

            var newTask = _taskFactory.CreateTask(
                retryPrompt,
                historicTask.ProjectPath,
                historicTask.SkipPermissions,
                historicTask.Headless,
                historicTask.IsFeatureMode,
                historicTask.IgnoreFileLocks,
                historicTask.UseMcp,
                historicTask.SpawnTeam,
                historicTask.ExtendedPlanning,
                historicTask.PlanOnly,
                historicTask.UseMessageBus,
                model: historicTask.Model);
            newTask.ProjectColor = historicTask.ProjectColor;
            newTask.ProjectDisplayName = historicTask.ProjectDisplayName;
            newTask.AdditionalInstructions = additionalInstructions;
            newTask.TimeoutMinutes = historicTask.TimeoutMinutes;
            newTask.Summary = _taskFactory.GenerateLocalSummary(retryPrompt);

            AddActiveTask(newTask);
            _outputTabManager.CreateTab(newTask);
            _outputTabManager.AppendOutput(newTask.Id,
                $"Re-running task #{historicTask.TaskNumber}\n", _activeTasks, _historyTasks);

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
                        $"Dependencies pending ({string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}) — starting in plan mode...\n",
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
                        $"Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}\n",
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
                    $"Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            RefreshFilterCombos();
            UpdateQueuePositions();
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
