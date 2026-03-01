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
        private readonly ICompletionAnalyzer _completionAnalyzer;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ITaskFactory _taskFactory;
        private readonly Func<int> _getTokenLimitRetryMinutes;

        internal const int FeatureModeMaxRuntimeHours = 12;
        internal const int FeatureModeIterationTimeoutMinutes = 60;
        internal const int FeatureModeMaxConsecutiveFailures = 3;
        internal const int FeatureModeOutputCapChars = 100_000;
        internal const int PlanningTeamMemberTimeoutMinutes = 30;

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
            ICompletionAnalyzer completionAnalyzer,
            IPromptBuilder promptBuilder,
            ITaskFactory taskFactory,
            Func<int> getTokenLimitRetryMinutes)
        {
            _scriptDir = scriptDir;
            _processLauncher = processLauncher;
            _outputProcessor = outputProcessor;
            _messageBusManager = messageBusManager;
            _outputTabManager = outputTabManager;
            _completionAnalyzer = completionAnalyzer;
            _promptBuilder = promptBuilder;
            _taskFactory = taskFactory;
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

            // Trim OutputBuilder if it exceeds the feature mode cap
            if (task.OutputBuilder.Length > FeatureModeOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - FeatureModeOutputCapChars, FeatureModeOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = Math.Min(task.LastIterationOutputStart, task.OutputBuilder.Length);
            }

            // Check runtime cap
            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= FeatureModeMaxRuntimeHours)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Total runtime cap ({FeatureModeMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Check for token limit error first
            if (_completionAnalyzer.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveTokenLimitRetries++;
                const int maxTokenLimitRetries = 5;

                if (task.ConsecutiveTokenLimitRetries >= maxTokenLimitRetries)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Max token limit retries ({maxTokenLimitRetries}) reached. Stopping.\n", activeTasks, historyTasks);
                    FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                    return;
                }

                HandleTokenLimitRetry(task, activeTasks, historyTasks, moveToHistory);
                return;
            }
            else
            {
                // Reset token limit retry counter on successful non-token-limit run
                task.ConsecutiveTokenLimitRetries = 0;
            }

            // Check consecutive failures (only if not a token limit error)
            if (exitCode != 0)
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
                // Only reset consecutive failures when exitCode is 0
                task.ConsecutiveFailures = 0;
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

            // Mark the parent as the Main coordinator so the UI clearly identifies it
            if (string.IsNullOrWhiteSpace(task.Summary) || !task.Summary.StartsWith("[Main]"))
                task.Summary = $"[Main] {(task.Summary ?? _taskFactory.GenerateLocalSummary(task.OriginalFeatureDescription))}";

            // First pass: Configure team members for planning only (no file modifications)
            task.ClearFeaturePhaseChildIds();
            foreach (var child in children)
            {
                child.SpawnTeam = false;
                child.AutoDecompose = false;
                child.IsFeatureMode = false;
                child.UseMessageBus = true;
                child.NoGitWrite = true;
                child.SkipPermissions = true;
                // Inject planning-only restriction so team members don't write files
                // (e.g., ARCHITECTURE.md) that could conflict between concurrent agents
                child.AdditionalInstructions = PromptBuilder.PlanningTeamMemberBlock +
                    (child.AdditionalInstructions ?? "");
            }

            // Second pass: Spawn all children, tracking successes
            var spawnedCount = 0;
            bool anySpawnFailed = false;
            foreach (var child in children)
            {
                try
                {
                    task.AddFeaturePhaseChildId(child.Id);
                    FeatureModeChildSpawned?.Invoke(task, child);
                    spawnedCount++;
                }
                catch (Exception ex)
                {
                    anySpawnFailed = true;
                    AppLogger.Warn("FeatureMode", $"Failed to spawn team member '{child.Summary}' for task {task.Id}", ex);
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[Feature Mode] WARNING: Failed to spawn team member: {ex.Message}\n",
                        activeTasks, historyTasks);
                    break; // Stop spawning more children on first failure
                }
            }

            // If any spawn failed or no children were spawned successfully, clean up
            if (anySpawnFailed || spawnedCount == 0)
            {
                // Clean up any successfully spawned children
                if (spawnedCount > 0)
                {
                    CleanupSpawnedChildren(task, activeTasks, historyTasks);
                }

                _outputProcessor.AppendOutput(task.Id,
                    "\n[Feature Mode] Team member spawn failures — falling back to direct plan consolidation.\n",
                    activeTasks, historyTasks);
                task.FeatureModePhase = FeatureModePhase.PlanConsolidation;
                StartPlanConsolidationProcess(task, "", activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Refresh dependency display info now that TaskNumbers are assigned
            TaskExecutionManager.RefreshDependencyDisplayInfo(children, "team member");

            task.FeatureModePhase = FeatureModePhase.TeamPlanning;
            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] Planning team spawned with {spawnedCount} member(s). Waiting for team to complete...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Safety timeout: kill any stuck team members after the planning timeout
            StartPhaseTimeout(task, PlanningTeamMemberTimeoutMinutes, activeTasks, historyTasks, moveToHistory);
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

            // Cancel phase timeout since all children completed
            CancelPhaseTimeout(task);

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
            task.ClearFeaturePhaseChildIds();

            StartPlanConsolidationProcess(task, teamResults, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 2 process: Run consolidation agent that reads team results and outputs FEATURE_STEPS.
        /// </summary>
        private void StartPlanConsolidationProcess(AgentTask task, string teamResults,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var prompt = _promptBuilder.BuildFeatureModePlanConsolidationPrompt(
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
            List<AgentTask> children;
            try
            {
                children = SpawnExecutionTasks(task, steps, activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureMode", $"Failed to spawn execution tasks for task {task.Id}", ex);
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Feature Mode] ERROR spawning execution tasks: {ex.Message}. Finishing.\n",
                    activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            task.FeatureModePhase = FeatureModePhase.Execution;
            task.ClearFeaturePhaseChildIds();

            // Spawn all children, tracking successes
            var spawnedCount = 0;
            bool anySpawnFailed = false;
            foreach (var child in children)
            {
                try
                {
                    task.AddFeaturePhaseChildId(child.Id);
                    FeatureModeChildSpawned?.Invoke(task, child);
                    spawnedCount++;
                }
                catch (Exception ex)
                {
                    anySpawnFailed = true;
                    AppLogger.Warn("FeatureMode", $"Failed to spawn execution task '{child.Summary}' for task {task.Id}", ex);
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[Feature Mode] WARNING: Failed to spawn execution step: {ex.Message}\n",
                        activeTasks, historyTasks);
                    break; // Stop spawning more children on first failure
                }
            }

            // If any spawn failed or no children were spawned successfully, clean up
            if (anySpawnFailed || spawnedCount == 0)
            {
                // Clean up any successfully spawned children
                if (spawnedCount > 0)
                {
                    CleanupSpawnedChildren(task, activeTasks, historyTasks);
                }

                _outputProcessor.AppendOutput(task.Id,
                    "\n[Feature Mode] Execution task spawn failures. Finishing.\n",
                    activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Refresh dependency display info now that TaskNumbers are assigned
            TaskExecutionManager.RefreshDependencyDisplayInfo(children, "step");

            _outputProcessor.AppendOutput(task.Id,
                $"\n[Feature Mode] {spawnedCount} execution task(s) spawned. Waiting for completion...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Safety timeout: kill stuck execution tasks after the iteration timeout
            StartPhaseTimeout(task, FeatureModeIterationTimeoutMinutes, activeTasks, historyTasks, moveToHistory);
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
            task.ClearFeaturePhaseChildIds();

            var prompt = _promptBuilder.BuildFeatureModeEvaluationPrompt(
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
            if (_completionAnalyzer.CheckFeatureModeComplete(output))
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
            task.ClearFeaturePhaseChildIds();
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

            var phaseModel = PromptBuilder.GetCliModelForPhase(task.FeatureModePhase);
            _outputProcessor.AppendOutput(task.Id,
                $"\n[HappyEngine] Phase: {task.FeatureModePhase} | Model: {PromptBuilder.GetFriendlyModelName(phaseModel)} ({phaseModel})\n",
                activeTasks, historyTasks);
            var claudeCmd = _promptBuilder.BuildClaudeCommand(task.SkipPermissions, task.RemoteSession, phaseModel);

            var ps1File = Path.Combine(_scriptDir, $"feature_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
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
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "FEATURE_STEPS");
            if (json == null)
            {
                AppLogger.Warn("FeatureMode", "No ```FEATURE_STEPS``` block found in output");
                return null;
            }

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
                var child = _taskFactory.CreateTask(
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
                child.Summary = _taskFactory.GenerateLocalSummary(step.Description);
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
        private const int MaxPerChildChars = 2_000;
        private const int MaxTotalResultsChars = 12_000;

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

                // Build per-child content using Summary (short title) instead of full Description
                var childSb = new StringBuilder();
                childSb.AppendLine($"### Result #{idx}: {title}");
                childSb.AppendLine($"**Status:** {child.Status}");
                childSb.AppendLine($"**Task:** {child.Summary ?? $"Task #{child.TaskNumber}"}");

                if (!string.IsNullOrWhiteSpace(child.CompletionSummary))
                    childSb.AppendLine($"**Changes:**\n{child.CompletionSummary}");

                if (!string.IsNullOrWhiteSpace(child.Recommendations))
                    childSb.AppendLine($"**Recommendations:**\n{child.Recommendations}");

                childSb.AppendLine();

                // Cap each child's combined output
                var childText = childSb.ToString();
                if (childText.Length > MaxPerChildChars)
                    childText = childText.Substring(0, MaxPerChildChars) + "\n[...truncated]\n";

                sb.Append(childText);
            }

            // Apply total results cap
            var result = sb.ToString();
            if (result.Length > MaxTotalResultsChars)
            {
                _outputProcessor.AppendOutput(parent.Id,
                    $"\n[Feature Mode] Token optimization: child results truncated from {result.Length:N0} to {MaxTotalResultsChars:N0} chars ({idx} children, phase {parent.FeatureModePhase})\n",
                    activeTasks, historyTasks);
                result = result.Substring(0, MaxTotalResultsChars) + "\n[...remaining results truncated]\n";
            }

            return result;
        }

        // ── Phase timeout (stuck child detection) ─────────────────────────

        /// <summary>
        /// Starts a timer that kills any still-running children after the specified timeout.
        /// Uses the task's FeatureModeIterationTimer slot (which is free during child-wait phases).
        /// </summary>
        private void StartPhaseTimeout(AgentTask task, int timeoutMinutes,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            CancelPhaseTimeout(task);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(timeoutMinutes)
            };
            task.FeatureModeIterationTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                task.FeatureModeIterationTimer = null;
                if (task.Status != AgentTaskStatus.Running) return;

                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Feature Mode] Phase timeout ({timeoutMinutes}min). Killing stuck child tasks...\n",
                    activeTasks, historyTasks);

                // Kill any still-running children
                foreach (var childId in task.FeaturePhaseChildIds)
                {
                    var child = activeTasks.FirstOrDefault(t => t.Id == childId);
                    if (child is { IsFinished: false, Process: { HasExited: false } })
                    {
                        _outputProcessor.AppendOutput(task.Id,
                            $"[Feature Mode] Killing stuck child #{child.TaskNumber}: {child.Summary}\n",
                            activeTasks, historyTasks);
                        try { child.Process.Kill(true); }
                        catch (Exception ex) { AppLogger.Warn("FeatureMode", $"Failed to kill stuck child {child.Id}", ex); }
                    }
                }
            };
            timer.Start();
        }

        private static void CancelPhaseTimeout(AgentTask task)
        {
            if (task.FeatureModeIterationTimer != null)
            {
                task.FeatureModeIterationTimer.Stop();
                task.FeatureModeIterationTimer = null;
            }
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
                        var evalPrompt = _promptBuilder.BuildFeatureModeEvaluationPrompt(
                            task.CurrentIteration, task.MaxIterations, task.OriginalFeatureDescription, execResults);
                        StartFeatureModeProcess(task, evalPrompt, activeTasks, historyTasks, moveToHistory);
                        break;
                }
            };
            timer.Start();
        }

        // ── Cleanup helpers ─────────────────────────────────────────────

        /// <summary>
        /// Cancels and cleans up any spawned children in the current phase.
        /// Used when child spawning partially fails to prevent orphaned tasks.
        /// </summary>
        private void CleanupSpawnedChildren(AgentTask parent, ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            if (parent.FeaturePhaseChildIds.Count == 0) return;

            _outputProcessor.AppendOutput(parent.Id,
                $"\n[Feature Mode] Cleaning up {parent.FeaturePhaseChildIds.Count} spawned children...\n",
                activeTasks, historyTasks);

            foreach (var childId in parent.FeaturePhaseChildIds.ToList())
            {
                var child = activeTasks.FirstOrDefault(t => t.Id == childId);
                if (child != null)
                {
                    // Set status to cancelled
                    child.Status = AgentTaskStatus.Cancelled;
                    child.EndTime = DateTime.Now;

                    // Kill process if running
                    if (child.Process is { HasExited: false })
                    {
                        try
                        {
                            child.Process.Kill(true);
                            _outputProcessor.AppendOutput(parent.Id,
                                $"[Feature Mode] Killed child process #{child.TaskNumber}: {child.Summary}\n",
                                activeTasks, historyTasks);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("FeatureMode", $"Failed to kill child process {child.Id}", ex);
                        }
                    }

                    // Remove from message bus if applicable
                    if (child.UseMessageBus)
                    {
                        _messageBusManager.LeaveBus(child.ProjectPath, child.Id);
                    }

                    _outputProcessor.AppendOutput(child.Id,
                        "\n[Task cancelled due to sibling spawn failure]\n",
                        activeTasks, historyTasks);
                }
            }

            // Clear the child IDs list
            parent.ClearFeaturePhaseChildIds();
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

            // Directly set final status without verifying step
            task.Status = status;
            task.EndTime = DateTime.Now;

            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);

            var duration = DateTime.Now - task.StartTime;
            var finishTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)} ({Helpers.FormatHelpers.FormatTokenCount(task.InputTokens)} in / {Helpers.FormatHelpers.FormatTokenCount(task.OutputTokens)} out)"
                : "";
            _outputProcessor.AppendOutput(task.Id, $"[Feature Mode] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).{finishTokenInfo}\n", activeTasks, historyTasks);

            _outputTabManager.UpdateTabHeader(task);
            moveToHistory(task);
            FeatureModeFinished?.Invoke(task.Id, status);
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
            ICompletionAnalyzer completionAnalyzer,
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

            if (completionAnalyzer.CheckFeatureModeComplete(iterationOutput))
                return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (currentIteration >= maxIterations)
                return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            var newFailures = consecutiveFailures;
            if (exitCode != 0 && !completionAnalyzer.IsTokenLimitError(iterationOutput))
            {
                newFailures++;
                if (newFailures >= FeatureModeMaxConsecutiveFailures)
                    return new FeatureModeDecision { Action = FeatureModeAction.Finish, FinishStatus = AgentTaskStatus.Failed, ConsecutiveFailures = newFailures };
            }
            else
            {
                newFailures = 0;
            }

            if (completionAnalyzer.IsTokenLimitError(iterationOutput))
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
