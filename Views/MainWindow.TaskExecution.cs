using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spritely.Dialogs;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Toggle Helpers ─────────────────────────────────────────

        private void ResetPerTaskToggles()
        {
            SpawnTeamToggle.IsChecked = false;
            TeamsModeToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;
            AutoDecomposeToggle.IsChecked = false;
            ApplyFixToggle.IsChecked = true;
            if (TeamsModeIterationsPanel != null)
                TeamsModeIterationsPanel.Visibility = Visibility.Collapsed;
            if (TeamsModeIterationsBox != null)
                TeamsModeIterationsBox.Text = "2";

            // Clear skill selections
            _skillManager.ClearEnabledSkills();
            RefreshSkillsPanel();
        }

        /// <summary>Reads the main-window toggle controls into a <see cref="TaskConfigBase"/>.</summary>
        private void ReadUiFlagsInto(TaskConfigBase target)
        {
            target.SpawnTeam = SpawnTeamToggle.IsChecked == true;
            target.IsTeamsMode = TeamsModeToggle.IsChecked == true;
            target.ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true;
            target.PlanOnly = PlanOnlyToggle.IsChecked == true;
            target.IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true;
            target.UseMcp = UseMcpToggle.IsChecked == true;
            target.AutoDecompose = AutoDecomposeToggle.IsChecked == true;
            target.ApplyFix = ApplyFixToggle.IsChecked == true;
            if (int.TryParse(TeamsModeIterationsBox?.Text, out var iter) && iter > 0)
                target.TeamsModeIterations = iter;
        }

        /// <summary>Applies flags from a <see cref="TaskConfigBase"/> to the main-window toggle controls.</summary>
        private void ApplyFlagsToUi(TaskConfigBase source)
        {
            if (SpawnTeamToggle == null) return; // Not yet loaded during InitializeComponent
            SpawnTeamToggle.IsChecked = source.SpawnTeam;
            TeamsModeToggle.IsChecked = source.IsTeamsMode;
            ExtendedPlanningToggle.IsChecked = source.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = source.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = source.IgnoreFileLocks;
            UseMcpToggle.IsChecked = source.UseMcp;
            AutoDecomposeToggle.IsChecked = source.AutoDecompose;
            ApplyFixToggle.IsChecked = source.ApplyFix;
            if (TeamsModeIterationsPanel != null)
                TeamsModeIterationsPanel.Visibility = source.IsTeamsMode ? Visibility.Visible : Visibility.Collapsed;
            if (TeamsModeIterationsBox != null)
                TeamsModeIterationsBox.Text = source.TeamsModeIterations.ToString();
        }

        // ── Execute ────────────────────────────────────────────────

        /// <summary>
        /// Creates an <see cref="AgentTask"/> from a description using the current UI toggle
        /// state, sets project metadata, then routes through the appropriate launch pipeline
        /// (Gemini image gen, headless, or standard terminal via <see cref="LaunchTask"/>).
        /// </summary>
        /// <remarks>
        /// Callers must read any UI state they need (model combo, additional instructions, etc.)
        /// <b>before</b> calling this method, because it reads toggle values internally.
        /// <see cref="ResetPerTaskToggles"/> should be called <b>after</b> this method returns.
        /// </remarks>
        private void LaunchTaskFromDescription(
            string description,
            string summary,
            ModelType model = ModelType.ClaudeCode,
            List<string>? imagePaths = null,
            bool planOnly = false,
            List<AgentTask>? dependencies = null,
            string? additionalInstructions = null,
            string? header = null,
            bool? forceMcp = null)
        {
            var task = _taskFactory.CreateTask(
                description,
                _projectManager.ProjectPath,
                skipPermissions: true,
                headless: false,
                isTeamsMode: TeamsModeToggle.IsChecked == true,
                ignoreFileLocks: IgnoreFileLocksToggle.IsChecked == true,
                useMcp: forceMcp ?? (UseMcpToggle.IsChecked == true),
                spawnTeam: SpawnTeamToggle.IsChecked == true,
                extendedPlanning: ExtendedPlanningToggle.IsChecked == true,
                planOnly: planOnly,
                imagePaths: imagePaths,
                model: model,
                autoDecompose: AutoDecomposeToggle.IsChecked == true,
                applyFix: ApplyFixToggle.IsChecked == true,
                useAutoMode: AutoModeToggle.IsChecked == true,
                allowFeatureModeInference: AutoTeamsModeToggle.IsChecked == true);

            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);
            task.Summary = summary;
            if (!string.IsNullOrEmpty(header))
                task.Header = header;
            task.AdditionalInstructions = additionalInstructions ?? "";

            if (task.IsTeamsMode && int.TryParse(TeamsModeIterationsBox?.Text, out var iterations) && iterations > 0)
                task.MaxIterations = iterations;

            task.TimeoutMinutes = _settingsManager.TaskTimeoutMinutes;

            if (model == ModelType.Gemini)
            {
                ExecuteGeminiTask(task);
                return;
            }

            if (model == ModelType.GeminiGameArt)
            {
                ExecuteGeminiGameArtTask(task);
                return;
            }

            if (task.Headless)
            {
                _taskExecutionManager.LaunchHeadless(task);
                UpdateStatus();
                return;
            }

            LaunchTask(task, dependencies);
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if (!_projectManager.HasProjects) return;

            var desc = TaskInput.Text?.Trim();
            if (!_taskFactory.ValidateTaskInput(desc)) return;

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
            {
                var modelTag = modelItem.Tag?.ToString();
                if (modelTag == "Gemini") selectedModel = ModelType.Gemini;
                else if (modelTag == "GeminiGameArt") selectedModel = ModelType.GeminiGameArt;
            }

            // Check if we should remove an unmodified saved prompt
            if (_currentLoadedPrompt != null && !_loadedPromptModified)
            {
                _savedPrompts.Remove(_currentLoadedPrompt);
                PersistSavedPrompts();
                _currentLoadedPrompt = null;
            }

            // Pin both rows to prevent prompt area rescaling during the clear+launch+reset sequence
            PinRowHeights();

            // Capture UI state before clearing
            var additionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "";
            var imagePaths = _imageManager.DetachImages();
            var dependencies = _pendingDependencies.ToList();
            ClearPendingDependencies();
            TaskInput.Clear();
            AdditionalInstructionsInput.Clear();

            LaunchTaskFromDescription(
                desc!,
                _taskFactory.GenerateLocalSummary(desc!),
                selectedModel,
                imagePaths,
                PlanOnlyToggle.IsChecked == true,
                dependencies,
                additionalInstructions);

            ResetPerTaskToggles();
            ReapplySelectedTemplate();
            RestoreStarRows();
        }

        private async void ComposeWorkflow_Click(object sender, RoutedEventArgs e)
        {
            if (!_projectManager.HasProjects) return;

            var result = await WorkflowComposerDialog.ShowAsync(_claudeService, _projectManager.ProjectPath);
            if (result == null || result.Steps.Count == 0)
                return;

            // Map taskName -> AgentTask for dependency resolution
            var tasksByName = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in result.Steps)
            {
                var task = _taskFactory.CreateTask(
                    step.Description,
                    _projectManager.ProjectPath,
                    skipPermissions: true,
                    headless: false,
                    isTeamsMode: false,
                    ignoreFileLocks: IgnoreFileLocksToggle.IsChecked == true,
                    useMcp: UseMcpToggle.IsChecked == true);

                task.Summary = step.TaskName;
                task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
                task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);

                task.TimeoutMinutes = _settingsManager.TaskTimeoutMinutes;

                // Resolve dependencies from name to task ID
                var depIds = new List<string>();
                var depNumbers = new List<int>();
                foreach (var depName in step.DependsOn)
                {
                    if (tasksByName.TryGetValue(depName, out var depTask))
                    {
                        depIds.Add(depTask.Id);
                        depNumbers.Add(depTask.TaskNumber);
                    }
                }

                tasksByName[step.TaskName] = task;

                // Add to UI
                AddActiveTask(task);
                _outputTabManager.CreateTab(task);

                if (depIds.Count > 0)
                {
                    task.DependencyTaskIds = depIds;
                    task.DependencyTaskNumbers = depNumbers;
                    _taskOrchestrator.AddTask(task, depIds);

                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    task.Status = AgentTaskStatus.Planning;
                    _outputTabManager.AppendOutput(task.Id,
                        $"Workflow task \"{step.TaskName}\" — waiting for dependencies: {string.Join(", ", step.DependsOn)}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
                {
                    _taskOrchestrator.AddTask(task, depIds);
                    task.Status = AgentTaskStatus.InitQueued;
                    task.QueuedReason = "Max concurrent tasks reached";
                    _outputTabManager.AppendOutput(task.Id,
                        $"Workflow task \"{step.TaskName}\" — queued (max concurrent tasks reached)\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
                else
                {
                    _taskOrchestrator.AddTask(task, depIds);
                    _outputTabManager.AppendOutput(task.Id,
                        $"Workflow task \"{step.TaskName}\" — starting...\n",
                        _activeTasks, _historyTasks);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
            }

            RefreshFilterCombos();
            UpdateStatus();
        }

        // ── Tab Events ─────────────────────────────────────────────

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => _outputTabManager.UpdateOutputTabWidths();

        private void ClearFinishedTasks_Click(object sender, RoutedEventArgs e) => _outputTabManager.ClearFinishedTabs(_activeTasks, _historyTasks);

        private void OnTabCloseRequested(AgentTask task) => CloseTab(task);

        private void OnTabStoreRequested(AgentTask task)
        {
            // If the task is still active, cancel it first
            if (task.IsRunning || task.IsPlanning || task.IsPaused || task.IsQueued)
            {
                task.TeamsModeRetryTimer?.Stop();
                task.TeamsModeIterationTimer?.Stop();
                try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                TaskExecutionManager.KillProcess(task);
                task.Cts?.Dispose();
                task.Cts = null;
                _outputTabManager.AppendOutput(task.Id,
                    "\nTask cancelled and stored.\n", _activeTasks, _historyTasks);
            }

            // Create the stored task entry
            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                ProjectDisplayName = task.ProjectDisplayName,
                StoredPrompt = task.StoredPrompt ?? task.Description,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();

            // Clean up the active task and close the tab
            FinalizeTask(task);
        }

        /// <summary>
        /// Moves a task from history back to active if needed, preventing layout jitter.
        /// </summary>
        private void MoveToActiveIfNeeded(AgentTask task)
        {
            if (_historyTasks.Contains(task) && !_activeTasks.Contains(task))
            {
                _historyTasks.Remove(task);
                PinRowHeights();
                _activeTasks.Insert(0, task);
                RestoreStarRows();
            }
        }

        private void OnTabResumeRequested(AgentTask task)
        {
            if (!task.IsFinished) return;

            MoveToActiveIfNeeded(task);

            var resumeMethod = !string.IsNullOrEmpty(task.ConversationId) ? "--resume (session tracked)" : "fresh session (no session ID)";
            _outputTabManager.AppendOutput(task.Id,
                $"\nResumed session — type a follow-up message below. It will be sent with {resumeMethod}.\n",
                _activeTasks, _historyTasks);

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            UpdateStatus();
        }

        private void OnTabExportRequested(AgentTask task)
        {
            if (string.IsNullOrEmpty(task.FullOutput) && task.OutputBuilder.Length > 0)
                task.FullOutput = task.OutputBuilder.ToString();

            var exportDialog = new Dialogs.ExportDialog(new List<AgentTask> { task });
            exportDialog.ShowDialog();
        }

        private void OnTabInputSent(AgentTask task, TextBox inputBox)
        {
            MoveToActiveIfNeeded(task);
            _taskExecutionManager.SendInput(task, inputBox, _activeTasks, _historyTasks);
        }

        private void OnTabInterruptInputSent(AgentTask task, TextBox inputBox)
        {
            var text = inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                // No input text — treat interrupt as pause/resume toggle
                if (task.Status == AgentTaskStatus.Paused)
                {
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                }
                else if (task.IsRunning)
                {
                    _taskExecutionManager.PauseTask(task);
                    _outputTabManager.AppendOutput(task.Id, "\nTask paused.\n", _activeTasks, _historyTasks);
                }
                return;
            }
            inputBox.Clear();
            MoveToActiveIfNeeded(task);
            _taskExecutionManager.SendFollowUp(task, text, _activeTasks, _historyTasks, isInterrupt: true);
        }

        internal void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.Paused)
            {
                _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
            }
            else if (task.IsRunning)
            {
                _taskExecutionManager.PauseTask(task);
                _outputTabManager.AppendOutput(task.Id, "\nTask paused.\n", _activeTasks, _historyTasks);
            }
        }

        internal void SoftStop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning)) return;

            _taskExecutionManager.SoftStopTask(task);
            _outputTabManager.AppendOutput(task.Id, "\nSoft-stop requested — waiting for task to finish gracefully...\n", _activeTasks, _historyTasks);
        }

        internal void ForceStartQueued_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.Status is not (AgentTaskStatus.Queued or AgentTaskStatus.InitQueued)) return;

            if (!DarkDialog.ShowConfirm(
                $"Force-start task #{task.TaskNumber}?\n\n" +
                $"This will bypass any dependencies or queue limits.\n\n" +
                $"Task: {task.ShortDescription}",
                "Force Start Queued Task"))
                return;

            if (task.Status == AgentTaskStatus.InitQueued)
            {
                task.QueuedReason = null;
                LaunchTaskProcess(task, $"\nForce-starting task #{task.TaskNumber} (limit bypassed)...\n\n");
                UpdateQueuePositions();
                UpdateStatus();
                return;
            }

            // Queued task
            if (task.DependencyTaskIdCount > 0)
            {
                _taskOrchestrator.MarkResolved(task.Id);
                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.ClearDependencyTaskIds();
                task.DependencyTaskNumbers.Clear();

                if (task.Process is { HasExited: false })
                {
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                    _outputTabManager.AppendOutput(task.Id,
                        $"\nForce-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                        _activeTasks, _historyTasks);
                }
                else
                {
                    LaunchTaskProcess(task, $"\nForce-starting task #{task.TaskNumber} (dependencies skipped)...\n\n");
                }
            }
            else
            {
                _fileLockManager.ForceStartQueuedTask(task);
            }

            _outputTabManager.UpdateTabHeader(task);
            UpdateStatus();
        }

        internal void ToggleFileLock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            task.IgnoreFileLocks = !task.IgnoreFileLocks;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning or AgentTaskStatus.Paused or AgentTaskStatus.InitQueued || task.Status == AgentTaskStatus.Queued)
            {
                var processAlreadyDone = task.Process == null || task.Process.HasExited;
                if (processAlreadyDone)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime ??= DateTime.Now;
                }
                else
                {
                    if (!DarkDialog.ShowConfirm("This task is still running. Closing will terminate it.\n\nAre you sure?", "Task Running"))
                        return;

                    task.TokenLimitRetryTimer?.Stop();
                    task.TokenLimitRetryTimer = null;
                    task.Status = AgentTaskStatus.Cancelled;
                    task.EndTime = DateTime.Now;
                    TaskExecutionManager.KillProcess(task);
                }
            }
            else if (_activeTasks.Contains(task))
            {
                task.EndTime ??= DateTime.Now;
            }

            FinalizeTask(task);
        }

        // ── Task Actions ───────────────────────────────────────────

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.InitQueued)
            {
                // Force-start an init-queued task (bypass max concurrent limit)
                task.QueuedReason = null;
                LaunchTaskProcess(task, $"\nForce-starting task #{task.TaskNumber} (limit bypassed)...\n\n");
                UpdateQueuePositions();
                UpdateStatus();
                return;
            }

            if (task.Status == AgentTaskStatus.Queued)
            {
                if (task.DependencyTaskIdCount > 0)
                {
                    // Force-start a dependency-queued task — remove from orchestrator tracking
                    _taskOrchestrator.MarkResolved(task.Id);
                    task.QueuedReason = null;
                    task.BlockedByTaskId = null;
                    task.BlockedByTaskNumber = null;
                    task.ClearDependencyTaskIds();
                    task.DependencyTaskNumbers.Clear();

                    if (task.Process is { HasExited: false })
                    {
                        // Resume suspended process (was queued via drag-drop)
                        _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                        _outputTabManager.AppendOutput(task.Id,
                            $"\nForce-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                            _activeTasks, _historyTasks);
                    }
                    else
                    {
                        LaunchTaskProcess(task, $"\nForce-starting task #{task.TaskNumber} (dependencies skipped)...\n\n");
                    }

                    _outputTabManager.UpdateTabHeader(task);
                    UpdateStatus();
                }
                else
                {
                    _fileLockManager.ForceStartQueuedTask(task);
                }
                return;
            }

            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            TaskExecutionManager.KillProcess(task);
            _outputTabManager.UpdateTabHeader(task);

            // Keep task in active list for follow-up. Release locks so queued tasks can proceed.
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);
            RefreshActivityDashboard();
            UpdateStatus();
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            DrainInitQueue();
        }

        internal void CopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (!string.IsNullOrEmpty(task.Description))
                Clipboard.SetText(task.Description);
        }

        internal void ExportTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;

            // Check if multiple items are selected in history list
            var tasksToExport = new List<AgentTask>();

            // If we're in the history tab and have multiple selections
            if (MainTabs.SelectedItem == HistoryTabItem && HistoryTasksList.SelectedItems.Count > 0)
            {
                foreach (AgentTask selectedTask in HistoryTasksList.SelectedItems)
                {
                    tasksToExport.Add(selectedTask);
                }
            }
            else
            {
                // Single task export
                tasksToExport.Add(task);
            }

            // Snapshot OutputBuilder for active tasks that don't have FullOutput yet
            foreach (var t in tasksToExport)
            {
                if (string.IsNullOrEmpty(t.FullOutput) && t.OutputBuilder.Length > 0)
                    t.FullOutput = t.OutputBuilder.ToString();
            }

            var exportDialog = new Dialogs.ExportDialog(tasksToExport);
            exportDialog.ShowDialog();
        }

        internal void CloneTask_Click(object sender, RoutedEventArgs e)
        {
            var sourceTask = GetTaskFromContextMenuItem(sender);
            if (sourceTask == null) return;

            var newTask = _taskFactory.CreateTask(
                sourceTask.Description,
                sourceTask.ProjectPath,
                sourceTask.SkipPermissions,
                sourceTask.Headless,
                sourceTask.IsTeamsMode,
                sourceTask.IgnoreFileLocks,
                sourceTask.UseMcp,
                sourceTask.SpawnTeam,
                sourceTask.ExtendedPlanning,
                sourceTask.PlanOnly,
                sourceTask.UseMessageBus,
                sourceTask.ImagePaths,
                sourceTask.Model,
                autoDecompose: sourceTask.AutoDecompose,
                applyFix: sourceTask.ApplyFix);

            // Copy additional properties that aren't in CreateTask
            newTask.AdditionalInstructions = sourceTask.AdditionalInstructions;
            newTask.ProjectColor = sourceTask.ProjectColor;
            newTask.ProjectDisplayName = sourceTask.ProjectDisplayName;
            newTask.MaxIterations = sourceTask.MaxIterations;
            newTask.TimeoutMinutes = sourceTask.TimeoutMinutes;
            newTask.Summary = _taskFactory.GenerateLocalSummary(sourceTask.Description);

            AddActiveTask(newTask);
            DrainInitQueue();
        }


        private void SetPriorityCritical_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Critical);
        private void SetPriorityHigh_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.High);
        private void SetPriorityNormal_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Normal);
        private void SetPriorityLow_Click(object sender, RoutedEventArgs e) => SetTaskPriority(sender, TaskPriority.Low);

        private static AgentTask? GetTaskFromContextMenuItem(object sender)
        {
            // Direct DataContext binding (works when item itself has the context)
            if (sender is FrameworkElement { DataContext: AgentTask task })
                return task;

            // Walk up the logical tree from the MenuItem to find the ContextMenu,
            // then resolve PlacementTarget (the card that was right-clicked).
            if (sender is MenuItem mi)
            {
                DependencyObject? current = mi;
                while (current != null)
                {
                    if (current is ContextMenu ctx && ctx.PlacementTarget is FrameworkElement target
                                                   && target.DataContext is AgentTask t)
                        return t;
                    current = LogicalTreeHelper.GetParent(current);
                }
            }

            return null;
        }

        private void SetTaskPriority(object sender, TaskPriority level)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;

            task.PriorityLevel = level;
            ReorderByPriority();
        }

        internal async void RevertTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;

            if (string.IsNullOrEmpty(task.GitStartHash))
            {
                DarkDialog.ShowAlert("No git snapshot was captured when this task started.\nRevert is not available.", "Revert Unavailable");
                return;
            }

            if (string.IsNullOrEmpty(task.ProjectPath) || !Directory.Exists(task.ProjectPath))
            {
                DarkDialog.ShowAlert("The project path for this task no longer exists.", "Revert Unavailable");
                return;
            }

            // Validate git hash to prevent command injection
            if (!_gitHelper.IsValidGitHash(task.GitStartHash))
            {
                DarkDialog.ShowAlert("Invalid git hash format detected. Cannot perform revert.", "Invalid Git Hash");
                AppLogger.Warn("TaskExecution", $"Invalid git hash detected in revert operation: {task.GitStartHash}");
                return;
            }

            var shortHash = task.GitStartHash.Length > 7 ? task.GitStartHash[..7] : task.GitStartHash;
            if (!DarkDialog.ShowConfirm(
                $"This will hard-reset the project to the state before this task ran (commit {shortHash}).\n\n" +
                "All uncommitted changes and any commits made after that point will be lost.\n\n" +
                "Are you sure?",
                "Revert Task Changes"))
                return;

            try
            {
                var result = await _gitHelper.RunGitCommandAsync(
                    task.ProjectPath, $"reset --hard {task.GitStartHash}");

                if (result != null)
                {
                    _outputTabManager.AppendOutput(task.Id,
                        $"\nReverted to commit {shortHash}.\n", _activeTasks, _historyTasks);
                    DarkDialog.ShowAlert($"Successfully reverted to commit {shortHash}.", "Revert Complete");
                }
                else
                {
                    DarkDialog.ShowAlert("Git reset failed. The commit may no longer exist or the repository state may have changed.", "Revert Failed");
                }
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
        }

        internal void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            CancelTask(task, el);
        }

        internal void TaskCard_PreviewMiddleDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            // Handle during tunneling phase to prevent the ScrollViewer from
            // capturing the mouse for auto-scroll, which would swallow MouseUp.
            CancelTask(task, el);
            e.Handled = true;
        }

        internal void TaskCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (e.ChangedButton == MouseButton.Left && _outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            }
        }

        private void CancelTask(AgentTask task, FrameworkElement? sender = null)
        {
            if (task.IsFinished)
            {
                // Dismiss Recommendation tasks: transition to Completed so teardown
                // performs normal cleanup instead of keeping the task in the active list.
                if (task.Status == AgentTaskStatus.Recommendation)
                {
                    task.Recommendations = "";
                    task.Status = AgentTaskStatus.Completed;
                }

                _outputTabManager.UpdateTabHeader(task);
                // User explicitly dismissed this task — go straight to teardown,
                // bypassing FinalizeTask which would re-trigger auto-commit and
                // keep the task stuck in the active list.
                if (sender != null)
                {
                    _outputTabManager.AppendOutput(task.Id, "\nTask removed.\n", _activeTasks, _historyTasks);
                    AnimateRemoval(sender, () => PerformTaskTeardown(task));
                }
                else
                {
                    PerformTaskTeardown(task);
                }
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning or AgentTaskStatus.Paused or AgentTaskStatus.SoftStop)
            {
                if (!DarkDialog.ShowConfirm(
                    $"Task #{task.TaskNumber} is still running.\nAre you sure you want to cancel it?",
                    "Cancel Running Task"))
                    return;

                // Re-check after dialog: the modal dialog has its own message pump,
                // so the process exit callback may have fired and changed the task state.
                if (task.IsFinished)
                {
                    _outputTabManager.UpdateTabHeader(task);
                    if (!_activeTasks.Contains(task))
                        return;
                    PerformTaskTeardown(task);
                    return;
                }
            }

            if (task.TeamsModeRetryTimer != null)
            {
                task.TeamsModeRetryTimer.Stop();
                task.TeamsModeRetryTimer = null;
            }
            if (task.TeamsModeIterationTimer != null)
            {
                task.TeamsModeIterationTimer.Stop();
                task.TeamsModeIterationTimer = null;
            }
            if (task.TokenLimitRetryTimer != null)
            {
                task.TokenLimitRetryTimer.Stop();
                task.TokenLimitRetryTimer = null;
            }
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            // Capture the process reference before clearing state, then kill on a
            // background thread — Process.Kill(entireProcessTree: true) can block
            // while enumerating/terminating child processes on Windows.
            var proc = task.Process;
            task.Process = null;
            if (proc is { HasExited: false })
                System.Threading.Tasks.Task.Run(() => { try { proc.Kill(true); } catch { /* best-effort */ } });
            task.Cts?.Dispose();
            task.Cts = null;
            _outputTabManager.AppendOutput(task.Id, "\nTask cancelled.\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            FinalizeTask(task);
        }

        internal void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            _outputTabManager.AppendOutput(task.Id, "\nTask removed.\n", _activeTasks, _historyTasks);
            AnimateRemoval(el, () =>
            {
                _outputTabManager.CloseTab(task);
                _historyTasks.Remove(task);
                _historyManager.SaveHistory(_historyTasks);
                RefreshFilterCombos();
                _outputTabManager.UpdateOutputTabWidths();
                UpdateStatus();
            });
        }

        private void ClearFinished_Click(object sender, RoutedEventArgs e)
        {
            var finished = _activeTasks.Where(t => t.IsFinished).ToList();
            if (finished.Count == 0) return;

            // User explicitly clearing finished tasks — bypass FinalizeTask to avoid
            // re-triggering auto-commit which would keep tasks stuck in active list.
            foreach (var task in finished)
                PerformTaskTeardown(task);

            _outputTabManager.UpdateOutputTabWidths();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.ShowConfirm(
                $"Are you sure you want to clear all {_historyTasks.Count} history entries? This cannot be undone.",
                "Clear History")) return;

            foreach (var task in _historyTasks.ToList())
                _outputTabManager.CloseTab(task);
            _historyTasks.Clear();
            _historyManager.SaveHistory(_historyTasks);
            RefreshFilterCombos();
            _outputTabManager.UpdateOutputTabWidths();
            UpdateStatus();
        }

        internal void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (!_outputTabManager.HasTab(task.Id))
            {
                _outputTabManager.CreateTab(task);
                _outputTabManager.AppendOutput(task.Id, $"Resumed session\n", _activeTasks, _historyTasks);
                _outputTabManager.AppendOutput(task.Id, $"Original task: {task.Description}\n", _activeTasks, _historyTasks);
                _outputTabManager.AppendOutput(task.Id, $"Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);
                _outputTabManager.AppendOutput(task.Id, $"Status: {task.StatusText}\n", _activeTasks, _historyTasks);
                if (!string.IsNullOrEmpty(task.ConversationId))
                    _outputTabManager.AppendOutput(task.Id, $"Session: {task.ConversationId}\n", _activeTasks, _historyTasks);
                if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                    _outputTabManager.AppendOutput(task.Id, $"\n{task.CompletionSummary}\n", _activeTasks, _historyTasks);
                if (!string.IsNullOrWhiteSpace(task.Recommendations))
                    _outputTabManager.AppendOutput(task.Id, $"\n[Recommendations]\n{task.Recommendations}\n", _activeTasks, _historyTasks);
                var resumeMethod = !string.IsNullOrEmpty(task.ConversationId) ? "--resume (session tracked)" : "fresh session (no session ID)";
                _outputTabManager.AppendOutput(task.Id, $"\nType a follow-up message below. It will be sent with {resumeMethod}.\n", _activeTasks, _historyTasks);
            }

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);

            MoveToActiveIfNeeded(task);

            UpdateStatus();
        }

        internal void RetryTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (!task.IsRetryable) return;
            RetryTask(task);
        }

        internal void ContinueTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (!task.IsContinuable || string.IsNullOrWhiteSpace(task.Recommendations)) return;

            var recommendations = task.Recommendations;

            // Build the follow-up prompt from the recommendations
            var followUpPrompt = $"Please execute these recommended next steps:\n{recommendations}";

            // Clear recommendations so the button disappears
            task.Recommendations = "";

            // Ensure the output tab exists and is selected
            if (!_outputTabManager.HasTab(task.Id))
            {
                _outputTabManager.CreateTab(task);
                _outputTabManager.AppendOutput(task.Id, $"Continuing task with recommendations\n", _activeTasks, _historyTasks);
                _outputTabManager.AppendOutput(task.Id, $"Original task: {task.Description}\n", _activeTasks, _historyTasks);
                _outputTabManager.AppendOutput(task.Id, $"Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);
                if (!string.IsNullOrEmpty(task.ConversationId))
                    _outputTabManager.AppendOutput(task.Id, $"Session: {task.ConversationId}\n", _activeTasks, _historyTasks);
            }
            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);

            MoveToActiveIfNeeded(task);

            // Send the follow-up with recommendations
            _taskExecutionManager.SendFollowUp(task, followUpPrompt, _activeTasks, _historyTasks);
            UpdateStatus();
        }

        internal void RerunTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            RetryTask(task);
        }

        internal void StoreHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (!task.IsFinished) return;

            var output = task.OutputBuilder.ToString();

            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                ProjectDisplayName = task.ProjectDisplayName,
                StoredPrompt = !string.IsNullOrWhiteSpace(task.CompletionSummary) ? task.CompletionSummary : task.Description,
                FullOutput = output,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();
        }

        internal async void VerifyTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!task.IsFinished) return;

            if (!_outputTabManager.HasTab(task.Id))
                _outputTabManager.CreateTab(task);

            _outputTabManager.AppendOutput(task.Id,
                "\nRunning result verification...\n", _activeTasks, _historyTasks);

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);

            var previousStatus = task.Status;
            task.Status = AgentTaskStatus.Verifying;
            _outputTabManager.UpdateTabHeader(task);

            await _taskExecutionManager.RunResultVerificationAsync(task, _activeTasks, _historyTasks);

            task.Status = previousStatus;
            _outputTabManager.UpdateTabHeader(task);
        }
    }
}
