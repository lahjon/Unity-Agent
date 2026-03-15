using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages teams mode multi-phase orchestration:
    /// Phase 1 (TeamPlanning): Spawn a planning team that explores the codebase
    /// Phase 2 (PlanConsolidation): Consolidate team findings into step-by-step TEAM_STEPS
    /// Phase 3 (Execution): Create tasks for each step with dependency tracking
    /// Phase 4 (Evaluation): Evaluate all results, decide whether to iterate
    /// </summary>
    public class TeamsModeHandler
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
        private readonly SmartTruncationService _smartTruncationService;
        private readonly ModelComplexityAnalyzer _modelComplexityAnalyzer;
        private readonly EarlyTerminationManager _earlyTerminationManager;
        private readonly IterationMemoryManager _iterationMemoryManager;
        private PlanSynthesizer? _planSynthesizer;

        /// <summary>Maps parent task ID → list of 3 perspective agent IDs (Architecture, Testing, EdgeCases).</summary>
        private readonly ConcurrentDictionary<string, string[]> _perspectiveAgents = new();

        /// <summary>Fires when perspective agents are spawned for the synthesis board (parentTaskId, perspectiveTaskIds[3]).</summary>
        public event Action<string, string[]>? SynthesisPerspectivesSpawned;

        /// <summary>Fires when a perspective agent completes (parentTaskId, perspectiveIndex 0-2).</summary>
        public event Action<string, int>? SynthesisPerspectiveCompleted;

        /// <summary>Fires when plan synthesis is complete (parentTaskId, synthesisResult).</summary>
        public event Action<string, string>? SynthesisComplete;

        internal const int TeamsModeMaxRuntimeHours = 12;
        internal const int TeamsModeIterationTimeoutMinutes = 60;
        internal const int TeamsModeMaxConsecutiveFailures = 3;
        internal const int TeamsModeOutputCapChars = 100_000;
        internal const int PlanningTeamMemberTimeoutMinutes = 30;

        /// <summary>Fires when a new teams mode iteration starts (taskId, iteration).</summary>
        public event Action<string, int>? IterationStarted;

        /// <summary>Fires when the teams mode finishes for a task (taskId, finalStatus).</summary>
        public event Action<string, AgentTaskStatus>? TeamsModeFinished;

        /// <summary>Fires when a team member or step task needs to be spawned (parent, child).</summary>
        public event Action<AgentTask, AgentTask>? TeamsModeChildSpawned;

        /// <summary>Fires to request team extraction from output (parent, output) → list of children.</summary>
        public event Func<AgentTask, string, List<AgentTask>?>? ExtractTeamRequested;

        public TeamsModeHandler(
            string scriptDir,
            TaskProcessLauncher processLauncher,
            OutputProcessor outputProcessor,
            MessageBusManager messageBusManager,
            OutputTabManager outputTabManager,
            ICompletionAnalyzer completionAnalyzer,
            IPromptBuilder promptBuilder,
            ITaskFactory taskFactory,
            Func<int> getTokenLimitRetryMinutes,
            SmartTruncationService? smartTruncationService = null,
            ModelComplexityAnalyzer? modelComplexityAnalyzer = null,
            EarlyTerminationManager? earlyTerminationManager = null,
            IterationMemoryManager? iterationMemoryManager = null)
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
            _smartTruncationService = smartTruncationService ?? new SmartTruncationService();
            _modelComplexityAnalyzer = modelComplexityAnalyzer ?? new ModelComplexityAnalyzer();
            _earlyTerminationManager = earlyTerminationManager ?? new EarlyTerminationManager(new ProgressAnalyzer());
            _iterationMemoryManager = iterationMemoryManager ?? new IterationMemoryManager();
        }

        /// <summary>Sets the plan synthesizer for merging perspective agent outputs.</summary>
        public void SetPlanSynthesizer(PlanSynthesizer synthesizer) => _planSynthesizer = synthesizer;

        // ── Main entry point: called when a teams mode task's process exits ──

        public void HandleTeamsModeIteration(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.TeamsModeIterationTimer != null)
            {
                task.TeamsModeIterationTimer.Stop();
                task.TeamsModeIterationTimer = null;
            }

            if (task.Status != AgentTaskStatus.Running) return;

            var fullOutput = task.OutputBuilder.ToString();
            var iterationOutput = task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;

            // Trim OutputBuilder if it exceeds the teams mode cap
            if (task.OutputBuilder.Length > TeamsModeOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - TeamsModeOutputCapChars, TeamsModeOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = Math.Min(task.LastIterationOutputStart, task.OutputBuilder.Length);
            }

            // Check runtime cap
            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= TeamsModeMaxRuntimeHours)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Total runtime cap ({TeamsModeMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Check for token limit error first (only on non-zero exit code to avoid false positives)
            if (exitCode != 0 && _completionAnalyzer.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveTokenLimitRetries++;
                const int maxTokenLimitRetries = 5;

                if (task.ConsecutiveTokenLimitRetries >= maxTokenLimitRetries)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Max token limit retries ({maxTokenLimitRetries}) reached. Stopping.\n", activeTasks, historyTasks);
                    FinishTeamsModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
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
                _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Phase process exited with code {exitCode} (failure {task.ConsecutiveFailures}/{TeamsModeMaxConsecutiveFailures})\n", activeTasks, historyTasks);
                if (task.ConsecutiveFailures >= TeamsModeMaxConsecutiveFailures)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] {TeamsModeMaxConsecutiveFailures} consecutive failures. Stopping.\n", activeTasks, historyTasks);
                    FinishTeamsModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
            }
            else
            {
                // Only reset consecutive failures when exitCode is 0
                task.ConsecutiveFailures = 0;
            }

            // ── Early termination check ──────────────────────────────────
            // Only evaluate during Evaluation phase (end of a full logical iteration).
            // Planning/consolidation phases are read-only by design and produce no file
            // changes, which would falsely trigger stall detection.
            var shouldEvaluateTermination = task.TeamsModePhase == TeamsModePhase.Evaluation;
            var terminationDecision = shouldEvaluateTermination
                ? _earlyTerminationManager.EvaluateTermination(task, iterationOutput)
                : new TerminationDecision { ShouldTerminate = false, Reason = TerminationReason.None };
            if (terminationDecision.ShouldTerminate)
            {
                var reason = terminationDecision.Reason switch
                {
                    TerminationReason.CriticalFailure => "critical failure detected",
                    TerminationReason.PersistentStall => "persistent stall detected",
                    TerminationReason.BudgetExceeded => "token budget exceeded",
                    _ => "early termination triggered"
                };
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Teams Mode] Early termination: {reason} (confidence: {terminationDecision.Confidence:P0})\n" +
                    $"[Teams Mode] Detail: {terminationDecision.Explanation}\n",
                    activeTasks, historyTasks);

                // Log evidence from each check
                foreach (var check in terminationDecision.Checks)
                {
                    if (check.Severity >= CheckSeverity.Warning)
                    {
                        _outputProcessor.AppendOutput(task.Id,
                            $"[Teams Mode]   • {check.Type}: {check.Description}\n",
                            activeTasks, historyTasks);
                    }
                }

                FinishTeamsModeTask(task,
                    terminationDecision.Reason == TerminationReason.CriticalFailure
                        ? AgentTaskStatus.Failed
                        : AgentTaskStatus.Completed,
                    activeTasks, historyTasks, moveToHistory);
                return;
            }
            else if (!string.IsNullOrEmpty(terminationDecision.RecommendedAction))
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"[Teams Mode] Progress monitor: {terminationDecision.Explanation} — {terminationDecision.RecommendedAction}\n",
                    activeTasks, historyTasks);
            }

            // Route based on current phase
            switch (task.TeamsModePhase)
            {
                case TeamsModePhase.None:
                    // Initial planning process completed — extract team
                    HandlePlanningProcessComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                case TeamsModePhase.PlanConsolidation:
                    // Consolidation process completed — extract steps
                    HandleConsolidationComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                case TeamsModePhase.Evaluation:
                    // Evaluation process completed — check if done or iterate
                    HandleEvaluationComplete(task, iterationOutput, activeTasks, historyTasks, moveToHistory);
                    break;

                default:
                    // TeamPlanning or Execution phases don't have their own process — they wait for children
                    _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Unexpected process exit in phase {task.TeamsModePhase}.\n", activeTasks, historyTasks);
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
                    "\n[Teams Mode] No team produced — falling back to direct plan consolidation.\n",
                    activeTasks, historyTasks);

                // Skip team phase, go directly to plan consolidation
                task.TeamsModePhase = TeamsModePhase.PlanConsolidation;
                StartPlanConsolidationProcess(task, "", activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Mark the parent as the Main coordinator so the UI clearly identifies it
            if (string.IsNullOrWhiteSpace(task.Summary) || !task.Summary.StartsWith("[Main]"))
                task.Summary = $"[Main] {(task.Summary ?? _taskFactory.GenerateLocalSummary(task.OriginalTeamsDescription))}";

            // First pass: Configure team members for planning only (no file modifications)
            task.ClearTeamsPhaseChildIds();
            foreach (var child in children)
            {
                child.SpawnTeam = false;
                child.AutoDecompose = false;
                child.IsTeamsMode = false;
                child.UseMessageBus = true;
                child.SkipPermissions = true;
                // Propagate parent's feature context so planning members know which files to explore
                if (!string.IsNullOrWhiteSpace(task.Runtime.FeatureContextBlock))
                    child.Runtime.FeatureContextBlock = task.Runtime.FeatureContextBlock;
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
                    task.AddTeamsPhaseChildId(child.Id);
                    TeamsModeChildSpawned?.Invoke(task, child);
                    spawnedCount++;
                }
                catch (Exception ex)
                {
                    anySpawnFailed = true;
                    AppLogger.Warn("TeamsMode", $"Failed to spawn team member '{child.Summary}' for task {task.Id}", ex);
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[Teams Mode] WARNING: Failed to spawn team member: {ex.Message}\n",
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
                    "\n[Teams Mode] Team member spawn failures — falling back to direct plan consolidation.\n",
                    activeTasks, historyTasks);
                task.TeamsModePhase = TeamsModePhase.PlanConsolidation;
                StartPlanConsolidationProcess(task, "", activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Refresh dependency display info now that TaskNumbers are assigned
            TaskExecutionManager.RefreshDependencyDisplayInfo(children, "team member");

            // Spawn 3 parallel perspective agents for synthesis board
            SpawnSynthesisPerspectiveAgents(task, activeTasks, historyTasks);

            task.TeamsModePhase = TeamsModePhase.TeamPlanning;
            _outputProcessor.AppendOutput(task.Id,
                $"\n[Teams Mode] Planning team spawned with {spawnedCount} member(s) + 3 perspective agents. Waiting for team to complete...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Safety timeout: kill any stuck team members after the planning timeout
            StartPhaseTimeout(task, PlanningTeamMemberTimeoutMinutes, activeTasks, historyTasks, moveToHistory);
        }

        // ── Synthesis perspective agents ────────────────────────────────

        /// <summary>
        /// Spawns 3 parallel Sonnet agents with distinct perspectives (Architecture, Testing, Edge Cases)
        /// as additional team members for the synthesis board.
        /// </summary>
        private void SpawnSynthesisPerspectiveAgents(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var perspectiveIds = new string[3];

            for (int i = 0; i < 3; i++)
            {
                var perspectiveName = PromptBuilder.SynthesisPerspectiveNames[i];
                var perspectivePrompt = PromptBuilder.SynthesisPerspectives[i];

                var description = $"[Synth:{perspectiveName}] Analyze from {perspectiveName} perspective: {task.OriginalTeamsDescription}";
                var child = _taskFactory.CreateTask(
                    description,
                    task.ProjectPath,
                    skipPermissions: true,
                    headless: false,
                    isTeamsMode: false,
                    ignoreFileLocks: false,
                    useMcp: task.UseMcp,
                    spawnTeam: false,
                    extendedPlanning: false,
                    planOnly: false,
                    useMessageBus: true,
                    model: task.Model,
                    parentTaskId: task.Id);

                child.ProjectColor = task.ProjectColor;
                child.ProjectDisplayName = task.ProjectDisplayName;
                child.Summary = $"[Synth:{perspectiveName}]";
                child.AutoDecompose = false;

                if (!string.IsNullOrWhiteSpace(task.Runtime.FeatureContextBlock))
                    child.Runtime.FeatureContextBlock = task.Runtime.FeatureContextBlock;

                child.AdditionalInstructions = perspectivePrompt +
                    PromptBuilder.PlanningTeamMemberBlock +
                    (child.AdditionalInstructions ?? "");

                perspectiveIds[i] = child.Id;

                try
                {
                    task.AddTeamsPhaseChildId(child.Id);
                    TeamsModeChildSpawned?.Invoke(task, child);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TeamsMode", $"Failed to spawn synthesis perspective '{perspectiveName}' for task {task.Id}", ex);
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[Synthesis] WARNING: Failed to spawn {perspectiveName} perspective: {ex.Message}\n",
                        activeTasks, historyTasks);
                }
            }

            _perspectiveAgents[task.Id] = perspectiveIds;
            _outputProcessor.AppendOutput(task.Id,
                "\n[Synthesis] 3 perspective agents spawned (Architecture, Testing, Edge Cases).\n",
                activeTasks, historyTasks);

            SynthesisPerspectivesSpawned?.Invoke(task.Id, perspectiveIds);
        }

        /// <summary>
        /// Checks if a completed child task is a perspective agent and fires the appropriate event.
        /// Call this from the task completion handler.
        /// </summary>
        public void NotifyChildCompleted(string parentTaskId, string childTaskId)
        {
            if (!_perspectiveAgents.TryGetValue(parentTaskId, out var ids)) return;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == childTaskId)
                {
                    SynthesisPerspectiveCompleted?.Invoke(parentTaskId, i);
                    return;
                }
            }
        }

        /// <summary>
        /// Called when all children of the current phase have completed.
        /// Routes to the next phase based on current TeamsModePhase.
        /// </summary>
        public void OnTeamsModePhaseComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            // Cancel phase timeout since all children completed
            CancelPhaseTimeout(task);

            switch (task.TeamsModePhase)
            {
                case TeamsModePhase.TeamPlanning:
                    OnTeamPlanningComplete(task, activeTasks, historyTasks, moveToHistory);
                    break;

                case TeamsModePhase.Execution:
                    OnExecutionComplete(task, activeTasks, historyTasks, moveToHistory);
                    break;
            }
        }

        /// <summary>
        /// Phase 1 → Phase 2: All planning team members finished. Run synthesis on perspective agents,
        /// then consolidate into TEAM_STEPS.
        /// </summary>
        private void OnTeamPlanningComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            _outputProcessor.AppendOutput(task.Id,
                "\n[Teams Mode] Planning team complete. Running synthesis...\n",
                activeTasks, historyTasks);

            // Collect team member results
            var teamResults = CollectChildResults(task, activeTasks, historyTasks);

            // Run perspective synthesis if perspective agents were spawned
            if (_planSynthesizer != null && _perspectiveAgents.TryGetValue(task.Id, out var perspectiveIds))
            {
                _ = RunSynthesisAndConsolidateAsync(task, teamResults, perspectiveIds,
                    activeTasks, historyTasks, moveToHistory);
            }
            else
            {
                // No synthesizer or no perspective agents — proceed directly
                task.TeamsModePhase = TeamsModePhase.PlanConsolidation;
                task.ClearTeamsPhaseChildIds();
                StartPlanConsolidationProcess(task, teamResults, activeTasks, historyTasks, moveToHistory);
            }
        }

        /// <summary>
        /// Runs Haiku synthesis on perspective outputs, then proceeds to plan consolidation.
        /// </summary>
        private async System.Threading.Tasks.Task RunSynthesisAndConsolidateAsync(AgentTask task, string teamResults,
            string[] perspectiveIds, ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks, Action<AgentTask> moveToHistory)
        {
            // Collect perspective outputs
            var perspectiveOutputs = new string[3];
            for (int i = 0; i < 3; i++)
            {
                var child = activeTasks.FirstOrDefault(t => t.Id == perspectiveIds[i])
                         ?? historyTasks.FirstOrDefault(t => t.Id == perspectiveIds[i]);
                if (child != null)
                {
                    var output = child.CompletionSummary;
                    if (string.IsNullOrWhiteSpace(output))
                        output = child.OutputBuilder.ToString();
                    perspectiveOutputs[i] = output ?? "";
                }
                else
                {
                    perspectiveOutputs[i] = "";
                }
            }

            _outputProcessor.AppendOutput(task.Id,
                "[Synthesis] Running Haiku plan synthesis across 3 perspectives...\n",
                activeTasks, historyTasks);

            string? synthesis = null;
            try
            {
                synthesis = await _planSynthesizer!.SynthesizeAsync(
                    perspectiveOutputs[0], perspectiveOutputs[1], perspectiveOutputs[2],
                    task.OriginalTeamsDescription);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"Plan synthesis failed for task {task.Id}", ex);
                _outputProcessor.AppendOutput(task.Id,
                    $"[Synthesis] WARNING: Synthesis failed ({ex.Message}), proceeding with raw team results.\n",
                    activeTasks, historyTasks);
            }

            // Clean up perspective agent tracking
            _perspectiveAgents.TryRemove(task.Id, out _);

            // Continue on dispatcher thread
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (task.Status != AgentTaskStatus.Running) return;

                string consolidationInput;
                if (!string.IsNullOrEmpty(synthesis))
                {
                    _outputProcessor.AppendOutput(task.Id,
                        "[Synthesis] Plan synthesis complete. Including in consolidation.\n",
                        activeTasks, historyTasks);
                    SynthesisComplete?.Invoke(task.Id, synthesis);
                    consolidationInput = teamResults + "\n\n" + synthesis;
                }
                else
                {
                    consolidationInput = teamResults;
                }

                task.TeamsModePhase = TeamsModePhase.PlanConsolidation;
                task.ClearTeamsPhaseChildIds();
                StartPlanConsolidationProcess(task, consolidationInput, activeTasks, historyTasks, moveToHistory);
            });
        }

        /// <summary>
        /// Phase 2 process: Run consolidation agent that reads team results and outputs TEAM_STEPS.
        /// </summary>
        private void StartPlanConsolidationProcess(AgentTask task, string teamResults,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var prompt = _promptBuilder.BuildTeamsModePlanConsolidationPrompt(
                task.CurrentIteration, task.MaxIterations, teamResults, task.OriginalTeamsDescription);

            StartTeamsModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 2 complete: Parse TEAM_STEPS and create execution tasks.
        /// </summary>
        private void HandleConsolidationComplete(AgentTask task, string output,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var steps = ExtractFeatureSteps(output);

            if (steps == null || steps.Count == 0)
            {
                _outputProcessor.AppendOutput(task.Id,
                    "\n[Teams Mode] No TEAM_STEPS found in consolidation output. Finishing.\n",
                    activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            _outputProcessor.AppendOutput(task.Id,
                $"\n[Teams Mode] Plan consolidated: {steps.Count} step(s). Creating execution tasks...\n",
                activeTasks, historyTasks);

            // Spawn execution tasks with dependencies
            List<AgentTask> children;
            try
            {
                children = SpawnExecutionTasks(task, steps, activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"Failed to spawn execution tasks for task {task.Id}", ex);
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Teams Mode] ERROR spawning execution tasks: {ex.Message}. Finishing.\n",
                    activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            task.TeamsModePhase = TeamsModePhase.Execution;
            task.ClearTeamsPhaseChildIds();

            // Spawn all children, tracking successes
            var spawnedCount = 0;
            bool anySpawnFailed = false;
            foreach (var child in children)
            {
                try
                {
                    task.AddTeamsPhaseChildId(child.Id);
                    TeamsModeChildSpawned?.Invoke(task, child);
                    spawnedCount++;
                }
                catch (Exception ex)
                {
                    anySpawnFailed = true;
                    AppLogger.Warn("TeamsMode", $"Failed to spawn execution task '{child.Summary}' for task {task.Id}", ex);
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[Teams Mode] WARNING: Failed to spawn execution step: {ex.Message}\n",
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
                    "\n[Teams Mode] Execution task spawn failures. Finishing.\n",
                    activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Refresh dependency display info now that TaskNumbers are assigned
            TaskExecutionManager.RefreshDependencyDisplayInfo(children, "step");

            _outputProcessor.AppendOutput(task.Id,
                $"\n[Teams Mode] {spawnedCount} execution task(s) spawned. Waiting for completion...\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Safety timeout: kill stuck execution tasks after the iteration timeout
            StartPhaseTimeout(task, TeamsModeIterationTimeoutMinutes, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 3 → Phase 4: All execution tasks finished. Run evaluation.
        /// </summary>
        private void OnExecutionComplete(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            _outputProcessor.AppendOutput(task.Id,
                "\n[Teams Mode] All execution tasks complete. Starting evaluation...\n",
                activeTasks, historyTasks);

            // Collect execution results
            var executionResults = CollectChildResults(task, activeTasks, historyTasks);

            // Cache execution child results as iteration memory so subsequent iterations
            // can reference what was done without re-reading all modified files.
            try
            {
                var memory = _iterationMemoryManager.ExtractMemoryFromOutput(executionResults, task.CurrentIteration);
                _iterationMemoryManager.RecordIteration(task.Id, task.CurrentIteration, memory);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"[{task.Id}] Failed to record execution memory", ex);
            }

            task.TeamsModePhase = TeamsModePhase.Evaluation;
            task.ClearTeamsPhaseChildIds();

            var prompt = _promptBuilder.BuildTeamsModeEvaluationPrompt(
                task.CurrentIteration, task.MaxIterations, task.OriginalTeamsDescription, executionResults);

            StartTeamsModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Phase 4 complete: Check STATUS, iterate or finish.
        /// </summary>
        private void HandleEvaluationComplete(AgentTask task, string output,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (_completionAnalyzer.CheckTeamsModeComplete(output))
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Teams Mode] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Feature finished.\n",
                    activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (task.CurrentIteration >= task.MaxIterations)
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Teams Mode] Max iterations ({task.MaxIterations}) reached. Stopping.\n",
                    activeTasks, historyTasks);
                FinishTeamsModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            // Start next iteration
            task.CurrentIteration++;
            task.TeamsModePhase = TeamsModePhase.None;
            task.ClearTeamsPhaseChildIds();
            task.LastIterationOutputStart = task.OutputBuilder.Length;

            // Record structured memory from this iteration for cross-iteration context caching.
            // The next iteration's planning prompt will inject discoveries, failures, and file
            // references so the agent doesn't re-read files or repeat failed approaches.
            try
            {
                var memory = _iterationMemoryManager.ExtractMemoryFromOutput(output, task.CurrentIteration - 1);
                _iterationMemoryManager.RecordIteration(task.Id, task.CurrentIteration - 1, memory);
                AppLogger.Debug("TeamsMode", $"[{task.Id}] Recorded iteration {task.CurrentIteration - 1} memory: " +
                    $"{memory.KeyDiscoveries.Count} discoveries, {memory.FailedApproaches.Count} failures, {memory.ImportantFiles.Count} files");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"[{task.Id}] Failed to record iteration memory", ex);
            }

            var iterRuntime = DateTime.Now - task.StartTime;
            var iterTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)}"
                : "";
            _outputProcessor.AppendOutput(task.Id,
                $"\n[Teams Mode] NEEDS_MORE_WORK — Starting iteration {task.CurrentIteration}/{task.MaxIterations} | Runtime: {(int)iterRuntime.TotalMinutes}m{iterTokenInfo}\n\n",
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
            // Build structured iteration memory context from previous iterations.
            // This injects cached discoveries, failed approaches, and key file references
            // so the agent doesn't waste tokens re-reading files or repeating mistakes.
            var iterationContext = "";
            try
            {
                iterationContext = _iterationMemoryManager.BuildIterationContext(task.Id, task.CurrentIteration);
                if (!string.IsNullOrEmpty(iterationContext))
                {
                    AppLogger.Debug("TeamsMode", $"[{task.Id}] Injecting iteration memory context ({iterationContext.Length} chars) into iteration {task.CurrentIteration}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"[{task.Id}] Failed to build iteration context", ex);
            }

            // Include feature context so iteration planning knows which files/features are relevant
            var featureCtx = !string.IsNullOrWhiteSpace(task.Runtime.FeatureContextBlock)
                ? task.Runtime.FeatureContextBlock + "\n"
                : "";

            // Format iteration template with current iteration number
            var iterationTemplate = string.Format(PromptBuilder.TeamsModeIterationPlanningTemplate,
                task.CurrentIteration);

            var prompt = featureCtx +
                PromptBuilder.TeamsModeInitialTemplate +
                task.OriginalTeamsDescription +
                iterationContext +
                "\n\n" + iterationTemplate +
                previousEvaluation;

            StartTeamsModeProcess(task, prompt, activeTasks, historyTasks, moveToHistory);
        }

        /// <summary>
        /// Launches a new Claude process for the teams mode task with the given prompt.
        /// </summary>
        private void StartTeamsModeProcess(AgentTask task, string prompt,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var promptFile = Path.Combine(_scriptDir, $"feature_{task.Id}_{task.CurrentIteration}_{(int)task.TeamsModePhase}.txt");
            File.WriteAllText(promptFile, prompt, Encoding.UTF8);

            // Dynamic model selection based on complexity
            var phaseModel = DetermineOptimalModel(task, activeTasks, historyTasks);

            if (task.Runtime.LastCliModel != null && task.Runtime.LastCliModel != phaseModel)
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\nSwitching Model to: {PromptBuilder.GetFriendlyModelName(phaseModel)} ({phaseModel})\n",
                    activeTasks, historyTasks);
            }
            task.Runtime.LastCliModel = phaseModel;
            _outputProcessor.AppendOutput(task.Id,
                $"\nPhase: {task.TeamsModePhase} | Model: {PromptBuilder.GetFriendlyModelName(phaseModel)} ({phaseModel})\n",
                activeTasks, historyTasks);
            var claudeCmd = _promptBuilder.BuildClaudeCommand(task.SkipPermissions, phaseModel);

            var ps1File = Path.Combine(_scriptDir, $"feature_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);
                HandleTeamsModeIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);

                var iterationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(TeamsModeIterationTimeoutMinutes)
                };
                task.TeamsModeIterationTimer = iterationTimer;
                iterationTimer.Tick += (_, _) =>
                {
                    iterationTimer.Stop();
                    task.TeamsModeIterationTimer = null;
                    if (task.Process is { HasExited: false })
                    {
                        _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Process timeout ({TeamsModeIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                        try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck teams mode process for task {task.Id}", ex); }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[Teams Mode] ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
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
        /// Extracts TEAM_STEPS JSON block from consolidation output.
        /// </summary>
        private static List<FeatureStepEntry>? ExtractFeatureSteps(string output)
        {
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "TEAM_STEPS");
            if (json == null)
            {
                AppLogger.Warn("TeamsMode", "No ```TEAM_STEPS``` block found in output");
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
                AppLogger.Warn("TeamsMode", "Failed to deserialize TEAM_STEPS JSON", ex);
                return null;
            }
        }

        /// <summary>
        /// Creates execution child tasks from TEAM_STEPS with inter-task dependencies.
        /// </summary>
        private List<AgentTask> SpawnExecutionTasks(AgentTask parent, List<FeatureStepEntry> steps,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var children = new List<AgentTask>();

            // First pass: Create all tasks (without message bus decision)
            foreach (var step in steps)
            {
                var child = _taskFactory.CreateTask(
                    step.Description,
                    parent.ProjectPath,
                    skipPermissions: true,
                    headless: false,
                    isTeamsMode: false,
                    ignoreFileLocks: false,
                    useMcp: parent.UseMcp,
                    spawnTeam: false,
                    extendedPlanning: true,
                    planOnly: false,
                    useMessageBus: false, // Start with false, will determine later
                    model: parent.Model,
                    parentTaskId: parent.Id);

                child.ProjectColor = parent.ProjectColor;
                child.ProjectDisplayName = parent.ProjectDisplayName;
                child.Summary = _taskFactory.GenerateLocalSummary(step.Description);
                child.AutoDecompose = false;
                // Propagate parent's feature context so execution steps don't re-search
                if (!string.IsNullOrWhiteSpace(parent.Runtime.FeatureContextBlock))
                    child.Runtime.FeatureContextBlock = parent.Runtime.FeatureContextBlock;
                // Add autonomous execution instructions + output efficiency
                child.AdditionalInstructions = PromptBuilder.AutonomousExecutionBlock +
                    PromptBuilder.OutputEfficiencyBlock +
                    (child.AdditionalInstructions ?? "");
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

            // Second pass: Determine which tasks need message bus
            DetermineMessageBusUsage(children, steps, parent);

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
            var childResults = new Dictionary<string, string>();
            var childMetadata = new Dictionary<string, (string title, AgentTaskStatus status)>();

            // Collect raw results from children
            foreach (var childId in parent.TeamsPhaseChildIds)
            {
                var child = activeTasks.FirstOrDefault(t => t.Id == childId)
                         ?? historyTasks.FirstOrDefault(t => t.Id == childId);
                if (child == null) continue;

                var title = !string.IsNullOrWhiteSpace(child.Summary) ? child.Summary : $"Task #{child.TaskNumber}";
                childMetadata[childId] = (title, child.Status);

                // Build per-child content
                var childSb = new StringBuilder();
                childSb.AppendLine($"**Status:** {child.Status}");
                childSb.AppendLine($"**Task:** {child.Summary ?? $"Task #{child.TaskNumber}"}");

                if (!string.IsNullOrWhiteSpace(child.CompletionSummary))
                    childSb.AppendLine($"**Changes:**\n{child.CompletionSummary}");

                if (!string.IsNullOrWhiteSpace(child.Recommendations))
                    childSb.AppendLine($"**Recommendations:**\n{child.Recommendations}");

                childResults[childId] = childSb.ToString();
            }

            // Use smart truncation to preserve important information
            var truncatedResults = _smartTruncationService.TruncateMultipleResults(
                childResults,
                MaxTotalResultsChars / 4, // Convert chars to approximate tokens
                preserveBalance: true);

            // Build final output with truncated results
            var sb = new StringBuilder();
            int idx = 0;

            foreach (var truncated in truncatedResults)
            {
                idx++;
                if (childMetadata.TryGetValue(truncated.ChildId!, out var metadata))
                {
                    sb.AppendLine($"### Result #{idx}: {metadata.title}");
                    sb.Append(truncated.Content);

                    // Add truncation notice if significant content was removed
                    if (truncated.TruncationMetrics.TruncationRatio > 0.3)
                    {
                        sb.AppendLine($"\n[Smart truncation: preserved {truncated.PreservedSections.Count} key sections, " +
                                    $"{truncated.TruncationMetrics.ErrorsPreserved} errors, " +
                                    $"{truncated.TruncationMetrics.DecisionsPreserved} decisions]");
                    }
                    sb.AppendLine();
                }
            }

            var result = sb.ToString();

            // Log truncation statistics
            if (truncatedResults.Any(r => r.TruncationMetrics.TruncationRatio > 0))
            {
                var totalOriginal = truncatedResults.Sum(r => r.OriginalLength);
                var totalTruncated = truncatedResults.Sum(r => r.TruncatedLength);
                var avgImportance = truncatedResults.Average(r => r.ImportanceScore);

                _outputProcessor.AppendOutput(parent.Id,
                    $"\n[Teams Mode] Smart truncation: {totalOriginal:N0} → {totalTruncated:N0} chars " +
                    $"({(1.0 - (double)totalTruncated / totalOriginal) * 100:F1}% reduction, " +
                    $"importance score: {avgImportance:F2}, {idx} children)\n",
                    activeTasks, historyTasks);
            }

            return result;
        }

        // ── Phase timeout (stuck child detection) ─────────────────────────

        /// <summary>
        /// Starts a timer that kills any still-running children after the specified timeout.
        /// Uses the task's TeamsModeIterationTimer slot (which is free during child-wait phases).
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
            task.TeamsModeIterationTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                task.TeamsModeIterationTimer = null;
                if (task.Status != AgentTaskStatus.Running) return;

                _outputProcessor.AppendOutput(task.Id,
                    $"\n[Teams Mode] Phase timeout ({timeoutMinutes}min). Killing stuck child tasks...\n",
                    activeTasks, historyTasks);

                // Kill any still-running children
                foreach (var childId in task.TeamsPhaseChildIds)
                {
                    var child = activeTasks.FirstOrDefault(t => t.Id == childId);
                    if (child is { IsFinished: false, Process: { HasExited: false } })
                    {
                        _outputProcessor.AppendOutput(task.Id,
                            $"[Teams Mode] Killing stuck child #{child.TaskNumber}: {child.Summary}\n",
                            activeTasks, historyTasks);
                        try { child.Process.Kill(true); }
                        catch (Exception ex) { AppLogger.Warn("TeamsMode", $"Failed to kill stuck child {child.Id}", ex); }
                    }
                }
            };
            timer.Start();
        }

        private static void CancelPhaseTimeout(AgentTask task)
        {
            if (task.TeamsModeIterationTimer != null)
            {
                task.TeamsModeIterationTimer.Stop();
                task.TeamsModeIterationTimer = null;
            }
        }

        // ── Token limit retry ───────────────────────────────────────────

        private void HandleTokenLimitRetry(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var retryMinutes = _getTokenLimitRetryMinutes();
            _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Token limit hit. Retrying in {retryMinutes} minutes...\n", activeTasks, historyTasks);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(retryMinutes) };
            task.TeamsModeRetryTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                task.TeamsModeRetryTimer = null;
                if (task.Status != AgentTaskStatus.Running) return;
                if ((DateTime.Now - task.StartTime).TotalHours >= TeamsModeMaxRuntimeHours)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Runtime cap reached during retry wait. Stopping.\n", activeTasks, historyTasks);
                    FinishTeamsModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
                _outputProcessor.AppendOutput(task.Id, "[Teams Mode] Retrying...\n", activeTasks, historyTasks);

                // Restart the appropriate phase process
                switch (task.TeamsModePhase)
                {
                    case TeamsModePhase.None:
                        StartIterationPlanningProcess(task, "", activeTasks, historyTasks, moveToHistory);
                        break;
                    case TeamsModePhase.PlanConsolidation:
                        var teamResults = CollectChildResults(task, activeTasks, historyTasks);
                        StartPlanConsolidationProcess(task, teamResults, activeTasks, historyTasks, moveToHistory);
                        break;
                    case TeamsModePhase.Evaluation:
                        var execResults = CollectChildResults(task, activeTasks, historyTasks);
                        var evalPrompt = _promptBuilder.BuildTeamsModeEvaluationPrompt(
                            task.CurrentIteration, task.MaxIterations, task.OriginalTeamsDescription, execResults);
                        StartTeamsModeProcess(task, evalPrompt, activeTasks, historyTasks, moveToHistory);
                        break;
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Determines which execution tasks need message bus based on dependencies.
        /// Only enables message bus when tasks have explicit dependencies that require coordination.
        /// </summary>
        private void DetermineMessageBusUsage(List<AgentTask> tasks, List<FeatureStepEntry> steps, AgentTask parent)
        {
            // Build dependency graph to identify which tasks are depended upon
            var tasksDependedUpon = new HashSet<string>();
            var tasksWithDependencies = new HashSet<string>();

            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].DependsOn is { Count: > 0 })
                {
                    tasksWithDependencies.Add(tasks[i].Id);
                    foreach (var depIdx in steps[i].DependsOn!)
                    {
                        if (depIdx >= 0 && depIdx < tasks.Count && depIdx != i)
                        {
                            tasksDependedUpon.Add(tasks[depIdx].Id);
                        }
                    }
                }
            }

            // Enable message bus for tasks that:
            // 1. Have dependencies (need to read from bus)
            // 2. Are depended upon (need to write to bus)
            // 3. User explicitly enabled it on parent
            int messageBusEnabledCount = 0;
            foreach (var task in tasks)
            {
                bool needsMessageBus = false;

                if (parent.UseMessageBus)
                {
                    needsMessageBus = true;
                }
                else if (tasksWithDependencies.Contains(task.Id))
                {
                    needsMessageBus = true;
                }
                else if (tasksDependedUpon.Contains(task.Id))
                {
                    needsMessageBus = true;
                }

                task.UseMessageBus = needsMessageBus;
                if (needsMessageBus)
                {
                    messageBusEnabledCount++;
                }
            }

            // Log the optimization
            if (messageBusEnabledCount < tasks.Count)
            {
                _outputProcessor.AppendOutput(parent.Id,
                    $"[Token Optimization] Message bus selectively enabled for {messageBusEnabledCount}/{tasks.Count} execution tasks " +
                    $"(saved {tasks.Count - messageBusEnabledCount} unnecessary bus connections)\n",
                    null!, null!);
            }
        }

        /// <summary>
        /// Determines the optimal model for the current phase using complexity analysis.
        /// </summary>
        private string DetermineOptimalModel(AgentTask task,
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            // For phases that don't involve consolidation/evaluation, use default selection
            if (task.TeamsModePhase != TeamsModePhase.PlanConsolidation &&
                task.TeamsModePhase != TeamsModePhase.Evaluation)
            {
                return PromptBuilder.GetCliModelForPhase(task.TeamsModePhase);
            }

            // Collect child results for complexity analysis
            var childResults = new Dictionary<string, string>();
            bool hasErrors = false;

            foreach (var childId in task.TeamsPhaseChildIds)
            {
                var child = activeTasks.FirstOrDefault(t => t.Id == childId)
                         ?? historyTasks.FirstOrDefault(t => t.Id == childId);
                if (child == null) continue;

                // Build simple result content for analysis
                var content = $"Status: {child.Status}\n";
                if (!string.IsNullOrWhiteSpace(child.CompletionSummary))
                    content += $"Summary: {child.CompletionSummary}\n";
                if (!string.IsNullOrWhiteSpace(child.Recommendations))
                    content += $"Recommendations: {child.Recommendations}\n";

                childResults[childId] = content;

                // Check for errors
                if (child.Status == AgentTaskStatus.Failed ||
                    content.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    hasErrors = true;
                }
            }

            // Analyze complexity
            var analysis = _modelComplexityAnalyzer.AnalyzeComplexity(
                task.TeamsPhaseChildIds.Count,
                childResults,
                task.TeamsModePhase,
                hasErrors);

            // Log the decision
            _outputProcessor.AppendOutput(task.Id,
                $"[Model Selection] {analysis.Reasoning.Split('\n')[0]}\n",
                activeTasks, historyTasks);

            // Return the appropriate model constant
            return analysis.RecommendedModel.ToLowerInvariant() == "opus"
                ? PromptBuilder.CliOpusModel
                : PromptBuilder.CliSonnetModel;
        }

        // ── Cleanup helpers ─────────────────────────────────────────────

        /// <summary>
        /// Cancels and cleans up any spawned children in the current phase.
        /// Used when child spawning partially fails to prevent orphaned tasks.
        /// </summary>
        private void CleanupSpawnedChildren(AgentTask parent, ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            if (parent.TeamsPhaseChildIds.Count == 0) return;

            _outputProcessor.AppendOutput(parent.Id,
                $"\n[Teams Mode] Cleaning up {parent.TeamsPhaseChildIds.Count} spawned children...\n",
                activeTasks, historyTasks);

            foreach (var childId in parent.TeamsPhaseChildIds.ToList())
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
                                $"[Teams Mode] Killed child process #{child.TaskNumber}: {child.Summary}\n",
                                activeTasks, historyTasks);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TeamsMode", $"Failed to kill child process {child.Id}", ex);
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
            parent.ClearTeamsPhaseChildIds();
        }

        // ── Finish ──────────────────────────────────────────────────────

        private void FinishTeamsModeTask(AgentTask task, AgentTaskStatus status,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
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

            // Directly set final status without verifying step
            task.Status = status;
            task.EndTime = DateTime.Now;

            // Clean up early termination tracking for this task
            _earlyTerminationManager.ClearTaskState(task.Id);

            // Clean up iteration memory files for this task (data already consumed)
            try { _iterationMemoryManager.CleanupOldMemories(TimeSpan.FromDays(7)); }
            catch (Exception ex) { AppLogger.Debug("TeamsMode", $"Iteration memory cleanup: {ex.Message}"); }

            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);

            var duration = DateTime.Now - task.StartTime;
            var finishTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)} ({Helpers.FormatHelpers.FormatTokenCount(task.InputTokens)} in / {Helpers.FormatHelpers.FormatTokenCount(task.OutputTokens)} out)"
                : "";
            _outputProcessor.AppendOutput(task.Id, $"[Teams Mode] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).{finishTokenInfo}\n", activeTasks, historyTasks);

            _outputTabManager.UpdateTabHeader(task);
            moveToHistory(task);
            TeamsModeFinished?.Invoke(task.Id, status);
        }


        // ── Feature mode iteration decision logic (extracted for testability) ──

        internal enum TeamsModeAction { Skip, Finish, RetryAfterDelay, Continue }

        internal struct FeatureModeDecision
        {
            public TeamsModeAction Action;
            public AgentTaskStatus FinishStatus;
            public int ConsecutiveFailures;
            public bool TrimOutput;
        }

        /// <summary>
        /// Pure decision function that evaluates what the teams mode loop should do next.
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
                return new FeatureModeDecision { Action = TeamsModeAction.Skip };

            if (totalRuntime.TotalHours >= TeamsModeMaxRuntimeHours)
                return new FeatureModeDecision { Action = TeamsModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (completionAnalyzer.CheckTeamsModeComplete(iterationOutput))
                return new FeatureModeDecision { Action = TeamsModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (currentIteration >= maxIterations)
                return new FeatureModeDecision { Action = TeamsModeAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            var newFailures = consecutiveFailures;
            if (exitCode != 0 && !completionAnalyzer.IsTokenLimitError(iterationOutput))
            {
                newFailures++;
                if (newFailures >= TeamsModeMaxConsecutiveFailures)
                    return new FeatureModeDecision { Action = TeamsModeAction.Finish, FinishStatus = AgentTaskStatus.Failed, ConsecutiveFailures = newFailures };
            }
            else
            {
                newFailures = 0;
            }

            if (exitCode != 0 && completionAnalyzer.IsTokenLimitError(iterationOutput))
                return new FeatureModeDecision { Action = TeamsModeAction.RetryAfterDelay, ConsecutiveFailures = newFailures };

            return new FeatureModeDecision
            {
                Action = TeamsModeAction.Continue,
                ConsecutiveFailures = newFailures,
                TrimOutput = outputLength > TeamsModeOutputCapChars
            };
        }
    }
}
