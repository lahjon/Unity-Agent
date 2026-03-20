using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Spritely.Helpers;
using Spritely.Services;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Thin coordinator that delegates to focused single-responsibility classes:
    /// <see cref="TaskProcessLauncher"/> for subprocess creation and process lifecycle,
    /// <see cref="TeamsModeHandler"/> for teams mode retry timers and iteration tracking,
    /// <see cref="OutputProcessor"/> for output trimming, completion summaries, and recommendation parsing,
    /// <see cref="TokenLimitHandler"/> for token limit detection and retry scheduling.
    /// </summary>
    public class TaskExecutionManager
    {
        private readonly string _scriptDir;
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<string> _getSystemPrompt;
        private readonly Func<AgentTask, string> _getProjectDescription;
        private readonly Func<string, string> _getProjectRulesBlock;
        private readonly Func<string, bool> _isGameProject;
        private readonly Func<string> _getSkillsBlock;
        private readonly Func<string> _getOpusEffortLevel;
        private readonly MessageBusManager _messageBusManager;
        private readonly Dispatcher _dispatcher;

        // ── Injected services ────────────────────────────────────────
        private readonly IGitHelper _gitHelper;
        private readonly ICompletionAnalyzer _completionAnalyzer;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ITaskFactory _taskFactory;
        private readonly GitOperationGuard _gitOperationGuard;

        // ── Focused sub-handlers ─────────────────────────────────────
        private readonly OutputProcessor _outputProcessor;
        private readonly TaskProcessLauncher _processLauncher;
        private readonly TeamsModeHandler _featureModeHandler;
        private readonly TokenLimitHandler _tokenLimitHandler;
        private readonly EarlyTerminationManager _earlyTerminationManager;
        private readonly TaskPreprocessor _taskPreprocessor;

        // ── Feature System ──────────────────────────────────────────────
        private readonly FeatureRegistryManager _featureRegistryManager;
        private readonly FeatureContextResolver _featureContextResolver;
        private readonly FeatureUpdateAgent _featureUpdateAgent;
        private readonly HybridSearchManager? _hybridSearchManager;
        private readonly ContextPrefetchPipeline? _contextPrefetchPipeline;

        // ── Feedback System ──────────────────────────────────────────────
        private readonly FeedbackCollector? _feedbackCollector;

        // ── Prompt Evolution ──────────────────────────────────────────────
        private readonly PromptEvolutionManager? _promptEvolutionManager;
        private readonly Func<string, bool> _isPromptEvolutionEnabled;

        // ── Cross-Project Insights ──────────────────────────────────────────
        private readonly CrossProjectInsightsManager? _crossProjectInsightsManager;

        /// <summary>Exposes the streaming tool state dictionary for external consumers.</summary>
        public ConcurrentDictionary<string, StreamingToolState> StreamingToolState => _processLauncher.StreamingToolState;

        /// <summary>Exposes the early termination manager for monitoring/dashboard access.</summary>
        public EarlyTerminationManager EarlyTerminationManager => _earlyTerminationManager;

        /// <summary>Exposes the output tab manager for synthesis board access.</summary>
        public OutputTabManager OutputTabManager => _outputTabManager;

        /// <summary>Fires when a task's process exits (with the task ID). Used to resume dependency-queued tasks.</summary>
        public event Action<string>? TaskCompleted;

        /// <summary>Fires when a subtask is spawned (parent, child). MainWindow subscribes to wire the subtask into _activeTasks and create its output tab.</summary>
        public event Action<AgentTask, AgentTask>? SubTaskSpawned;


        /// <summary>Fires when 3 perspective agents are spawned for synthesis (parentTaskId, perspectiveTaskIds[3]).</summary>
        public event Action<string, string[]>? SynthesisPerspectivesSpawned;

        /// <summary>Fires when a perspective agent completes (parentTaskId, perspectiveIndex 0-2).</summary>
        public event Action<string, int>? SynthesisPerspectiveCompleted;

        /// <summary>Fires when plan synthesis completes (parentTaskId, synthesisResult).</summary>
        public event Action<string, string>? SynthesisComplete;

        /// <summary>Fires when teams mode finishes (parentTaskId, finalStatus). Used to reset synthesis board.</summary>
        public event Action<string, AgentTaskStatus>? TeamsModeFinished;

        public TaskExecutionManager(TaskExecutionServices services)
        {
            _scriptDir = services.ScriptDir;
            _fileLockManager = services.FileLockManager;
            _outputTabManager = services.OutputTabManager;
            _gitHelper = services.GitHelper;
            _completionAnalyzer = services.CompletionAnalyzer;
            _promptBuilder = services.PromptBuilder;
            _taskFactory = services.TaskFactory;
            _gitOperationGuard = services.GitOperationGuard;
            _getSystemPrompt = services.GetSystemPrompt;
            _getProjectDescription = services.GetProjectDescription;
            _getProjectRulesBlock = services.GetProjectRulesBlock;
            _isGameProject = services.IsGameProject;
            _getSkillsBlock = services.GetSkillsBlock ?? (() => "");
            _getOpusEffortLevel = services.GetOpusEffortLevel ?? (() => "high");
            _messageBusManager = services.MessageBusManager;
            _dispatcher = services.Dispatcher;
            _taskPreprocessor = services.TaskPreprocessor ?? new TaskPreprocessor(services.ClaudeService);

            var retryMinutesFunc = services.GetTokenLimitRetryMinutes ?? (() => 30);

            // Wire up sub-handlers
            _outputProcessor = new OutputProcessor(services.OutputTabManager, services.CompletionAnalyzer, services.GitHelper, services.GetAutoVerify, services.GetShowCodeChanges);
            _processLauncher = new TaskProcessLauncher(services.ScriptDir, services.FileLockManager, services.OutputTabManager, _outputProcessor, services.PromptBuilder, services.Dispatcher);
            _earlyTerminationManager = new EarlyTerminationManager(new ProgressAnalyzer());

            // Feature System (injectable for testability)
            _featureRegistryManager = services.FeatureRegistryManager ?? new FeatureRegistryManager();
            _featureContextResolver = services.FeatureContextResolver ?? new FeatureContextResolver(_featureRegistryManager, claudeService: services.ClaudeService);
            _featureUpdateAgent = services.FeatureUpdateAgent ?? new FeatureUpdateAgent(_featureRegistryManager, claudeService: services.ClaudeService);
            _hybridSearchManager = services.HybridSearchManager;
            _contextPrefetchPipeline = services.ContextPrefetchPipeline;
            _feedbackCollector = services.FeedbackCollector;
            _promptEvolutionManager = services.PromptEvolutionManager;
            _isPromptEvolutionEnabled = services.IsPromptEvolutionEnabled ?? (_ => false);
            _crossProjectInsightsManager = services.CrossProjectInsightsManager;
            _featureModeHandler = new TeamsModeHandler(services.ScriptDir, _processLauncher, _outputProcessor, services.MessageBusManager, services.OutputTabManager, services.CompletionAnalyzer, services.PromptBuilder, services.TaskFactory, retryMinutesFunc, earlyTerminationManager: _earlyTerminationManager);
            if (services.ClaudeService != null)
                _featureModeHandler.SetPlanSynthesizer(new PlanSynthesizer(services.ClaudeService));
            _tokenLimitHandler = new TokenLimitHandler(services.ScriptDir, _processLauncher, _outputProcessor, services.FileLockManager, services.MessageBusManager, services.OutputTabManager, services.CompletionAnalyzer, retryMinutesFunc);

            // Set callback to process queued messages when task becomes ready
            _processLauncher.ProcessQueuedMessagesCallback = ProcessQueuedMessages;


            // Forward token-limit handler's TaskCompleted to the coordinator's event
            _tokenLimitHandler.TaskCompleted += id => TaskCompleted?.Invoke(id);

            // Wire up teams mode handler events for team/step extraction and child spawning
            _featureModeHandler.ExtractTeamRequested += (parent, output) => ExtractAndSpawnTeamForFeatureMode(parent, output);
            _featureModeHandler.TeamsModeChildSpawned += (parent, child) => SubTaskSpawned?.Invoke(parent, child);
            _featureModeHandler.SynthesisPerspectivesSpawned += (parentId, ids) => SynthesisPerspectivesSpawned?.Invoke(parentId, ids);
            _featureModeHandler.SynthesisPerspectiveCompleted += (parentId, idx) => SynthesisPerspectiveCompleted?.Invoke(parentId, idx);
            _featureModeHandler.SynthesisComplete += (parentId, result) => SynthesisComplete?.Invoke(parentId, result);
            _featureModeHandler.TeamsModeFinished += (taskId, status) => TeamsModeFinished?.Invoke(taskId, status);
        }

        // ── Prompt preparation ────────────────────────────────────────

        /// <summary>
        /// Builds the full prompt for a task and writes it to disk.
        /// When <paramref name="activeTasks"/> is provided, auto-enables the message bus
        /// if sibling tasks exist on the same project.
        /// </summary>
        private string BuildAndWritePromptFile(AgentTask task, ObservableCollection<AgentTask>? activeTasks = null, string featureContextBlock = "", string pendingChangesBlock = "")
        {
            // Check for active prompt evolution variant (A/B test assignment)
            PromptVariant? evolutionVariant = null;
            if (_promptEvolutionManager != null && _isPromptEvolutionEnabled(task.ProjectPath) && !task.IsSubTask)
                evolutionVariant = _promptEvolutionManager.GetActiveVariant(task.ProjectPath, task.Id);

            // Inject cross-project insights (globally-successful patterns from other projects)
            var crossProjectHints = _crossProjectInsightsManager?.GetCrossProjectHints() ?? "";

            var fullPrompt = _promptBuilder.BuildFullPrompt(
                _getSystemPrompt(), task,
                _getProjectDescription(task),
                _getProjectRulesBlock(task.ProjectPath),
                _isGameProject(task.ProjectPath),
                _getSkillsBlock(),
                featureContextBlock,
                pendingChangesBlock,
                evolutionVariant,
                crossProjectHints);

            if (activeTasks != null)
            {
                // Auto-enable message bus when other active tasks exist on the same project
                if (!task.UseMessageBus && activeTasks.Any(t => t.Id != task.Id && !t.IsFinished && t.ProjectPath == task.ProjectPath))
                    task.UseMessageBus = true;

                if (task.UseMessageBus)
                {
                    _messageBusManager.JoinBus(task.ProjectPath, task.Id, task.Summary ?? task.Description);
                    var siblings = _messageBusManager.GetParticipants(task.ProjectPath)
                        .Where(p => p.TaskId != task.Id)
                        .Select(p => (p.TaskId, p.Summary))
                        .ToList();
                    fullPrompt = _promptBuilder.BuildMessageBusBlock(task.Id, task.ProjectPath, siblings) + fullPrompt;
                }
            }

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);
            return promptFile;
        }

        // ── Process Lifecycle (delegated) ────────────────────────────

        public async System.Threading.Tasks.Task StartProcess(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            task.GitStartHash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath, task.Cts.Token);

            // Run preprocessor and feature resolver in parallel to reduce startup latency.
            // Both are independent Haiku CLI calls; feature resolver uses the original description
            // (before enhancement) since that better represents the user's intent for matching.
            var originalDescription = task.Description;
            var needsPreprocess = task.UseAutoMode && !task.HasHeader && !task.IsSubTask && !IsKnownTaskType(task.Summary);
            var needsFeatures = !task.IsSubTask && !IsKnownTaskType(task.Summary)
                && (task.IsTeamsMode || task.AllowTeamsModeInference)
                && _featureRegistryManager.RegistryExists(task.ProjectPath);

            if (needsPreprocess || needsFeatures)
            {
                _outputProcessor.AppendColoredOutput(task.Id,
                    $"[Startup] Preparing task{(needsFeatures ? " (features)" : "")}...\n",
                    Brushes.DarkGray, activeTasks, historyTasks);
            }

            System.Threading.Tasks.Task<PreprocessResult?> preprocessTask = needsPreprocess
                ? _taskPreprocessor.PreprocessAsync(task.Description, task.Cts.Token)
                : System.Threading.Tasks.Task.FromResult<PreprocessResult?>(null);

            // Check prefetch cache first to skip the feature resolution round-trip
            System.Threading.Tasks.Task<FeatureContextResult?> featureTask;
            var prefetchedResult = needsFeatures ? _contextPrefetchPipeline?.TryGetCached(originalDescription) : null;
            if (prefetchedResult != null)
            {
                featureTask = System.Threading.Tasks.Task.FromResult<FeatureContextResult?>(prefetchedResult);
                _outputProcessor.AppendColoredOutput(task.Id,
                    "[Features] Using pre-fetched context (cache hit)\n",
                    Brushes.DarkGray, activeTasks, historyTasks);
            }
            else if (needsFeatures && _hybridSearchManager != null)
            {
                featureTask = ResolveWithHybridSearchAsync(task.ProjectPath, originalDescription, task.Cts.Token);
            }
            else if (needsFeatures)
            {
                featureTask = _featureContextResolver.ResolveAsync(task.ProjectPath, originalDescription, ct: task.Cts.Token);
            }
            else
            {
                featureTask = System.Threading.Tasks.Task.FromResult<FeatureContextResult?>(null);
            }

            // Await both in parallel
            PreprocessResult? preprocessResult = null;
            FeatureContextResult? featureResult = null;
            try
            {
                await System.Threading.Tasks.Task.WhenAll(preprocessTask, featureTask);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Individual results checked below
            }

            // Apply preprocessor result
            if (needsPreprocess)
            {
                try
                {
                    preprocessResult = await preprocessTask;
                    if (preprocessResult != null)
                    {
                        task.Runtime.PreprocessorPrompt = preprocessResult.SentPrompt;
                        TaskPreprocessor.ApplyToTask(task, preprocessResult);
                        _outputTabManager.UpdateTabHeader(task);
                        if (string.IsNullOrEmpty(task.OriginalTeamsDescription))
                            task.OriginalTeamsDescription = originalDescription;
                        _outputProcessor.AppendColoredOutput(task.Id,
                            $"[Preprocessor] OK — \"{preprocessResult.Header}\"\n",
                            Brushes.MediumPurple, activeTasks, historyTasks);
                        if (preprocessResult.EnhancedPrompt != originalDescription)
                        {
                            _outputProcessor.AppendColoredOutput(task.Id,
                                $"[Preprocessor] Enhanced prompt:\n{preprocessResult.EnhancedPrompt}\n",
                                Brushes.MediumPurple, activeTasks, historyTasks);
                        }
                        else
                        {
                            _outputProcessor.AppendColoredOutput(task.Id,
                                "[Preprocessor] Prompt unchanged\n",
                                Brushes.DarkGray, activeTasks, historyTasks);
                        }
                        AppLogger.Info("TaskExecution", $"[Task {task.Id}] {TaskPreprocessor.FormatActiveToggles(task).TrimEnd()}");
                    }
                    else
                    {
                        _outputProcessor.AppendColoredOutput(task.Id,
                            "[Preprocessor] Failed — using defaults\n",
                            Brushes.Orange, activeTasks, historyTasks);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Debug("TaskExecutionManager", "Haiku pre-processing failed, continuing with defaults", ex);
                    _outputProcessor.AppendColoredOutput(task.Id,
                        $"[Preprocessor] Skipped — {ex.GetType().Name}: {ex.Message}\n",
                        Brushes.DarkGray, activeTasks, historyTasks);
                }
            }

            if (task.IsTeamsMode)
                _taskFactory.PrepareTaskForFeatureModeStart(task);

            // Apply feature resolver result
            var featureContextBlock = "";
            if (needsFeatures)
            {
                try
                {
                    featureResult = await featureTask;
                    if (featureResult != null)
                    {
                        // Store resolver suggestion for deferred new-feature creation at task completion
                        if (featureResult.IsNewFeature)
                            task.Runtime.ResolverSuggestion = featureResult;

                        if (!string.IsNullOrWhiteSpace(featureResult.ContextBlock))
                        {
                            featureContextBlock = featureResult.ContextBlock;
                            task.Runtime.FeatureContextBlock = featureContextBlock;
                            var featureCount = featureResult.RelevantFeatures.Count;
                            _outputProcessor.AppendColoredOutput(task.Id,
                                $"[Features] Matched {featureCount} feature{(featureCount != 1 ? "s" : "")} for context injection\n",
                                Brushes.MediumAquamarine, activeTasks, historyTasks);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Debug("TaskExecutionManager", "Feature context resolution failed, continuing without", ex);
                }
            }
            // Subtasks inherit feature context from their parent (set during spawn)
            else if (task.IsSubTask && !string.IsNullOrWhiteSpace(task.Runtime.FeatureContextBlock))
            {
                featureContextBlock = task.Runtime.FeatureContextBlock;
            }

            // Fetch pending diff to give the AI awareness of uncommitted changes
            var pendingChangesBlock = "";
            try
            {
                var rawDiff = await _gitHelper.GetPendingDiffAsync(task.ProjectPath, task.Cts.Token)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(rawDiff))
                {
                    const int maxDiffTokens = 2000;
                    var truncationService = new SmartTruncationService();
                    var truncated = truncationService.TruncateWithSemantics(
                        rawDiff, maxDiffTokens, SmartTruncationService.TruncationPriority.Balanced);
                    pendingChangesBlock = $"# PENDING CHANGES\nUncommitted changes in the repository:\n```diff\n{truncated.Content}\n```";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("TaskExecutionManager", "Pending diff fetch failed, continuing without", ex);
            }

            var promptFile = BuildAndWritePromptFile(task, activeTasks, featureContextBlock, pendingChangesBlock);
            var projectPath = task.ProjectPath;

            var cliModel = PromptBuilder.GetCliModelForTask(task);
            task.Runtime.LastCliModel = cliModel;
            var effortLevel = cliModel == PromptBuilder.CliOpusModel ? _getOpusEffortLevel() : null;
            var claudeCmd = _promptBuilder.BuildClaudeCommand(task.SkipPermissions, cliModel, task.PlanOnly, effortLevel);

            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildPowerShellScript(projectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            // Build compact startup line for visible output
            var friendlyModel = PromptBuilder.GetFriendlyModelName(cliModel);
            var promptPreview = task.ShortDescription;
            if (string.IsNullOrWhiteSpace(promptPreview))
                promptPreview = "No prompt";
            var flags = new List<string>();
            if (task.ExtendedPlanning) flags.Add("Planning");
            if (task.AutoDecompose) flags.Add("Auto-decompose");
            if (task.SpawnTeam) flags.Add("Team");
            if (task.IsTeamsMode) flags.Add($"Feature(max {task.MaxIterations})");
            var flagsSuffix = flags.Count > 0 ? $" | {string.Join(" | ", flags)}" : "";
            _outputProcessor.AppendOutput(task.Id,
                $"Task #{task.TaskNumber}: {promptPreview}\nModel: {friendlyModel}{flagsSuffix}\n\n",
                activeTasks, historyTasks);

            // Store full boot details + prompt in OutputBuilder only (not displayed)
            var bootLog = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(task.Summary))
                bootLog.AppendLine($"Summary: {task.Summary}");
            bootLog.AppendLine($"Project: {projectPath}");
            bootLog.AppendLine($"Model: {PromptBuilder.GetFriendlyModelName(cliModel)} ({cliModel})");
            bootLog.AppendLine($"Skip permissions: {task.SkipPermissions}");
            try
            {
                var promptContent = File.ReadAllText(promptFile, Encoding.UTF8);
                bootLog.AppendLine($"── Full Prompt ──────────────────────────────");
                bootLog.AppendLine(promptContent);
                bootLog.AppendLine($"── End Prompt ───────────────────────────────");
            }
            catch { /* non-critical */ }

            // Append pipeline diagnostics: preprocessor prompt and feature context block
            if (!string.IsNullOrWhiteSpace(task.Runtime.PreprocessorPrompt))
            {
                bootLog.AppendLine();
                bootLog.AppendLine($"── Preprocessor Prompt (sent to Haiku) ──────");
                bootLog.AppendLine(task.Runtime.PreprocessorPrompt);
                bootLog.AppendLine($"── End Preprocessor Prompt ──────────────────");
            }
            if (!string.IsNullOrWhiteSpace(task.Runtime.FeatureContextBlock))
            {
                bootLog.AppendLine();
                bootLog.AppendLine($"── Feature Context Block (injected) ────────");
                bootLog.AppendLine(task.Runtime.FeatureContextBlock);
                bootLog.AppendLine($"── End Feature Context Block ────────────────");
            }

            task.OutputBuilder.Append(bootLog);

            // Transition to Running now that prompt is written and process is launching.
            // Planning tasks stay in Planning (they run the plan phase, not full execution).
            if (task.Status is AgentTaskStatus.Stored or AgentTaskStatus.InitQueued or AgentTaskStatus.Queued)
                task.Status = AgentTaskStatus.Running;

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);

                // Auto-decompose: decomposition phase complete — spawn subtasks
                if (task.AutoDecompose && task.Status == AgentTaskStatus.Running)
                {
                    HandleDecompositionCompletion(task, activeTasks, historyTasks, moveToHistory);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Feature mode: multi-phase orchestration — always handle before SpawnTeam
                if (task.IsTeamsMode && task.Status == AgentTaskStatus.Running)
                {
                    _featureModeHandler.HandleTeamsModeIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Spawn team: team decomposition phase complete — spawn team members
                if (task.SpawnTeam && task.Status == AgentTaskStatus.Running)
                {
                    HandleTeamCompletion(task, activeTasks, historyTasks, moveToHistory);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Soft-stopped task: treat as completed regardless of exit code
                if (task.Status == AgentTaskStatus.SoftStop)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime = DateTime.Now;
                    _ = CompleteWithVerificationAsync(task, 0, activeTasks, historyTasks);
                    moveToHistory(task);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Already queued or cancelled — skip normal completion
                if (task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Cancelled)
                {
                    // For queued tasks, ensure process reference is cleaned up
                    // This happens when a process is killed due to file lock conflict
                    if (task.Status == AgentTaskStatus.Queued)
                    {
                        task.Process = null;
                        AppLogger.Info("TaskExecutionManager", $"Cleaned up process reference for queued task #{task.TaskNumber}");
                    }

                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                if (exitCode != 0 && task.Status == AgentTaskStatus.Running && _tokenLimitHandler.HandleTokenLimitRetry(task, activeTasks, historyTasks, moveToHistory))
                {
                    // Token limit detected on non-feature-mode task — retry scheduled, don't complete yet
                    return;
                }
                else
                {
                    // Check if this is a teams mode child task (e.g., architect, team member)
                    var isTeamsModeChild = false;
                    if (!string.IsNullOrEmpty(task.ParentTaskId))
                    {
                        var parent = activeTasks.FirstOrDefault(t => t.Id == task.ParentTaskId)
                                    ?? historyTasks.FirstOrDefault(t => t.Id == task.ParentTaskId);
                        if (parent != null && parent.IsTeamsMode)
                        {
                            isTeamsModeChild = true;
                        }
                    }

                    if (isTeamsModeChild)
                    {
                        // Feature mode child tasks complete directly without verification step
                        var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                        task.Status = expectedStatus;
                        task.EndTime = DateTime.Now;

                        // Extract basic summary from output for parent to collect
                        try
                        {
                            var outputText = task.OutputBuilder.ToString();
                            var summary = _completionAnalyzer.ExtractRecommendations(outputText);
                            if (!string.IsNullOrWhiteSpace(summary))
                            {
                                task.CompletionSummary = summary;
                            }
                            else
                            {
                                // Fallback to simple status summary
                                task.CompletionSummary = $"Task completed with status: {expectedStatus}";
                            }

                            if (HasExplicitRecommendationStatus(outputText))
                            {
                                var recommendations = _completionAnalyzer.ExtractRecommendations(outputText);
                                if (!string.IsNullOrWhiteSpace(recommendations))
                                {
                                    task.Recommendations = recommendations;
                                }
                                else if (HasNeedsMoreWorkStatus(outputText))
                                {
                                    task.Recommendations = "Continue working on the incomplete task.";
                                }
                                else
                                {
                                    // COMPLETE WITH RECOMMENDATIONS without extractable headers
                                    task.Recommendations = "Continue with the recommended next steps from this task.";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("TaskExecution", $"Failed to extract summary for teams mode child task {task.Id}", ex);
                            task.CompletionSummary = $"Task completed with status: {expectedStatus}";
                        }

                        // Release locks and cleanup
                        _fileLockManager.ReleaseTaskLocks(task.Id);
                        if (task.UseMessageBus)
                            _messageBusManager.LeaveBus(task.ProjectPath, task.Id);

                        // Log completion
                        try
                        {
                            var statusColor = exitCode == 0
                                ? (Brush)Application.Current.FindResource("Success")
                                : (Brush)Application.Current.FindResource("DangerBright");
                            _outputProcessor.AppendColoredOutput(task.Id,
                                $"\nFeature mode child task completed (exit code: {exitCode}).\n",
                                statusColor, activeTasks, historyTasks);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("TaskExecution", $"Failed to append completion output for teams mode child task {task.Id}", ex);
                        }

                        _outputTabManager.UpdateTabHeader(task);
                        _fileLockManager.CheckQueuedTasks(activeTasks);
                        TaskCompleted?.Invoke(task.Id);

                        // Check if all teams mode children are complete to trigger next phase
                        if (!string.IsNullOrEmpty(task.ParentTaskId))
                        {
                            var parent = activeTasks.FirstOrDefault(t => t.Id == task.ParentTaskId);
                            if (parent != null)
                            {
                                _featureModeHandler.NotifyChildCompleted(parent.Id, task.Id);
                                CheckTeamsModePhaseCompletion(parent, activeTasks, historyTasks, moveToHistory);
                            }
                        }
                        return;
                    }

                    // Regular tasks go through verification
                    // File locks are held until FinalizeTask handles the Committing → Completed flow
                    // Set to Verifying while summary runs; final status is set afterwards
                    task.Status = AgentTaskStatus.Verifying;
                    _outputTabManager.UpdateTabHeader(task);
                    _ = CompleteWithVerificationAsync(task, exitCode, activeTasks, historyTasks);
                    return;
                }
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);

                if (task.IsTeamsMode)
                {
                    var iterationTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(TeamsModeHandler.TeamsModeIterationTimeoutMinutes)
                    };
                    task.TeamsModeIterationTimer = iterationTimer;
                    iterationTimer.Tick += (_, _) =>
                    {
                        iterationTimer.Stop();
                        task.TeamsModeIterationTimer = null;
                        if (task.Process is { HasExited: false })
                        {
                            _outputProcessor.AppendOutput(task.Id, $"\n[Teams Mode] Iteration timeout ({TeamsModeHandler.TeamsModeIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                            try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck process for task {task.Id}", ex); }
                        }
                    };
                    iterationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        /// <summary>
        /// Runs the completion summary, then sets the final task status.
        /// Wrapped in try-finally to guarantee the task always transitions out of
        /// Verifying and fires TaskCompleted, even if summary/verification throws.
        /// </summary>
        private async System.Threading.Tasks.Task CompleteWithVerificationAsync(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;

            // Only release locks for failed tasks here.
            // For successful tasks, FinalizeTask handles the Committing → Completed flow
            // which ensures file locks are held until after git commit succeeds.
            if (exitCode != 0)
            {
                _fileLockManager.ReleaseTaskLocks(task.Id);
            }

            // Always handle message bus cleanup
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);

            try
            {
                // Generate summary (awaited, not fire-and-forget)
                await _outputProcessor.AppendCompletionSummary(task, activeTasks, historyTasks, expectedStatus);

                await _outputProcessor.TryInjectSubtaskResultAsync(task, activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Error("TaskExecution", $"CompleteWithVerificationAsync failed for task {task.Id}", ex);
            }

            // Prompt Evolution: record task outcome for A/B testing
            if (_promptEvolutionManager != null && _isPromptEvolutionEnabled(task.ProjectPath) && !task.IsSubTask)
            {
                var success = exitCode == 0 &&
                    (expectedStatus == AgentTaskStatus.Completed || task.Status == AgentTaskStatus.Completed);
                _promptEvolutionManager.RecordOutcome(task.Id, task.ProjectPath, success);
            }

            // Feature System: update registry with task results (fire-and-forget, never blocks teardown)
            // Use file locks directly — task.ChangedFiles isn't populated until FinalizeTask runs later
            if (exitCode == 0)
            {
                var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                var changedFilesList = lockedFiles.Count > 0
                    ? lockedFiles.Select(f =>
                    {
                        var projectRoot = task.ProjectPath.Replace('\\', '/').TrimEnd('/') + "/";
                        var normalized = f.Replace('\\', '/');
                        return normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                            ? normalized[projectRoot.Length..]
                            : normalized;
                    }).ToList()
                    : task.ChangedFiles?.ToList() ?? new List<string>();

                _ = _featureUpdateAgent.UpdateFeaturesAsync(
                    task.ProjectPath, task.Id, task.Description,
                    task.CompletionSummary,
                    changedFilesList,
                    resolverSuggestion: task.Runtime.ResolverSuggestion);

                // Hybrid search: index task completion and re-embed changed files
                if (_hybridSearchManager != null && _hybridSearchManager.IsAvailable)
                {
                    var featureIds = task.Runtime.ResolverSuggestion?.RelevantFeatures?
                        .Select(f => f.FeatureId).ToList() ?? new List<string>();
                    _ = _hybridSearchManager.IndexTaskCompletionAsync(
                        task.Id, task.Description, task.CompletionSummary, featureIds, "success");
                    _ = _hybridSearchManager.UpdateChangedFileEmbeddingsAsync(
                        task.ProjectPath, changedFilesList);
                }
            }

            // Feedback System: collect execution feedback (fire-and-forget, never blocks teardown)
            if (_feedbackCollector != null)
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { _feedbackCollector.CollectAndAnalyze(task); }
                    catch (Exception ex) { AppLogger.Debug("TaskExecution", "Feedback collection failed (non-critical)", ex); }
                });
            }

            // If a follow-up was started during summary generation, the status will
            // have changed from Verifying to Running — don't overwrite it.
            if (task.Status == AgentTaskStatus.Verifying)
            {
                // Now set the final status after summary is complete
                // If the task completed successfully and has recommendations, use the Recommendation status
                var finalStatus = expectedStatus;
                if (finalStatus == AgentTaskStatus.Completed && task.HasRecommendations)
                    finalStatus = AgentTaskStatus.Recommendation;
                task.Status = finalStatus;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);

                // Process any messages that were queued while the task was busy.
                // The process has exited so message_stop won't fire — this is the
                // last chance to deliver queued follow-ups before the task leaves Running.
                if (task.Runtime.PendingMessageCount > 0)
                {
                    AppLogger.Info("TaskExecution", $"[{task.Id}] Processing {task.Runtime.PendingMessageCount} queued message(s) after completion");
                    ProcessQueuedMessages(task, activeTasks, historyTasks);
                }
            }

            // Always check queued tasks and fire TaskCompleted event,
            // even if status changed during verification
            _fileLockManager.CheckQueuedTasks(activeTasks);
            TaskCompleted?.Invoke(task.Id);
        }

        /// <summary>
        /// Runs the completion summary after a follow-up completes, then sets the final status.
        /// Fires TaskCompleted so teams mode parents and the orchestrator are notified.
        /// </summary>
        private async System.Threading.Tasks.Task CompleteFollowUpWithVerificationAsync(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            // If the task was already cancelled/finalized while the async completion was pending,
            // skip all verification to avoid recreating closed tabs or overwriting status.
            if (task.IsFinished)
            {
                AppLogger.Info("FollowUp", $"[{task.Id}] Task already finalized (status={task.Status}), skipping follow-up verification");
                _fileLockManager.CheckQueuedTasks(activeTasks);
                TaskCompleted?.Invoke(task.Id);
                return;
            }

            var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;

            // File locks are NOT released here — they are held so that
            // OnTaskProcessCompleted → FinalizeTask can use them for auto-commit.
            // If auto-commit is disabled, OnTaskProcessCompleted releases locks directly.

            // Always handle message bus cleanup
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);

            try
            {
                await _outputProcessor.AppendCompletionSummary(task, activeTasks, historyTasks, expectedStatus);
                await _outputProcessor.TryInjectSubtaskResultAsync(task, activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Error("TaskExecution", $"CompleteFollowUpWithVerificationAsync failed for task {task.Id}", ex);
            }

            // If a follow-up was started during summary generation, the status will
            // have changed from Verifying to Running — don't overwrite it.
            if (task.Status == AgentTaskStatus.Verifying)
            {
                // Follow-ups complete as Completed/Failed — don't re-enter Recommendation
                // status, which would create an infinite continue loop.
                task.Recommendations = "";
                task.Status = expectedStatus;
                task.EndTime = DateTime.Now;
                _outputProcessor.AppendOutput(task.Id, "\nFollow-up complete.\n", activeTasks, historyTasks);
                _outputTabManager.UpdateTabHeader(task);

                // Process any messages that were queued while the follow-up was busy.
                // The process has exited so message_stop won't fire — deliver them now.
                if (task.Runtime.PendingMessageCount > 0)
                {
                    AppLogger.Info("FollowUp", $"[{task.Id}] Processing {task.Runtime.PendingMessageCount} queued message(s) after follow-up completion");
                    ProcessQueuedMessages(task, activeTasks, historyTasks);
                }
            }

            // Fire TaskCompleted so OnTaskProcessCompleted can handle auto-commit
            // and queued task resumption. Don't call CheckQueuedTasks here — locks
            // may still be needed for auto-commit; downstream handlers release them.
            TaskCompleted?.Invoke(task.Id);
        }


        public void LaunchHeadless(AgentTask task)
        {
            var promptFile = BuildAndWritePromptFile(task);
            var projectPath = task.ProjectPath;
            var cliModel = PromptBuilder.GetCliModelForTask(task);
            task.Runtime.LastCliModel = cliModel;
            var effortLevel = cliModel == PromptBuilder.CliOpusModel ? _getOpusEffortLevel() : null;

            var ps1File = Path.Combine(_scriptDir, $"headless_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildHeadlessPowerShellScript(projectPath, promptFile, task.SkipPermissions, cliModel, effortLevel),
                Encoding.UTF8);

            var psi = _promptBuilder.BuildProcessStartInfo(ps1File, headless: true);
            psi.WorkingDirectory = projectPath;
            try
            {
                var proc = Process.Start(psi);
                if (proc != null)
                    JobObjectManager.Instance.AssignProcess(proc);
            }
            catch (Exception ex)
            {
                Dialogs.DarkDialog.ShowConfirm($"Failed to launch terminal:\n{ex.Message}", "Launch Error");
            }
        }

        // ── Input / Follow-up ────────────────────────────────────────

        /// <summary>
        /// Processes any messages that were queued while the task was busy.
        /// Should be called when the task becomes ready to accept new input.
        /// </summary>
        public void ProcessQueuedMessages(AgentTask task, ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            if (task.Runtime.PendingMessageCount == 0) return;

            var nextMessage = task.Runtime.DequeueMessage();
            if (nextMessage != null)
            {
                AppLogger.Info("FollowUp", $"[{task.Id}] Processing queued message. Remaining: {task.Runtime.PendingMessageCount}");

                // Send the dequeued message through the regular SendFollowUp flow
                // This will handle checking if the task is still busy and re-queue if needed
                SendFollowUp(task, nextMessage, activeTasks, historyTasks);
            }
        }

        public void SendInput(AgentTask task, TextBox inputBox,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var text = inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputBox.Clear();
            SendFollowUp(task, text, activeTasks, historyTasks);
        }

        public void SendFollowUp(AgentTask task, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            bool isInterrupt = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            AppLogger.Info("FollowUp", $"[{task.Id}] SendFollowUp called. Status={task.Status}, IsTeamsMode={task.IsTeamsMode}, Phase={task.TeamsModePhase}, ProcessAlive={task.Process is { HasExited: false }}, ConversationId={task.ConversationId ?? "(null)"}");
            AppLogger.Info("FollowUp", $"[{task.Id}] Task details: ActiveTasksCount={activeTasks.Count}, HistoryTasksCount={historyTasks.Count}");

            // Ensure task is in active tasks (not history)
            var isInActive = activeTasks.Contains(task);
            var isInHistory = historyTasks.Contains(task);
            AppLogger.Info("FollowUp", $"[{task.Id}] Task location: InActive={isInActive}, InHistory={isInHistory}");

            // Block follow-up when the task is a teams mode coordinator waiting for subtasks.
            // Without this, it would start a new Claude process that has no context about the
            // coordination and asks "what do you want me to do?"
            if (task.IsTeamsMode && task.TeamsModePhase is TeamsModePhase.TeamPlanning or TeamsModePhase.Execution)
            {
                AppLogger.Warn("FollowUp", $"[{task.Id}] Blocked: teams mode coordinator in phase {task.TeamsModePhase}");
                _outputProcessor.AppendOutput(task.Id,
                    "\nThis task is coordinating subtasks and waiting for them to complete. Follow-up input is not available during this phase.\n",
                    activeTasks, historyTasks);
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning && task.Process is { HasExited: false })
            {
                try
                {
                    // Check if the task is currently processing a message or busy with tool execution
                    if (task.Runtime.IsProcessingMessage || task.HasToolActivity)
                    {
                        // If this is an interrupt message and interrupts are allowed, inject immediately
                        if (isInterrupt && task.Runtime.AllowInterrupts)
                        {
                            AppLogger.Info("FollowUp", $"[{task.Id}] Injecting interrupt message mid-task");
                            _outputProcessor.AppendOutput(task.Id,
                                $"\n[INTERRUPT] Modifying current prompt:\n> {text}\n",
                                activeTasks, historyTasks);

                            // Format as an interrupt message that Claude can recognize
                            var interruptMessage = $"\n\n[SYSTEM INTERRUPT]\nThe user has provided additional context that modifies the current task:\n\nHuman: {text}\n\nPlease acknowledge this modification and adjust your approach accordingly.\n\nAssistant:";

                            try
                            {
                                task.Process.StandardInput.WriteLine(interruptMessage);
                                task.Process.StandardInput.Flush();
                                AppLogger.Info("FollowUp", $"[{task.Id}] Interrupt message sent successfully");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Error("FollowUp", $"[{task.Id}] Failed to send interrupt message", ex);
                                // Fall back to queuing
                                task.Runtime.EnqueueMessage(text);
                                _outputProcessor.AppendOutput(task.Id,
                                    $"\n[Interrupt failed - message queued instead]\n",
                                    activeTasks, historyTasks);
                            }
                            return;
                        }

                        // Otherwise queue the message for later delivery
                        task.Runtime.EnqueueMessage(text);
                        AppLogger.Info("FollowUp", $"[{task.Id}] Task is busy, queuing message. Queue size: {task.Runtime.PendingMessageCount}");
                        _outputProcessor.AppendOutput(task.Id,
                            $"\n[Message queued - will be sent when task is ready]\n> {text}\n",
                            activeTasks, historyTasks);
                        return;
                    }

                    AppLogger.Info("FollowUp", $"[{task.Id}] Writing to existing stdin (process alive)");
                    _outputProcessor.AppendOutput(task.Id, $"\n> {text}\n", activeTasks, historyTasks);

                    // Mark that we're processing a message
                    task.Runtime.IsProcessingMessage = true;

                    // Append follow-up prompt to task description for git commit tracking
                    if (!string.IsNullOrEmpty(task.Description))
                    {
                        task.Description += $" | Follow-up: {text}";
                    }
                    else
                    {
                        task.Description = text;
                    }
                    AppLogger.Info("FollowUp", $"[{task.Id}] Updated task description for commit tracking");

                    // Format the message as a proper user message in the conversation
                    // Claude CLI expects messages to be properly formatted to maintain conversation context
                    var formattedMessage = $"\n\nHuman: {text}\n\nAssistant:";

                    try
                    {
                        task.Process.StandardInput.WriteLine(formattedMessage);
                        task.Process.StandardInput.Flush();

                        // Store the message in conversation history for proper context tracking
                        AppLogger.Info("FollowUp", $"[{task.Id}] Sent formatted user message to Claude process");
                    }
                    catch (Exception writeEx)
                    {
                        AppLogger.Error("FollowUp", $"[{task.Id}] Failed to write message to StandardInput", writeEx);

                        // Process any remaining queued messages to prevent deadlock
                        ProcessQueuedMessages(task, activeTasks, historyTasks);
                    }
                    finally
                    {
                        // Always reset the processing flag to prevent queue deadlock
                        task.Runtime.IsProcessingMessage = false;
                    }

                    return;
                }
                catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to write to stdin for task {task.Id}, starting follow-up", ex); }
            }

            task.Status = AgentTaskStatus.Running;
            task.EndTime = null;
            task.Recommendations = "";
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            // Reset auto-commit state so follow-up changes get their own commit
            task.IsCommitted = false;
            task.CommitError = null;
            task.CommitHash = null;

            // Capture new git baseline for the follow-up so auto-commit can diff against it.
            // Fire-and-forget is safe: git rev-parse completes in milliseconds, long before
            // the follow-up process finishes and the exit handler needs the hash.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                task.GitStartHash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath, task.Cts.Token);
                AppLogger.Info("FollowUp", $"[{task.Id}] Captured new GitStartHash for follow-up: {task.GitStartHash ?? "(null)"}");
            });

            _outputTabManager.UpdateTabHeader(task);

            // Process any queued messages before starting the follow-up
            ProcessQueuedMessages(task, activeTasks, historyTasks);

            // Use --resume with session ID when available.
            // Never fall back to --continue — it resumes the most recent session in the
            // project directory, which may belong to a completely different task.
            var hasSessionId = !string.IsNullOrEmpty(task.ConversationId);
            var resumeFlag = hasSessionId
                ? $" --resume {task.ConversationId}"
                : "";
            if (!hasSessionId)
            {
                AppLogger.Warn("FollowUp", $"[{task.Id}] No ConversationId available — starting fresh session instead of --continue to avoid cross-task context bleed");
            }

            var followUpModel = PromptBuilder.GetCliModelForTask(task);
            if (task.Runtime.LastCliModel != null && task.Runtime.LastCliModel != followUpModel)
            {
                _outputProcessor.AppendOutput(task.Id,
                    $"\nSwitching Model to: {PromptBuilder.GetFriendlyModelName(followUpModel)} ({followUpModel})\n",
                    activeTasks, historyTasks);
            }
            task.Runtime.LastCliModel = followUpModel;
            _outputProcessor.AppendOutput(task.Id, $"\nUser Input > {text}\n\n", activeTasks, historyTasks);

            // Append follow-up prompt to task description for git commit tracking
            if (!string.IsNullOrEmpty(task.Description))
            {
                task.Description += $" | Follow-up: {text}";
            }
            else
            {
                task.Description = text;
            }
            AppLogger.Info("FollowUp", $"[{task.Id}] Updated task description for commit tracking");

            // Set iteration start AFTER the echo so the echoed prompt text
            // (which contains recommendation keywords) is excluded from
            // recommendation extraction when the follow-up completes.
            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var modelFlag = $" --model {followUpModel}";
            var followUpFile = Path.Combine(_scriptDir, $"followup_{task.Id}_{DateTime.Now.Ticks}.txt");
            File.WriteAllText(followUpFile, text, Encoding.UTF8);
            AppLogger.Info("FollowUp", $"[{task.Id}] Wrote follow-up prompt to: {followUpFile}");

            var ps1File = Path.Combine(_scriptDir, $"followup_{task.Id}.ps1");
            var ps1Content =
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"Get-Content -Raw -LiteralPath '{followUpFile}' | claude -p{skipFlag}{resumeFlag}{modelFlag} --verbose --output-format stream-json\n";
            File.WriteAllText(ps1File, ps1Content, Encoding.UTF8);
            AppLogger.Info("FollowUp", $"[{task.Id}] Wrote PS1 script to: {ps1File}");
            AppLogger.Debug("FollowUp", $"[{task.Id}] PS1 content:\n{ps1Content}");

            AppLogger.Info("FollowUp", $"[{task.Id}] Creating managed process with ps1File={ps1File}");
            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                AppLogger.Info("FollowUp", $"[{task.Id}] Follow-up process exited with code {exitCode}");

                // If the task was already cancelled/finalized (e.g. user removed the task
                // while follow-up was running), don't overwrite its status or recreate tabs.
                if (task.IsFinished)
                {
                    AppLogger.Info("FollowUp", $"[{task.Id}] Task already finalized (status={task.Status}), skipping follow-up completion");
                    return;
                }

                task.Status = AgentTaskStatus.Verifying;
                _outputTabManager.UpdateTabHeader(task);
                _ = CompleteFollowUpWithVerificationAsync(task, exitCode, activeTasks, historyTasks);
            });
            AppLogger.Info("FollowUp", $"[{task.Id}] Process created, about to start");

            try
            {
                _processLauncher.StartManagedProcess(task, process);
                AppLogger.Info("FollowUp", $"[{task.Id}] Process started successfully. PID={task.Process?.Id}");
                // Process started - no user-facing output needed
            }
            catch (Exception ex)
            {
                AppLogger.Error("FollowUp", $"[{task.Id}] Failed to start follow-up process", ex);
                _outputProcessor.AppendOutput(task.Id, $"Follow-up error: {ex.Message}\n", activeTasks, historyTasks);
            }
        }

        // ── Cancellation ─────────────────────────────────────────────

        public void CancelTaskImmediate(AgentTask task)
        {
            if (task.IsFinished) return;

            // Resume suspended threads before killing so the process can exit cleanly
            if (task.Status == AgentTaskStatus.Paused && task.Process is { HasExited: false })
            {
                try { TaskProcessLauncher.ResumeProcessTree(task.Process); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to resume process tree before cancel for task {task.Id}", ex); }
            }

            // Cancel cooperative async operations before killing the process
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }

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
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            TaskProcessLauncher.KillProcess(task);
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _processLauncher.StreamingToolState.TryRemove(task.Id, out _);
            task.Cts?.Dispose();
            task.Cts = null;

            // Fire TaskCompleted so the orchestrator and teams mode parent get notified.
            // Without this, cancelling a subtask leaves the parent stuck waiting forever.
            TaskCompleted?.Invoke(task.Id);
        }

        /// <summary>Backward-compatible static KillProcess forwarding to TaskProcessLauncher.</summary>
        public static void KillProcess(AgentTask task) => TaskProcessLauncher.KillProcess(task);

        public void RemoveStreamingState(string taskId) => _processLauncher.RemoveStreamingState(taskId);

        // ── Result Verification (delegated) ─────────────────────────

        public System.Threading.Tasks.Task RunResultVerificationAsync(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
            => _outputProcessor.RunResultVerificationAsync(task, null, activeTasks, historyTasks);

        // ── Pause / Resume (delegated) ───────────────────────────────

        public void PauseTask(AgentTask task) => _processLauncher.PauseTask(task);

        public void SoftStopTask(AgentTask task) => _processLauncher.SoftStopTask(task);

        public void ResumeTask(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
            => _processLauncher.ResumeTask(task, activeTasks, historyTasks);

        // ── Subtask spawning ─────────────────────────────────────────

        /// <summary>
        /// Creates a new subtask from a parent task, inheriting project path, permissions, and model settings.
        /// Sets up the parent-child relationship and builds a DependencyContext from the parent's current output.
        /// </summary>
        public AgentTask SpawnSubtask(AgentTask parent, string description, bool inheritSettings = true)
        {
            var child = inheritSettings
                ? _taskFactory.CreateTask(
                    description,
                    parent.ProjectPath,
                    parent.SkipPermissions,
                    parent.Headless,
                    parent.IsTeamsMode,
                    parent.IgnoreFileLocks,
                    parent.UseMcp,
                    parent.SpawnTeam,
                    parent.ExtendedPlanning,
                    planOnly: false,
                    parent.UseMessageBus,
                    model: parent.Model)
                : _taskFactory.CreateTask(
                    description,
                    parent.ProjectPath,
                    skipPermissions: false,
                    headless: false,
                    isTeamsMode: false,
                    ignoreFileLocks: false,
                    useMcp: false);

            child.ParentTaskId = parent.Id;
            child.ProjectColor = parent.ProjectColor;
            child.ProjectDisplayName = parent.ProjectDisplayName;
            child.TimeoutMinutes = parent.TimeoutMinutes;

            parent.SubTaskCounter++;
            parent.ChildTaskIds.Add(child.Id);

            // Build structured dependency context from parent
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine($"# Context from parent task #{parent.TaskNumber}");
            contextBuilder.AppendLine($"Parent description: {parent.Description}");

            // Prefer CompletionSummary; fall back to truncated description
            if (!string.IsNullOrWhiteSpace(parent.CompletionSummary))
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine("## Parent summary:");
                contextBuilder.AppendLine(parent.CompletionSummary);
            }
            else if (parent.Description?.Length > 200)
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine("## Parent summary:");
                contextBuilder.AppendLine(parent.Description[..Math.Min(1_000, parent.Description.Length)]);
            }

            // Append only the tail of parent output, stripped of ANSI noise
            var parentOutput = parent.OutputBuilder.ToString();
            if (!string.IsNullOrWhiteSpace(parentOutput))
            {
                const int maxTailChars = 2_000;
                var tail = parentOutput.Length > maxTailChars
                    ? parentOutput[^maxTailChars..]
                    : parentOutput;
                tail = Helpers.FormatHelpers.StripAnsiCodes(tail);
                contextBuilder.AppendLine();
                contextBuilder.AppendLine("## Recent parent output (tail):");
                contextBuilder.AppendLine(tail);
            }

            child.DependencyContext = contextBuilder.ToString();

            // NOTE: Does NOT fire SubTaskSpawned — callers are responsible for firing the event
            // after any additional child configuration (e.g., property overrides, dependency wiring).
            return child;
        }

        /// <summary>
        /// Creates a subtask and sets it to Queued status, blocked by the parent task.
        /// The subtask will only start after the parent finishes.
        /// </summary>
        public AgentTask SpawnSubtaskAndQueue(AgentTask parent, string description)
        {
            var child = SpawnSubtask(parent, description);
            child.Status = AgentTaskStatus.Queued;
            child.QueuedReason = $"Waiting for parent task #{parent.TaskNumber}";
            child.BlockedByTaskId = parent.Id;
            child.BlockedByTaskNumber = parent.TaskNumber;
            child.DependencyTaskIds = new List<string> { parent.Id };
            child.DependencyTaskNumbers = new List<int> { parent.TaskNumber };
            SubTaskSpawned?.Invoke(parent, child);
            return child;
        }

        // ── Auto-decomposition ───────────────────────────────────────

        /// <summary>
        /// Parses the ```SUBTASKS``` JSON block from agent output, spawns child tasks,
        /// and wires up inter-subtask dependencies from the depends_on fields.
        /// Returns the list of spawned subtasks, or null if no valid block was found.
        /// </summary>
        public List<AgentTask>? ExtractAndSpawnSubtasks(AgentTask parent, string output)
        {
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "SUBTASKS");
            if (json == null)
            {
                AppLogger.Warn("TaskExecution", $"No ```SUBTASKS``` block found in decomposition output for task {parent.Id}");
                return null;
            }

            List<SubtaskEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<SubtaskEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskExecution", $"Failed to deserialize SUBTASKS JSON for task {parent.Id}", ex);
                return null;
            }

            if (entries == null || entries.Count == 0)
            {
                AppLogger.Warn("TaskExecution", $"Empty SUBTASKS array for task {parent.Id}");
                return null;
            }

            // Safety cap to prevent runaway spawning (dynamic scaling allowed up to 15)
            if (entries.Count > 15)
                entries = entries.GetRange(0, 15);

            // Spawn all subtasks
            var children = new List<AgentTask>();
            foreach (var entry in entries)
            {
                var child = SpawnSubtask(parent, entry.Description);
                child.AutoDecompose = false; // Subtasks don't auto-decompose
                children.Add(child);
            }

            // Wire up inter-subtask dependencies
            for (int i = 0; i < entries.Count; i++)
            {
                var deps = entries[i].DependsOn;
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
                    children[i].QueuedReason = $"Waiting for subtask(s): {string.Join(", ", depNumbers.Select(n => $"#{n}"))}";
                    children[i].BlockedByTaskId = depIds[0];
                    children[i].BlockedByTaskNumber = depNumbers[0];
                }
            }

            return children;
        }

        /// <summary>
        /// Handles completion of the decomposition phase: extracts subtasks from output,
        /// spawns them, and transitions the parent into coordinator mode.
        /// </summary>
        public void HandleDecompositionCompletion(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var output = task.OutputBuilder.ToString();
            var children = ExtractAndSpawnSubtasks(task, output);

            if (children == null || children.Count == 0)
            {
                _outputProcessor.AppendOutput(task.Id,
                    "\nDecomposition produced no valid subtasks — completing parent task.\n",
                    activeTasks, historyTasks);
                task.AutoDecompose = false;
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                return;
            }

            // Mark parent as coordinator
            task.AutoDecompose = false; // Decomposition phase done
            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            task.CompletionSummary = $"Decomposed into {children.Count} subtask(s)";
            _outputProcessor.AppendOutput(task.Id,
                $"\nTask decomposed into {children.Count} subtask(s). Parent is now a coordinator.\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Fire SubTaskSpawned for each child so the UI picks them up
            foreach (var child in children)
                SubTaskSpawned?.Invoke(task, child);

            // Refresh dependency display info now that TaskNumbers are assigned
            RefreshDependencyDisplayInfo(children, "subtask");
        }

        /// <summary>
        /// If the completing task has a ParentTaskId, finds the parent and injects the subtask result.
        /// Delegates to OutputProcessor.
        /// </summary>
        public void InjectSubtaskResult(AgentTask parent, AgentTask child)
            => _outputProcessor.InjectSubtaskResult(parent, child);

        // ── Backward-compatible forwarding for EvaluateFeatureModeIteration ─

        /// <summary>
        /// Pure decision function for teams mode iteration logic.
        /// Delegates to <see cref="TeamsModeHandler.EvaluateFeatureModeIteration"/>.
        /// </summary>
        internal static TeamsModeHandler.FeatureModeDecision EvaluateFeatureModeIteration(
            ICompletionAnalyzer completionAnalyzer,
            AgentTaskStatus currentStatus,
            TimeSpan totalRuntime,
            string iterationOutput,
            int currentIteration,
            int maxIterations,
            int exitCode,
            int consecutiveFailures,
            int outputLength)
            => TeamsModeHandler.EvaluateFeatureModeIteration(
                completionAnalyzer, currentStatus, totalRuntime, iterationOutput,
                currentIteration, maxIterations, exitCode,
                consecutiveFailures, outputLength);

        // ── Team spawning ────────────────────────────────────────────

        /// <summary>
        /// Parses the ```TEAM``` JSON block from agent output, spawns child tasks
        /// with roles and message bus enabled, and wires up inter-member dependencies.
        /// Returns the list of spawned team members, or null if no valid block was found.
        /// </summary>
        public List<AgentTask>? ExtractAndSpawnTeam(AgentTask parent, string output)
        {
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "TEAM");
            if (json == null)
            {
                AppLogger.Warn("TaskExecution", $"No ```TEAM``` block found in team decomposition output for task {parent.Id}");
                return null;
            }

            List<TeamMemberEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<TeamMemberEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskExecution", $"Failed to deserialize TEAM JSON for task {parent.Id}", ex);
                return null;
            }

            if (entries == null || entries.Count == 0)
            {
                AppLogger.Warn("TaskExecution", $"Empty TEAM array for task {parent.Id}");
                return null;
            }

            // Cap at 5 team members
            if (entries.Count > 5)
                entries = entries.GetRange(0, 5);

            // Spawn all team members with message bus enabled
            var children = new List<AgentTask>();
            foreach (var entry in entries)
            {
                var child = SpawnSubtask(parent, entry.Description);
                child.SpawnTeam = false;
                child.AutoDecompose = false;
                child.UseMessageBus = true;
                child.Summary = $"[{entry.Role}] {_taskFactory.GenerateLocalSummary(entry.Description)}";
                children.Add(child);
            }

            // Wire up inter-member dependencies
            for (int i = 0; i < entries.Count; i++)
            {
                var deps = entries[i].DependsOn;
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
                    children[i].QueuedReason = $"Waiting for team member(s): {string.Join(", ", depNumbers.Select(n => $"#{n}"))}";
                    children[i].BlockedByTaskId = depIds[0];
                    children[i].BlockedByTaskNumber = depNumbers[0];
                }
            }

            return children;
        }

        /// <summary>
        /// Handles completion of the team decomposition phase: extracts team members from output,
        /// spawns them with message bus coordination, and transitions the parent into coordinator mode.
        /// </summary>
        public void HandleTeamCompletion(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var output = task.OutputBuilder.ToString();
            var children = ExtractAndSpawnTeam(task, output);

            if (children == null || children.Count == 0)
            {
                _outputProcessor.AppendOutput(task.Id,
                    "\nTeam decomposition produced no valid team members — completing parent task.\n",
                    activeTasks, historyTasks);
                task.SpawnTeam = false;
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                return;
            }

            // Mark parent as coordinator
            task.SpawnTeam = false;
            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            task.CompletionSummary = $"Spawned team of {children.Count} agent(s): {string.Join(", ", children.Select(c => c.Summary))}";
            _outputProcessor.AppendOutput(task.Id,
                $"\nTeam spawned with {children.Count} member(s). Parent is now a coordinator.\n",
                activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Fire SubTaskSpawned for each child so the UI picks them up
            foreach (var child in children)
                SubTaskSpawned?.Invoke(task, child);

            // Refresh dependency display info now that TaskNumbers are assigned
            RefreshDependencyDisplayInfo(children, "team member");
        }

        /// <summary>
        /// Refreshes DependencyTaskNumbers, BlockedByTaskNumber, and QueuedReason with actual
        /// TaskNumbers after children have been added to the UI (which assigns TaskNumbers).
        /// Must be called after SubTaskSpawned events have been fired for all children.
        /// </summary>
        internal static void RefreshDependencyDisplayInfo(List<AgentTask> children, string waitLabel = "subtask")
        {
            foreach (var child in children)
            {
                if (child.DependencyTaskIds.Count == 0) continue;

                var depNumbers = new List<int>();
                foreach (var depId in child.DependencyTaskIds)
                {
                    var dep = children.FirstOrDefault(c => c.Id == depId);
                    if (dep != null) depNumbers.Add(dep.TaskNumber);
                }
                child.DependencyTaskNumbers = depNumbers;

                if (child.BlockedByTaskId != null)
                {
                    var blocker = children.FirstOrDefault(c => c.Id == child.BlockedByTaskId);
                    if (blocker != null) child.BlockedByTaskNumber = blocker.TaskNumber;
                }

                if (depNumbers.Count > 0)
                    child.QueuedReason = $"Waiting for {waitLabel}(s): {string.Join(", ", depNumbers.Select(n => $"#{n}"))}";
            }
        }

        private class SubtaskEntry
        {
            [System.Text.Json.Serialization.JsonPropertyName("description")]
            public string Description { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("depends_on")]
            public List<int>? DependsOn { get; set; }
        }

        private class TeamMemberEntry
        {
            [System.Text.Json.Serialization.JsonPropertyName("role")]
            public string Role { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("description")]
            public string Description { get; set; } = "";

            [System.Text.Json.Serialization.JsonPropertyName("depends_on")]
            public List<int>? DependsOn { get; set; }
        }

        // ── Feature mode team extraction (used by TeamsModeHandler) ──

        /// <summary>
        /// Extracts team members from the teams mode planning output.
        /// Similar to ExtractAndSpawnTeam but returns children without marking the parent as Completed.
        /// </summary>
        private List<AgentTask>? ExtractAndSpawnTeamForFeatureMode(AgentTask parent, string output)
        {
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "TEAM");
            if (json == null)
            {
                AppLogger.Warn("TeamsMode", $"No ```TEAM``` block found in teams mode planning output for task {parent.Id}");
                return null;
            }

            List<TeamMemberEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<TeamMemberEntry>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TeamsMode", $"Failed to deserialize TEAM JSON for teams mode task {parent.Id}", ex);
                return null;
            }

            if (entries == null || entries.Count == 0) return null;
            if (entries.Count > 5) entries = entries.GetRange(0, 5);

            var children = new List<AgentTask>();
            foreach (var entry in entries)
            {
                var child = SpawnSubtask(parent, entry.Description);
                child.Summary = $"[{entry.Role}] {_taskFactory.GenerateLocalSummary(entry.Description)}";
                children.Add(child);
            }

            // Wire up inter-member dependencies
            for (int i = 0; i < entries.Count; i++)
            {
                var deps = entries[i].DependsOn;
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
                    children[i].QueuedReason = $"Waiting for team member(s): {string.Join(", ", depNumbers.Select(n => $"#{n}"))}";
                    children[i].BlockedByTaskId = depIds[0];
                    children[i].BlockedByTaskNumber = depNumbers[0];
                }
            }

            return children;
        }

        // ── Feature mode phase completion check ─────────────────────────

        /// <summary>
        /// Checks whether all children of a teams mode phase are complete.
        /// If so, triggers the next phase via TeamsModeHandler.
        /// Called from FinalizeTask when a child task completes.
        /// </summary>
        public void CheckTeamsModePhaseCompletion(AgentTask featureParent,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (!featureParent.IsTeamsMode) return;
            if (featureParent.TeamsModePhase is not (TeamsModePhase.TeamPlanning or TeamsModePhase.Execution)) return;
            if (featureParent.TeamsPhaseChildIdCount == 0) return;

            var children = featureParent.TeamsPhaseChildIds
                .Select(id => activeTasks.FirstOrDefault(t => t.Id == id)
                           ?? historyTasks.FirstOrDefault(t => t.Id == id))
                .Where(c => c != null)
                .ToList();

            // Guard against empty list after filtering
            if (children.Count == 0) return;

            var allComplete = children.Count > 0 && children.All(c => c != null && c.IsFinished);
            if (!allComplete) return;

            // If ALL children were cancelled, abort the teams mode task gracefully
            // instead of advancing to the next phase with empty results
            var allCancelled = children.All(c => c?.Status == AgentTaskStatus.Cancelled);
            if (allCancelled)
            {
                _outputProcessor.AppendOutput(featureParent.Id,
                    "\n[Teams Mode] All subtasks were cancelled. Aborting teams mode.\n",
                    activeTasks, historyTasks);
                featureParent.Status = AgentTaskStatus.Cancelled;
                featureParent.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(featureParent);
                moveToHistory(featureParent);
                return;
            }

            // Handle mixed cancelled/completed children
            var cancelledChildren = children.Where(c => c?.Status == AgentTaskStatus.Cancelled).ToList();
            var completedChildren = children.Where(c => c?.Status == AgentTaskStatus.Completed || c?.Status == AgentTaskStatus.Failed).ToList();

            if (cancelledChildren.Count > 0)
            {
                // Log warnings about cancelled children
                _outputProcessor.AppendOutput(featureParent.Id,
                    $"\n[Teams Mode] Warning: {cancelledChildren.Count} out of {children.Count} subtasks were cancelled:\n",
                    activeTasks, historyTasks);

                foreach (var cancelled in cancelledChildren)
                {
                    if (cancelled != null)
                    {
                        _outputProcessor.AppendOutput(featureParent.Id,
                            $"  - Task #{cancelled.TaskNumber}: {cancelled.Summary ?? cancelled.Description}\n",
                            activeTasks, historyTasks);
                    }
                }

                // If more than half the children were cancelled, fail the phase entirely
                if (cancelledChildren.Count > children.Count / 2)
                {
                    _outputProcessor.AppendOutput(featureParent.Id,
                        $"\n[Teams Mode] Majority of subtasks ({cancelledChildren.Count}/{children.Count}) were cancelled. Aborting teams mode.\n",
                        activeTasks, historyTasks);
                    featureParent.Status = AgentTaskStatus.Failed;
                    featureParent.EndTime = DateTime.Now;
                    featureParent.CompletionSummary = $"Feature mode aborted: {cancelledChildren.Count} out of {children.Count} subtasks were cancelled";
                    _outputTabManager.UpdateTabHeader(featureParent);
                    moveToHistory(featureParent);
                    return;
                }

                // Filter out cancelled children from TeamsPhaseChildIds before proceeding
                var cancelledIds = cancelledChildren.Where(c => c != null).Select(c => c!.Id).ToHashSet();
                var validChildIds = featureParent.TeamsPhaseChildIds
                    .Where(id => !cancelledIds.Contains(id))
                    .ToList();
                featureParent.ClearTeamsPhaseChildIds();
                foreach (var id in validChildIds)
                {
                    featureParent.AddTeamsPhaseChildId(id);
                }

                _outputProcessor.AppendOutput(featureParent.Id,
                    $"\n[Teams Mode] Proceeding with {completedChildren.Count} completed subtasks, ignoring {cancelledChildren.Count} cancelled ones.\n",
                    activeTasks, historyTasks);
            }

            _featureModeHandler.OnTeamsModePhaseComplete(featureParent, activeTasks, historyTasks, moveToHistory);
        }

        private static bool HasNeedsMoreWorkStatus(string outputText)
        {
            var tail = outputText.Length > 2000 ? outputText[^2000..] : outputText;
            foreach (var line in tail.Split('\n'))
            {
                if (line.Trim() == "STATUS: NEEDS_MORE_WORK")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the output contains "STATUS: COMPLETE WITH RECOMMENDATIONS" or
        /// "STATUS: NEEDS_MORE_WORK" as a standalone line in the tail.
        /// </summary>
        private static bool HasExplicitRecommendationStatus(string outputText)
        {
            var tail = outputText.Length > 2000 ? outputText[^2000..] : outputText;
            var lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == "STATUS: COMPLETE WITH RECOMMENDATIONS" || trimmed == "STATUS: NEEDS_MORE_WORK")
                    return true;
            }
            return false;
        }

        private static readonly HashSet<string> _skipPreprocessSummaries = new(StringComparer.OrdinalIgnoreCase)
        {
            "Test Verification",
            "Crash Log Investigation",
            "Build Test",
            "Generate Suggestions",
            "Re-assign"
        };

        private static bool IsKnownTaskType(string? summary) =>
            !string.IsNullOrEmpty(summary) && _skipPreprocessSummaries.Contains(summary);

        /// <summary>
        /// Lazily initializes the hybrid search manager for the project, then performs
        /// a hybrid search. Falls back to the standard feature resolver on any failure.
        /// </summary>
        private async System.Threading.Tasks.Task<FeatureContextResult?> ResolveWithHybridSearchAsync(
            string projectPath, string taskDescription, CancellationToken ct)
        {
            try
            {
                if (!_hybridSearchManager!.IsAvailable)
                    await _hybridSearchManager.InitializeAsync(projectPath);

                var searchRequest = new HybridSearchRequest { Query = taskDescription };
                return await _hybridSearchManager.SearchAsync(searchRequest, projectPath, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("TaskExecutionManager",
                    $"Hybrid search failed, falling back to feature resolver: {ex.Message}");
                return await _featureContextResolver.ResolveAsync(projectPath, taskDescription, ct: ct);
            }
        }
    }
}

