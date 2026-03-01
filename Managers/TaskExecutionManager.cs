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
using HappyEngine.Helpers;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Thin coordinator that delegates to focused single-responsibility classes:
    /// <see cref="TaskProcessLauncher"/> for subprocess creation and process lifecycle,
    /// <see cref="FeatureModeHandler"/> for feature mode retry timers and iteration tracking,
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
        private readonly MessageBusManager _messageBusManager;
        private readonly Dispatcher _dispatcher;

        // ── Injected services ────────────────────────────────────────
        private readonly IGitHelper _gitHelper;
        private readonly ICompletionAnalyzer _completionAnalyzer;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ITaskFactory _taskFactory;

        // ── Focused sub-handlers ─────────────────────────────────────
        private readonly OutputProcessor _outputProcessor;
        private readonly TaskProcessLauncher _processLauncher;
        private readonly FeatureModeHandler _featureModeHandler;
        private readonly TokenLimitHandler _tokenLimitHandler;

        // ── Static git operation serialization ──────────────────────
        /// <summary>
        /// Serializes git operations across all tasks to prevent concurrent git commits
        /// from racing against each other and hitting git's index.lock.
        /// </summary>
        private static readonly SemaphoreSlim _gitCommitSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Gets the git commit semaphore for external use (e.g., MainWindow commit operations).
        /// </summary>
        public static SemaphoreSlim GitCommitSemaphore => _gitCommitSemaphore;

        /// <summary>Exposes the streaming tool state dictionary for external consumers.</summary>
        public ConcurrentDictionary<string, StreamingToolState> StreamingToolState => _processLauncher.StreamingToolState;

        /// <summary>Fires when a task's process exits (with the task ID). Used to resume dependency-queued tasks.</summary>
        public event Action<string>? TaskCompleted;

        /// <summary>Fires when a subtask is spawned (parent, child). MainWindow subscribes to wire the subtask into _activeTasks and create its output tab.</summary>
        public event Action<AgentTask, AgentTask>? SubTaskSpawned;

        public TaskExecutionManager(
            string scriptDir,
            FileLockManager fileLockManager,
            OutputTabManager outputTabManager,
            IGitHelper gitHelper,
            ICompletionAnalyzer completionAnalyzer,
            IPromptBuilder promptBuilder,
            ITaskFactory taskFactory,
            Func<string> getSystemPrompt,
            Func<AgentTask, string> getProjectDescription,
            Func<string, string> getProjectRulesBlock,
            Func<string, bool> isGameProject,
            MessageBusManager messageBusManager,
            Dispatcher dispatcher,
            Func<int>? getTokenLimitRetryMinutes = null,
            Func<bool>? getAutoVerify = null)
        {
            _scriptDir = scriptDir;
            _fileLockManager = fileLockManager;
            _outputTabManager = outputTabManager;
            _gitHelper = gitHelper;
            _completionAnalyzer = completionAnalyzer;
            _promptBuilder = promptBuilder;
            _taskFactory = taskFactory;
            _getSystemPrompt = getSystemPrompt;
            _getProjectDescription = getProjectDescription;
            _getProjectRulesBlock = getProjectRulesBlock;
            _isGameProject = isGameProject;
            _messageBusManager = messageBusManager;
            _dispatcher = dispatcher;

            var retryMinutesFunc = getTokenLimitRetryMinutes ?? (() => 30);

            // Wire up sub-handlers
            _outputProcessor = new OutputProcessor(outputTabManager, completionAnalyzer, gitHelper, getAutoVerify);
            _processLauncher = new TaskProcessLauncher(scriptDir, fileLockManager, outputTabManager, _outputProcessor, promptBuilder, dispatcher);
            _featureModeHandler = new FeatureModeHandler(scriptDir, _processLauncher, _outputProcessor, messageBusManager, outputTabManager, completionAnalyzer, promptBuilder, taskFactory, retryMinutesFunc);
            _tokenLimitHandler = new TokenLimitHandler(scriptDir, _processLauncher, _outputProcessor, fileLockManager, messageBusManager, outputTabManager, completionAnalyzer, retryMinutesFunc);

            // Forward token-limit handler's TaskCompleted to the coordinator's event
            _tokenLimitHandler.TaskCompleted += id => TaskCompleted?.Invoke(id);

            // Wire up feature mode handler events for team/step extraction and child spawning
            _featureModeHandler.ExtractTeamRequested += (parent, output) => ExtractAndSpawnTeamForFeatureMode(parent, output);
            _featureModeHandler.FeatureModeChildSpawned += (parent, child) => SubTaskSpawned?.Invoke(parent, child);
        }

        // ── Prompt preparation ────────────────────────────────────────

        /// <summary>
        /// Builds the full prompt for a task and writes it to disk.
        /// When <paramref name="activeTasks"/> is provided, auto-enables the message bus
        /// if sibling tasks exist on the same project.
        /// </summary>
        private string BuildAndWritePromptFile(AgentTask task, ObservableCollection<AgentTask>? activeTasks = null)
        {
            var fullPrompt = _promptBuilder.BuildFullPrompt(
                _getSystemPrompt(), task,
                _getProjectDescription(task),
                _getProjectRulesBlock(task.ProjectPath),
                _isGameProject(task.ProjectPath));

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

            if (task.IsFeatureMode)
                _taskFactory.PrepareTaskForFeatureModeStart(task);

            var promptFile = BuildAndWritePromptFile(task, activeTasks);
            var projectPath = task.ProjectPath;

            var cliModel = PromptBuilder.GetCliModelForTask(task);
            var claudeCmd = _promptBuilder.BuildClaudeCommand(task.SkipPermissions, task.RemoteSession, cliModel);

            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildPowerShellScript(projectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Task #{task.TaskNumber} starting...\n", activeTasks, historyTasks);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Summary: {task.Summary}\n", activeTasks, historyTasks);
            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Project: {projectPath}\n", activeTasks, historyTasks);
            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Model: {PromptBuilder.GetFriendlyModelName(cliModel)} ({cliModel})\n", activeTasks, historyTasks);
            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Skip permissions: {task.SkipPermissions}\n", activeTasks, historyTasks);
            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Remote session: {task.RemoteSession}\n", activeTasks, historyTasks);
            if (task.UseMessageBus)
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Message Bus: ON\n", activeTasks, historyTasks);
            if (task.ExtendedPlanning)
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Extended planning: ON\n", activeTasks, historyTasks);
            if (task.AutoDecompose)
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Auto-decompose: ON (will spawn subtasks)\n", activeTasks, historyTasks);
            if (task.SpawnTeam)
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Spawn Team: ON (will decompose into team roles with message bus)\n", activeTasks, historyTasks);
            if (task.IsFeatureMode)
            {
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Feature mode: ON (max {task.MaxIterations} iterations, 12h cap)\n", activeTasks, historyTasks);
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Safety: skip-permissions forced, git blocked, 30min iteration timeout\n", activeTasks, historyTasks);
            }
            // Show the full prompt that Claude will receive
            try
            {
                var promptContent = File.ReadAllText(promptFile, Encoding.UTF8);
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] ── Full Prompt ──────────────────────────────\n{promptContent}\n[HappyEngine] ── End Prompt ───────────────────────────────\n\n", activeTasks, historyTasks);
            }
            catch { /* non-critical */ }

            _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Connecting to Claude...\n\n", activeTasks, historyTasks);

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);

                // Plan-before-queue: killed process needs to restart in plan mode
                if (task.NeedsPlanRestart)
                {
                    task.NeedsPlanRestart = false;
                    task.IsPlanningBeforeQueue = true;
                    task.Status = AgentTaskStatus.Planning;
                    task.StartTime = DateTime.Now;
                    _outputProcessor.AppendOutput(task.Id, "\n[HappyEngine] Restarting in plan mode...\n\n", activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = StartProcess(task, activeTasks, historyTasks, moveToHistory);
                    return;
                }

                // Plan-before-queue: planning phase complete
                if (task.IsPlanningBeforeQueue)
                {
                    HandlePlanBeforeQueueCompletion(task, activeTasks, historyTasks, moveToHistory);
                    return;
                }

                // Auto-decompose: decomposition phase complete — spawn subtasks
                if (task.AutoDecompose && task.Status == AgentTaskStatus.Running)
                {
                    HandleDecompositionCompletion(task, activeTasks, historyTasks, moveToHistory);
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Feature mode: multi-phase orchestration — always handle before SpawnTeam
                if (task.IsFeatureMode && task.Status == AgentTaskStatus.Running)
                {
                    _featureModeHandler.HandleFeatureModeIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
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
                    // Capture locked files BEFORE releasing so we can scope the auto-commit
                    var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);
                    // NOTE: Lock release moved to CompleteWithVerificationAsync to prevent race condition
                    // where another task could modify files before commit completes
                    // Set to Verifying while summary runs; final status is set afterwards
                    task.Status = AgentTaskStatus.Verifying;
                    _outputTabManager.UpdateTabHeader(task);
                    _ = CompleteWithVerificationAsync(task, exitCode, activeTasks, historyTasks, lockedFiles);
                    return;
                }
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);

                if (task.IsFeatureMode)
                {
                    var iterationTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(FeatureModeHandler.FeatureModeIterationTimeoutMinutes)
                    };
                    task.FeatureModeIterationTimer = iterationTimer;
                    iterationTimer.Tick += (_, _) =>
                    {
                        iterationTimer.Stop();
                        task.FeatureModeIterationTimer = null;
                        if (task.Process is { HasExited: false })
                        {
                            _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Iteration timeout ({FeatureModeHandler.FeatureModeIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                            try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck process for task {task.Id}", ex); }
                        }
                    };
                    iterationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] ERROR starting process: {ex.Message}\n", activeTasks, historyTasks);
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
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            IReadOnlyCollection<string>? lockedFiles = null)
        {
            var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;

            try
            {
                // Auto-commit only the files this task locked (not all local changes)
                if (exitCode == 0 && !task.NoGitWrite && lockedFiles is { Count: > 0 })
                {
                    await CommitTaskLockedFilesAsync(task, lockedFiles, activeTasks, historyTasks);
                }
            }
            finally
            {
                // Release locks AFTER commit to prevent race condition where another task
                // could modify files before commit completes
                _fileLockManager.ReleaseTaskLocks(task.Id);

                // Also handle message bus cleanup
                if (task.UseMessageBus)
                    _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            }

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

            // If a follow-up was started during summary generation, the status will
            // have changed from Verifying to Running — don't overwrite it.
            if (task.Status != AgentTaskStatus.Verifying)
                return;

            // Now set the final status after summary is complete
            task.Status = expectedStatus;
            task.EndTime = DateTime.Now;
            try
            {
                var statusColor = exitCode == 0
                    ? (Brush)Application.Current.FindResource("Success")
                    : (Brush)Application.Current.FindResource("DangerBright");
                _outputProcessor.AppendColoredOutput(task.Id,
                    $"\n[HappyEngine] Process finished (exit code: {exitCode}).\n",
                    statusColor, activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Error("TaskExecution", $"Failed to append completion output for task {task.Id}", ex);
            }
            _outputTabManager.UpdateTabHeader(task);

            _fileLockManager.CheckQueuedTasks(activeTasks);
            TaskCompleted?.Invoke(task.Id);
        }

        /// <summary>
        /// Runs the completion summary after a follow-up completes, then sets the final status.
        /// Fires TaskCompleted so feature mode parents and the orchestrator are notified.
        /// </summary>
        private async System.Threading.Tasks.Task CompleteFollowUpWithVerificationAsync(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;

            // Capture locked files before releasing so we can scope the auto-commit
            var lockedFiles = _fileLockManager.GetTaskLockedFiles(task.Id);

            try
            {
                // Auto-commit only the files this task locked (not all local changes)
                if (exitCode == 0 && !task.NoGitWrite && lockedFiles.Count > 0)
                {
                    await CommitTaskLockedFilesAsync(task, lockedFiles, activeTasks, historyTasks);
                }
            }
            finally
            {
                // Release locks AFTER commit to prevent race condition where another task
                // could modify files before commit completes
                _fileLockManager.ReleaseTaskLocks(task.Id);

                // Also handle message bus cleanup
                if (task.UseMessageBus)
                    _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            }

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
            if (task.Status != AgentTaskStatus.Verifying)
                return;

            task.Status = expectedStatus;
            task.EndTime = DateTime.Now;
            _outputProcessor.AppendOutput(task.Id, "\n[HappyEngine] Follow-up complete.\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            // Check queued tasks after releasing locks to unblock waiting tasks
            _fileLockManager.CheckQueuedTasks(activeTasks);
            TaskCompleted?.Invoke(task.Id);
        }

        /// <summary>
        /// Commits only the files that were locked by this task, not all local changes.
        /// Uses <c>git commit -- &lt;files&gt;</c> to scope the commit to exactly those paths,
        /// preventing concurrent tasks' changes from being included.
        /// </summary>
        private async System.Threading.Tasks.Task CommitTaskLockedFilesAsync(AgentTask task,
            IReadOnlyCollection<string> lockedFiles,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (string.IsNullOrEmpty(task.ProjectPath) || lockedFiles.Count == 0)
                return;

            try
            {
                // Build relative paths from the project root for the git commands
                var projectRoot = task.ProjectPath.TrimEnd('\\', '/').ToLowerInvariant() + "\\";
                var relativePaths = new List<string>();
                foreach (var absPath in lockedFiles)
                {
                    var rel = absPath.StartsWith(projectRoot)
                        ? absPath[projectRoot.Length..]
                        : absPath;
                    relativePaths.Add(rel.Replace('\\', '/'));
                }

                // Serialize git operations to prevent concurrent commits from racing
                await _gitCommitSemaphore.WaitAsync();
                try
                {
                    // Stage only the locked files (handles new/untracked files)
                    var escapedPaths = relativePaths.Select(GitHelper.EscapeGitPath).ToList();
                    var pathArgs = string.Join(" ", escapedPaths);
                    var addResult = await _gitHelper.RunGitCommandAsync(task.ProjectPath, $"add -- {pathArgs}");

                    // Check if git add failed
                    if (addResult == null)
                    {
                        _outputProcessor.AppendOutput(task.Id,
                            "[HappyEngine] Failed to stage files for auto-commit\n",
                            activeTasks, historyTasks);
                        return;
                    }

                    // Commit only these specific files — using the secure method to prevent shell injection
                    var desc = task.Description?.Length > 70
                        ? task.Description[..70] + "..."
                        : task.Description ?? "task changes";
                    var commitMsg = $"Task #{task.TaskNumber}: {desc}";
                    var result = await _gitHelper.CommitSecureAsync(task.ProjectPath, commitMsg, pathArgs);

                    if (result != null)
                    {
                        // Get the commit hash after successful commit
                        var commitHash = await _gitHelper.CaptureGitHeadAsync(task.ProjectPath, task.Cts.Token);
                        if (commitHash != null)
                        {
                            task.IsCommitted = true;
                            task.CommitHash = commitHash;
                            _outputProcessor.AppendOutput(task.Id,
                                $"\n[HappyEngine] Auto-committed {relativePaths.Count} locked file(s). Commit: {commitHash[..8]}\n",
                                activeTasks, historyTasks);
                        }
                        else
                        {
                            _outputProcessor.AppendOutput(task.Id,
                                $"\n[HappyEngine] Auto-committed {relativePaths.Count} locked file(s) but failed to capture commit hash.\n",
                                activeTasks, historyTasks);
                        }
                    }
                }
                finally
                {
                    _gitCommitSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskExecution", $"Auto-commit failed for task {task.Id}", ex);
            }
        }

        public void LaunchHeadless(AgentTask task)
        {
            var promptFile = BuildAndWritePromptFile(task);
            var projectPath = task.ProjectPath;
            var cliModel = PromptBuilder.GetCliModelForTask(task);

            var ps1File = Path.Combine(_scriptDir, $"headless_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                _promptBuilder.BuildHeadlessPowerShellScript(projectPath, promptFile, task.SkipPermissions, task.RemoteSession, cliModel),
                Encoding.UTF8);

            var psi = _promptBuilder.BuildProcessStartInfo(ps1File, headless: true);
            psi.WorkingDirectory = projectPath;
            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Dialogs.DarkDialog.ShowConfirm($"Failed to launch terminal:\n{ex.Message}", "Launch Error");
            }
        }

        // ── Input / Follow-up ────────────────────────────────────────

        public void SendInput(AgentTask task, TextBox inputBox,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var text = inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputBox.Clear();
            SendFollowUp(task, text, activeTasks, historyTasks);
        }

        public void SendFollowUp(AgentTask task, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (string.IsNullOrEmpty(text)) return;

            AppLogger.Info("FollowUp", $"[{task.Id}] SendFollowUp called. Status={task.Status}, IsFeatureMode={task.IsFeatureMode}, Phase={task.FeatureModePhase}, ProcessAlive={task.Process is { HasExited: false }}, ConversationId={task.ConversationId ?? "(null)"}");
            AppLogger.Info("FollowUp", $"[{task.Id}] Task details: ActiveTasksCount={activeTasks.Count}, HistoryTasksCount={historyTasks.Count}");

            // Ensure task is in active tasks (not history)
            var isInActive = activeTasks.Contains(task);
            var isInHistory = historyTasks.Contains(task);
            AppLogger.Info("FollowUp", $"[{task.Id}] Task location: InActive={isInActive}, InHistory={isInHistory}");

            // Block follow-up when the task is a feature mode coordinator waiting for subtasks.
            // Without this, it would start a new Claude process that has no context about the
            // coordination and asks "what do you want me to do?"
            if (task.IsFeatureMode && task.FeatureModePhase is FeatureModePhase.TeamPlanning or FeatureModePhase.Execution)
            {
                AppLogger.Warn("FollowUp", $"[{task.Id}] Blocked: feature mode coordinator in phase {task.FeatureModePhase}");
                _outputProcessor.AppendOutput(task.Id,
                    "\n[HappyEngine] This task is coordinating subtasks and waiting for them to complete. Follow-up input is not available during this phase.\n",
                    activeTasks, historyTasks);
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning && task.Process is { HasExited: false })
            {
                try
                {
                    AppLogger.Info("FollowUp", $"[{task.Id}] Writing to existing stdin (process alive)");
                    _outputProcessor.AppendOutput(task.Id, $"\n> {text}\n", activeTasks, historyTasks);
                    task.Process.StandardInput.WriteLine(text);
                    return;
                }
                catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to write to stdin for task {task.Id}, starting follow-up", ex); }
            }

            task.Status = AgentTaskStatus.Running;
            task.EndTime = null;
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();
            _outputTabManager.UpdateTabHeader(task);

            // Use --resume with session ID when available, fall back to --continue
            var hasSessionId = !string.IsNullOrEmpty(task.ConversationId);
            var resumeFlag = hasSessionId
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var resumeLabel = hasSessionId
                ? $"--resume {task.ConversationId}"
                : "--continue";

            var followUpModel = PromptBuilder.GetCliModelForTask(task);
            _outputProcessor.AppendOutput(task.Id, $"\n> {text}\n[HappyEngine] Sending follow-up with {resumeLabel} (Model: {PromptBuilder.GetFriendlyModelName(followUpModel)})...\n\n", activeTasks, historyTasks);

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
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Follow-up process exited (code={exitCode})\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Verifying;
                _outputTabManager.UpdateTabHeader(task);
                _ = CompleteFollowUpWithVerificationAsync(task, exitCode, activeTasks, historyTasks);
            });
            AppLogger.Info("FollowUp", $"[{task.Id}] Process created, about to start");

            try
            {
                _processLauncher.StartManagedProcess(task, process);
                AppLogger.Info("FollowUp", $"[{task.Id}] Process started successfully. PID={task.Process?.Id}");
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Follow-up process started (PID={task.Process?.Id})\n", activeTasks, historyTasks);
            }
            catch (Exception ex)
            {
                AppLogger.Error("FollowUp", $"[{task.Id}] Failed to start follow-up process", ex);
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Follow-up error: {ex.Message}\n", activeTasks, historyTasks);
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

            // Fire TaskCompleted so the orchestrator and feature mode parent get notified.
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

        public void ResumeTask(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
            => _processLauncher.ResumeTask(task, activeTasks, historyTasks);

        // ── Plan-before-queue completion ─────────────────────────────

        private void HandlePlanBeforeQueueCompletion(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.IsPlanningBeforeQueue = false;
            task.PlanOnly = false;

            // Extract execution prompt from plan output
            var output = task.OutputBuilder.ToString();
            var executionPrompt = FormatHelpers.ExtractExecutionPrompt(output);
            if (!string.IsNullOrEmpty(executionPrompt))
                task.StoredPrompt = executionPrompt;

            // Check dependency-based queue
            var depSnapshot = task.DependencyTaskIds;
            if (depSnapshot.Count > 0)
            {
                var allResolved = depSnapshot.All(depId =>
                {
                    var dep = activeTasks.FirstOrDefault(t => t.Id == depId);
                    return dep == null || dep.IsFinished;
                });

                if (!allResolved)
                {
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Plan complete, waiting for dependencies";
                    var blocker = activeTasks.FirstOrDefault(t =>
                        depSnapshot.Contains(t.Id) && !t.IsFinished);
                    task.BlockedByTaskId = blocker?.Id;
                    task.BlockedByTaskNumber = blocker?.TaskNumber;
                    _outputProcessor.AppendOutput(task.Id,
                        $"\n[HappyEngine] Planning complete. Queued — waiting for dependencies: " +
                        $"{string.Join(", ", task.DependencyTaskNumbers.Select(n => $"#{n}"))}\n",
                        activeTasks, historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    return;
                }
                // Dependencies resolved during planning — gather context before clearing
                task.DependencyContext = _promptBuilder.BuildDependencyContext(
                    depSnapshot, activeTasks, historyTasks);
                task.ClearDependencyTaskIds();
                task.DependencyTaskNumbers.Clear();
            }

            // No more blockers — start execution
            task.Status = AgentTaskStatus.Running;
            task.StartTime = DateTime.Now;
            _outputProcessor.AppendOutput(task.Id, "\n[HappyEngine] Planning complete. Starting execution...\n\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = StartProcess(task, activeTasks, historyTasks, moveToHistory);
        }

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
                    parent.RemoteSession,
                    parent.Headless,
                    parent.IsFeatureMode,
                    parent.IgnoreFileLocks,
                    parent.UseMcp,
                    parent.SpawnTeam,
                    parent.ExtendedPlanning,
                    parent.NoGitWrite,
                    planOnly: false,
                    parent.UseMessageBus,
                    model: parent.Model)
                : _taskFactory.CreateTask(
                    description,
                    parent.ProjectPath,
                    skipPermissions: false,
                    remoteSession: false,
                    headless: false,
                    isFeatureMode: false,
                    ignoreFileLocks: false,
                    useMcp: false);

            child.ParentTaskId = parent.Id;
            child.ProjectColor = parent.ProjectColor;
            child.ProjectDisplayName = parent.ProjectDisplayName;

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

            // Cap at 5 subtasks
            if (entries.Count > 5)
                entries = entries.GetRange(0, 5);

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
                    "\n[HappyEngine] Decomposition produced no valid subtasks — completing parent task.\n",
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
                $"\n[HappyEngine] Task decomposed into {children.Count} subtask(s). Parent is now a coordinator.\n",
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
        /// Pure decision function for feature mode iteration logic.
        /// Delegates to <see cref="FeatureModeHandler.EvaluateFeatureModeIteration"/>.
        /// </summary>
        internal static FeatureModeHandler.FeatureModeDecision EvaluateFeatureModeIteration(
            ICompletionAnalyzer completionAnalyzer,
            AgentTaskStatus currentStatus,
            TimeSpan totalRuntime,
            string iterationOutput,
            int currentIteration,
            int maxIterations,
            int exitCode,
            int consecutiveFailures,
            int outputLength)
            => FeatureModeHandler.EvaluateFeatureModeIteration(
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
                    "\n[HappyEngine] Team decomposition produced no valid team members — completing parent task.\n",
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
                $"\n[HappyEngine] Team spawned with {children.Count} member(s). Parent is now a coordinator.\n",
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

        // ── Feature mode team extraction (used by FeatureModeHandler) ──

        /// <summary>
        /// Extracts team members from the feature mode planning output.
        /// Similar to ExtractAndSpawnTeam but returns children without marking the parent as Completed.
        /// </summary>
        private List<AgentTask>? ExtractAndSpawnTeamForFeatureMode(AgentTask parent, string output)
        {
            var json = Helpers.FormatHelpers.ExtractCodeBlockContent(output, "TEAM");
            if (json == null)
            {
                AppLogger.Warn("FeatureMode", $"No ```TEAM``` block found in feature mode planning output for task {parent.Id}");
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
                AppLogger.Warn("FeatureMode", $"Failed to deserialize TEAM JSON for feature mode task {parent.Id}", ex);
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
        /// Checks whether all children of a feature mode phase are complete.
        /// If so, triggers the next phase via FeatureModeHandler.
        /// Called from FinalizeTask when a child task completes.
        /// </summary>
        public void CheckFeatureModePhaseCompletion(AgentTask featureParent,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (!featureParent.IsFeatureMode) return;
            if (featureParent.FeatureModePhase is not (FeatureModePhase.TeamPlanning or FeatureModePhase.Execution)) return;
            if (featureParent.FeaturePhaseChildIds.Count == 0) return;

            var children = featureParent.FeaturePhaseChildIds
                .Select(id => activeTasks.FirstOrDefault(t => t.Id == id)
                           ?? historyTasks.FirstOrDefault(t => t.Id == id))
                .ToList();

            var allComplete = children.All(c => c?.IsFinished == true);
            if (!allComplete) return;

            // If ALL children were cancelled, abort the feature mode task gracefully
            // instead of advancing to the next phase with empty results
            var allCancelled = children.All(c => c?.Status == AgentTaskStatus.Cancelled);
            if (allCancelled)
            {
                _outputProcessor.AppendOutput(featureParent.Id,
                    "\n[Feature Mode] All subtasks were cancelled. Aborting feature mode.\n",
                    activeTasks, historyTasks);
                featureParent.Status = AgentTaskStatus.Cancelled;
                featureParent.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(featureParent);
                moveToHistory(featureParent);
                return;
            }

            _featureModeHandler.OnFeatureModePhaseComplete(featureParent, activeTasks, historyTasks, moveToHistory);
        }
    }
}

