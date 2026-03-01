using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages feature mode multi-phase orchestration:
    /// Phase 1 (TeamPlanning): Spawn a planning team that explores the codebase
    /// Phase 2 (PlanConsolidation): Consolidate team findings into step-by-step FEATURE_STEPS
    /// Phase 3 (Execution): Create tasks for each step with dependency tracking
    /// Phase 4 (Evaluation): Evaluate all results, decide whether to iterate
    /// </summary>
    public class FeatureModeHandler
    {
        private readonly string _scriptDir;
        private readonly TaskProcessLauncher _processLauncher;
        private readonly OutputProcessor _outputProcessor;
        private readonly MessageBusManager _messageBusManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<int> _getTokenLimitRetryMinutes;

        internal const int FeatureModeMaxRuntimeHours = 12;
        internal const int FeatureModeIterationTimeoutMinutes = 60;
        internal const int FeatureModeMaxConsecutiveFailures = 3;
        internal const int FeatureModeOutputCapChars = 100_000;

        /// <summary>Fires when a new feature mode iteration starts (taskId, iteration).</summary>
        public event Action<string, int>? IterationStarted;

        /// <summary>Fires when the feature mode finishes for a task (taskId, finalStatus).</summary>
        public event Action<string, AgentTaskStatus>? FeatureModeFinished;

        /// <summary>Fires when a team member or step task needs to be spawned (parent, child).</summary>
        public event Action<AgentTask, AgentTask>? FeatureModeChildSpawned;

        /// <summary>Fires to request team extraction from output (parent, output) → list of children.</summary>
        public event Func<AgentTask, string, List<AgentTask>?>? ExtractTeamRequested;

        public FeatureModeHandler(
            string scriptDir,
            TaskProcessLauncher processLauncher,
            OutputProcessor outputProcessor,
            MessageBusManager messageBusManager,
            OutputTabManager outputTabManager,
            Func<int> getTokenLimitRetryMinutes)
        {
            _scriptDir = scriptDir;
            _processLauncher = processLauncher;
            _outputProcessor = outputProcessor;
            _messageBusManager = messageBusManager;
            _outputTabManager = outputTabManager;
            _getTokenLimitRetryMinutes = getTokenLimitRetryMinutes;
        }

        // ── Main entry point: called when a feature mode task's process exits ──

        public void HandleFeatureModeIteration(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.FeatureModeIterationTimer != null)
            {
                task.FeatureModeIterationTimer.Stop();
                task.FeatureModeIterationTimer = null;
            }

            if (task.Status != AgentTaskStatus.Running) return;

            var fullOutput = task.OutputBuilder.ToString();
            var iterationOutput = task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;

            // Check runtime cap
            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= FeatureModeMaxRuntimeHours)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Total runtime cap ({FeatureModeMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Check consecutive failures
            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveFailures++;
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Phase process exited with code {exitCode} (failure {task.ConsecutiveFailures}/{FeatureModeMaxConsecutiveFailures})\n", activeTasks, historyTasks);
                if (task.ConsecutiveFailures >= FeatureModeMaxConsecutiveFailures)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] {FeatureModeMaxConsecutiveFailures} consecutive failures. Stopping.\n", activeTasks, historyTasks);
                    FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
            }
            else
            {
                task.ConsecutiveFailures = 0;
            }

            // Token limit retry
            if (TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                HandleTokenLimitRetry(task, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Route based on current phase
            switch (task.FeatureModePhase)
            {
                case FeatureModePhase.None:
                    // Initial planning process completed — extract team
                    HandlePlanningProcessComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                case FeatureModePhase.PlanConsolidation:
                    // Consolidation process completed — extract steps
                    HandleConsolidationComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                case FeatureModePhase.Evaluation:
                    // Evaluation process completed — check if done or iterate
                    HandleEvaluationComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                default:
                    // TeamPlanning or Execution phases don't have their own process — they wait for children
                    _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Unexpected process exit in phase {task.FeatureModePhase}.\n", activeTasks, historyTasks);
                    break;
            }
        }

        // ── Phase handlers ──────────────────────────────────────────────

        /// <summary>
        /// Phase 0 → Phase 1: The initial planning process completed. Extract TEAM block and spawn team members.
        /// </summary>
        private void HandlePlanningProcessComplete(AgentTask task, string output,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            // Try to extract team from output
            var children = ExtractTeamRequested?.Invoke(task, output);

            if (children == null || children.Count == 0)
            {
                _outputProcessor.AppendOutput(task.Id,
                    "\n[Feature Mode] No team produced — falling back to direct plan consolidation.\n",
                    activeTasks, historyTasks);

                // Skip team phase, go directly to plan consolidation
                task.FeatureModePhase = FeatureModePhase.PlanConsolidation;
                StartPlanConsolidationProcess(task, "", activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Configure team members for planning only (no file modifications)
            task.FeaturePhaseChildIds.Clear();
            foreach (var child in children)
            {
                child.SpawnTeam = false;
                child.AutoDecompose = false;
                child.IsFeatureMode = false;
                child.UseMessageBus = true;
                child.NoGitWrite = true;
                child.SkipPermissions = true;
                task.FeaturePhaseChildIds.Add(child.Id);
                FeatureModeChildSpawned?.Invoke(task, child);
            }

            task.FeatureModePhase = FeatureModePhase.TeamPlanning;
            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] Planning team spawned with {children.Count} member(s). Waiting for team to complete...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
        }

        /// <summary>
        /// Called when all children of the current phase have completed.
        /// Routes to the next phase based on current FeatureModePhase.
        /// </summary>
        public void OnFeatureModePhaseComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            switch (task.FeatureModePhase)
            {
                case FeatureModePhase.TeamPlanning:
                    OnTeamPlanningComplete(task, activeTasks, historyTasks, moveToHistory);
                    break;

                case FeatureModePhase.Execution:
                    OnExecutionComplete(task, activeTasks, historyTasks, moveToHistory);
                    break;
            }
        }

        /// <summary>
        /// Phase 1 → Phase 2: All planning team members finished. Consolidate into FEATURE_STEPS.
        /// </summary>
        private void OnTeamPlanningComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            _outputProcessor.AppendOutput(task.Id,
                "\n[Feature Mode] Planning team complete. Consolidating into step-by-step plan...\n",
                activeTasks, historyTasks);

            // Collect team member results
            var teamResults = CollectChildResults(task, activeTasks, historyTasks);

            task.FeatureModePhase = FeatureModePhase.PlanConsolidation;
            task.FeaturePhaseChildIds.Clear();

            StartPlanConsolidationProcess(task, teamResults, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 2 process: Run consolidation agent that reads team results and outputs FEATURE_STEPS.
        /// </summary>
        private void StartPlanConsolidationProcess(AgentTask task, string teamResults,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var prompt = TaskLauncher.BuildFeatureModePlanConsolidationPrompt(
                task.CurrentIteration, task.MaxIterations, teamResults, task.OriginalFeatureDescription);

            StartFeatureModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 2 complete: Parse FEATURE_STEPS and create execution tasks.
        /// </summary>
        private void HandleConsolidationComplete(AgentTask task, string output,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var steps = ExtractFeatureSteps(output);

            if (steps == null || steps.Count == 0)
            {
                _outputProcessor.AppendOutput(task.Id,
                    "\n[Feature Mode] No FEATURE_STEPS found in consolidation output. Finishing.\n",
                    activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] Plan consolidated: {steps.Count} step(s). Creating execution tasks...\n",
                activeTasks, historyTasks);

            // Spawn execution tasks with dependencies
            var children = SpawnExecutionTasks(task, steps, activeTasks, historyTasks);

            task.FeatureModePhase = FeatureModePhase.Execution;
            task.FeaturePhaseChildIds.Clear();
            foreach (var child in children)
            {
                task.FeaturePhaseChildIds.Add(child.Id);
                FeatureModeChildSpawned?.Invoke(task, child);
            }

            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] {children.Count} execution task(s) spawned. Waiting for completion...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
        }

        /// <summary>
        /// Phase 3 → Phase 4: All execution tasks finished. Run evaluation.
        /// </summary>
        private void OnExecutionComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            _outputProcessor.AppendOutput(task.Id,
                "\n[Feature Mode] All execution tasks complete. Starting evaluation...\n",
                activeTasks, historyTasks);

            // Collect execution results
            var executionResults = CollectChildResults(task, activeTasks, historyTasks);

            task.FeatureModePhase = FeatureModePhase.Evaluation;
            task.FeaturePhaseChildIds.Clear();

            var prompt = TaskLauncher.BuildFeatureModeEvaluationPrompt(
                task.CurrentIteration, task.MaxIterations, task.OriginalFeatureDescription, executionResults);

            StartFeatureModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 4 complete: Check STATUS, iterate or finish.
        /// </summary>
        private void HandleEvaluationComplete(AgentTask task, string output,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (TaskLauncher.CheckFeatureModeComplete(output))
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Feature Mode] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Feature finished.\n",
                    activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (task.CurrentIteration >= task.MaxIterations)
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Feature Mode] Max iterations ({task.MaxIterations}) reached. Stopping.\n",
                    activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Start next iteration
            task.CurrentIteration++;
            task.FeatureModePhase = FeatureModePhase.None;
            task.FeaturePhaseChildIds.Clear();
            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var iterRuntime = DateTime.Now - task.StartTime;
            var iterTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)}"
                : "";
            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] NEEDS_MORE_WORK — Starting iteration {task.CurrentIteration}/{task.MaxIterations} | Runtime: {(int)iterRuntime.TotalMinutes}m{iterTokenInfo}\n\n",
                activeTasks, historyTasks);
            IterationStarted?.Invoke(task.Id, task.CurrentIteration);
            _outputTabManager.UpdateTabHeader(task);

            // Re-run planning phase with context from previous iteration
            StartIterationPlanningProcess(task, output, activeTasks, historyTasks, moveToHistory);
        }

        // ── Process launching helpers ───────────────────────────────────

        /// <summary>
        /// Re-starts the planning phase for a new iteration, including context from the previous evaluation.
        /// </summary>
        private void StartIterationPlanningProcess(AgentTask task, string previousEvaluation,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var prompt = PromptBuilder.FeatureModeInitialTemplate +
                task.OriginalFeatureDescription +
                "\n\n# PREVIOUS ITERATION EVALUATION\n" +
                "The following is the evaluation from the previous iteration. Address the identified issues:\n\n" +
                previousEvaluation;

            StartFeatureModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Launches a new Claude process for the feature mode task with the given prompt.
        /// </summary>
        private void StartFeatureModeProcess(AgentTask task, string prompt,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var promptFile = Path.Combine(_scriptDir, $"feature_{task.Id}_{task.CurrentIteration}_{(int)task.FeatureModePhase}.txt");
            File.WriteAllText(promptFile, prompt, Encoding.UTF8);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag} --verbose --output-format stream-json $prompt";

            var ps1File = Path.Combine(_scriptDir, $"feature_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);
                HandleFeatureModeIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);

                var iterationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(FeatureModeIterationTimeoutMinutes)
                };
                task.FeatureModeIterationTimer = iterationTimer;
                iterationTimer.Tick += (_, _) =>
                {
                    iterationTimer.Stop();
                    task.FeatureModeIterationTimer = null;
                    if (task.Process is { HasExited: false })
                    {
                        _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Process timeout ({FeatureModeIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                        try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck feature mode process for task {task.Id}", ex); }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[Feature Mode] ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        // ── Step extraction and task spawning ───────────────────────────

        private class FeatureStepEntry
        {
            [System.Text.Json.Serialization.JsonPropertyName("description")]
            public string Description { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("depends_on")]
            public List<int>? DependsOn { get; set; }
        }

        /// <summary>
        /// Extracts FEATURE_STEPS JSON block from consolidation output.
        /// </summary>
        private static List<FeatureStepEntry>? ExtractFeatureSteps(string output)
        {
            var match = Regex.Match(output, @"```FEATURE_STEPS\s*\n([\s\S]*?)```", RegexOptions.Multiline);
            if (!match.Success)
            {
                AppLogger.Warn("FeatureMode", "No ```FEATURE_STEPS``` block found in output");
                return null;
            }

            var json = match.Groups[1].Value.Trim();

            try
            {
                var entries = JsonSerializer.Deserialize<List<FeatureStepEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                return entries;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureMode", "Failed to deserialize FEATURE_STEPS JSON", ex);
                return null;
            }
        }

        /// <summary>
        /// Creates execution child tasks from FEATURE_STEPS with inter-task dependencies.
        /// </summary>
        private List<AgentTask> SpawnExecutionTasks(AgentTask parent, List<FeatureStepEntry> steps,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var children = new List<AgentTask>();

            foreach (var step in steps)
            {
                var child = TaskLauncher.CreateTask(
                    step.Description,
                    parent.ProjectPath,
                    skipPermissions: true,
                    remoteSession: false,
                    headless: false,
                    isFeatureMode: false,
                    ignoreFileLocks: false,
                    useMcp: parent.UseMcp,
                    spawnTeam: false,
                    extendedPlanning: true,
                    noGitWrite: parent.NoGitWrite,
                    planOnly: false,
                    useMessageBus: true,
                    model: parent.Model,
                    parentTaskId: parent.Id);

                child.ProjectColor = parent.ProjectColor;
                child.ProjectDisplayName = parent.ProjectDisplayName;
                child.Summary = TaskLauncher.GenerateLocalSummary(step.Description);
                child.AutoDecompose = false;
                children.Add(child);
            }

            // Wire up inter-step dependencies
            for (int i = 0; i < steps.Count; i++)
            {
                var deps = steps[i].DependsOn;
                if (deps == null || deps.Count == 0) continue;

                var depIds = new List<string>();
                var depNumbers = new List<int>();
                foreach (var depIdx in deps)
                {
                    if (depIdx < 0 || depIdx >= children.Count || depIdx == i) continue;
                    depIds.Add(children[depIdx].Id);
                    depNumbers.Add(children[depIdx].TaskNumber);
                }

                if (depIds.Count > 0)
                {
                    children[i].Status = AgentTaskStatus.Queued;
                    children[i].DependencyTaskIds = depIds;
                    children[i].DependencyTaskNumbers = depNumbers;
                    children[i].QueuedReason = $"Waiting for step(s): {string.Join(", ", depNumbers.Select(n => $"#{n}"))}";
                    children[i].BlockedByTaskId = depIds[0];
                    children[i].BlockedByTaskNumber = depNumbers[0];
                }
            }

            return children;
        }

        // ── Result collection ───────────────────────────────────────────

        /// <summary>
        /// Collects completion summaries from all phase children for use as context.
        /// </summary>
        private string CollectChildResults(AgentTask parent,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var sb = new StringBuilder();
            int idx = 0;

            foreach (var childId in parent.FeaturePhaseChildIds)
            {
                var child = activeTasks.FirstOrDefault(t => t.Id == childId)
                         ?? historyTasks.FirstOrDefault(t => t.Id == childId);
                if (child == null) continue;

                idx++;
                var title = !string.IsNullOrWhiteSpace(child.Summary) ? child.Summary : $"Task #{child.TaskNumber}";
                sb.AppendLine($"### Result #{idx}: {title}");
                sb.AppendLine($"**Status:** {child.Status}");
                sb.AppendLine($"**Task:** {child.Description}");

                if (!string.IsNullOrWhiteSpace(child.CompletionSummary))
                    sb.AppendLine($"**Changes:**\n{child.CompletionSummary}");

                if (!string.IsNullOrWhiteSpace(child.Recommendations))
                    sb.AppendLine($"**Recommendations:**\n{child.Recommendations}");

                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ── Token limit retry ───────────────────────────────────────────

        private void HandleTokenLimitRetry(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var retryMinutes = _getTokenLimitRetryMinutes();
            _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Token limit hit. Retrying in {retryMinutes} minutes...\n", activeTasks, historyTasks);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(retryMinutes) };
            task.FeatureModeRetryTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                task.FeatureModeRetryTimer = null;
                if (task.Status != AgentTaskStatus.Running) return;
                if ((DateTime.Now - task.StartTime).TotalHours >= FeatureModeMaxRuntimeHours)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Runtime cap reached during retry wait. Stopping.\n", activeTasks, historyTasks);
                    FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
                _outputProcessor.AppendOutput(task.Id, "[Feature Mode] Retrying...\n", activeTasks, historyTasks);

                // Restart the appropriate phase process
                switch (task.FeatureModePhase)
                {
                    case FeatureModePhase.None:
                        StartIterationPlanningProcess(task, "", activeTasks, historyTasks, moveToHistory);
                        break;
                    case FeatureModePhase.PlanConsolidation:
                        var teamResults = CollectChildResults(task, activeTasks, historyTasks);
                        StartPlanConsolidationProcess(task, teamResults, activeTasks, historyTasks, moveToHistory);
                        break;
                    case FeatureModePhase.Evaluation:
                        var execResults = CollectChildResults(task, activeTasks, historyTasks);
                        var evalPrompt = TaskLauncher.BuildFeatureModeEvaluationPrompt(
                            task.CurrentIteration, task.MaxIterations, task.OriginalFeatureDescription, execResults);
                        StartFeatureModeProcess(task, evalPrompt, activeTasks, historyTasks, moveToHistory);
                        break;
                }
            };
            timer.Start();
        }

        // ── Finish ──────────────────────────────────────────────────────

        private void FinishFeatureModeTask(AgentTask task, AgentTaskStatus status,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.FeatureModeRetryTimer != null)
            {
                task.FeatureModeRetryTimer.Stop();
                task.FeatureModeRetryTimer = null;
            }
            if (task.FeatureModeIterationTimer != null)
            {
                task.FeatureModeIterationTimer.Stop();
                task.FeatureModeIterationTimer = null;
            }
            task.Status = AgentTaskStatus.Verifying;
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            var duration = DateTime.Now - task.StartTime;
            var finishTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)} ({Helpers.FormatHelpers.FormatTokenCount(task.InputTokens)} in / {Helpers.FormatHelpers.FormatTokenCount(task.OutputTokens)} out)"
                : "";
            _outputProcessor.AppendOutput(task.Id, $"[Feature Mode] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).{finishTokenInfo}\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = CompleteFeatureModeWithVerificationAsync(task, status, activeTasks, historyTasks, moveToHistory);
        }

        private async System.Threading.Tasks.Task CompleteFeatureModeWithVerificationAsync(AgentTask task, AgentTaskStatus finalStatus,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            await _outputProcessor.AppendCompletionSummary(task, activeTasks, historyTasks, finalStatus);
            _outputProcessor.TryInjectSubtaskResult(task, activeTasks, historyTasks);

            task.Status = finalStatus;
            task.EndTime = DateTime.Now;
            _outputTabManager.UpdateTabHeader(task);
            moveToHistory(task);
            FeatureModeFinished?.Invoke(task.Id, finalStatus);
        }

        // ── Feature mode iteration decision logic (extracted for testability) ──

        internal enum FeatureModeAction { Skip, Finish, RetryAfterDelay, Continue }

        internal struct FeatureModeDecision
        {
            public FeatureModeAction Action;
            public AgentTaskStatus FinishStatus;
            public int ConsecutiveFailures;
            public bool TrimOutput;
        }

        /// <summary>
        /// Pure decision function that evaluates what the feature mode loop should do next.
        /// </summary>
        internal static FeatureModeDecision EvaluateFeatureModeIteration(
            AgentTaskStatus currentStatus,
            TimeSpan totalRuntime,
            string iterationOutput,
            int currentIteration,
            int maxIterations,
            int exitCode,
            int consecutiveFailures,
            int outputLength)
        {
            if (currentStatus != AgentTaskStatus.Running)
                return new FeatureModeDecision { Action = FeatureModeAction.Skip };

            if (totalRuntime.TotalHours >= FeatureModeMaxRuntimeHours)
                return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (TaskLauncher.CheckFeatureModeComplete(iterationOutput))
                return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (currentIteration >= maxIterations)
                return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            var newFailures = consecutiveFailures;
            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                newFailures++;
                if (newFailures >= FeatureModeMaxConsecutiveFailures)
                    return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Failed, ConsecutiveFailures = newFailures };
            }
            else
            {
                newFailures = 0;
            }

            if (TaskLauncher.IsTokenLimitError(iterationOutput))
                return new FeatureModeDecision { Action = FeatureModeAction.RetryAfterDelay, ConsecutiveFailures = newFailures };

            return new FeatureModeDecision
            {
                Action = FeatureModeAction.Continue,
                ConsecutiveFailures = newFailures,
                TrimOutput = outputLength > FeatureModeOutputCapChars
            };
        }
    }
}
