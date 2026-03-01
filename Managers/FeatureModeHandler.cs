using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages feature mode retry timers, iteration tracking, and consecutive failure counting.
    /// Accepts its dependencies via constructor injection and exposes events for status changes.
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
        internal const int FeatureModeIterationTimeoutMinutes = 30;
        internal const int FeatureModeMaxConsecutiveFailures = 3;
        internal const int FeatureModeOutputCapChars = 100_000;

        /// <summary>Fires when a new feature mode iteration starts (taskId, iteration).</summary>
        public event Action<string, int>? IterationStarted;

        /// <summary>Fires when the feature mode finishes for a task (taskId, finalStatus).</summary>
        public event Action<string, AgentTaskStatus>? FeatureModeFinished;

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

            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= FeatureModeMaxRuntimeHours)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Total runtime cap ({FeatureModeMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (TaskLauncher.CheckFeatureModeComplete(iterationOutput))
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Task finished.\n", activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (task.CurrentIteration >= task.MaxIterations)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Max iterations ({task.MaxIterations}) reached. Stopping.\n", activeTasks, historyTasks);
                FinishFeatureModeTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveFailures++;
                _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Iteration exited with code {exitCode} (failure {task.ConsecutiveFailures}/{FeatureModeMaxConsecutiveFailures})\n", activeTasks, historyTasks);
                if (task.ConsecutiveFailures >= FeatureModeMaxConsecutiveFailures)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] {FeatureModeMaxConsecutiveFailures} consecutive failures detected (crash loop). Stopping.\n", activeTasks, historyTasks);
                    FinishFeatureModeTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
                    return;
                }
            }
            else
            {
                task.ConsecutiveFailures = 0;
            }

            if (TaskLauncher.IsTokenLimitError(iterationOutput))
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
                    StartFeatureModeContinuation(task, activeTasks, historyTasks, moveToHistory);
                };
                timer.Start();
                return;
            }

            if (task.OutputBuilder.Length > FeatureModeOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - FeatureModeOutputCapChars, FeatureModeOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = 0;
            }

            task.CurrentIteration++;
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            var iterRuntime = DateTime.Now - task.StartTime;
            var iterTokenInfo = task.HasTokenData
                ? $" | Tokens: {Helpers.FormatHelpers.FormatTokenCount(task.InputTokens + task.OutputTokens)} ({Helpers.FormatHelpers.FormatTokenCount(task.InputTokens)} in / {Helpers.FormatHelpers.FormatTokenCount(task.OutputTokens)} out)"
                : "";
            _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Starting iteration {task.CurrentIteration}/{task.MaxIterations} | Runtime: {(int)iterRuntime.TotalMinutes}m {iterRuntime.Seconds}s{iterTokenInfo}\n\n", activeTasks, historyTasks);
            IterationStarted?.Invoke(task.Id, task.CurrentIteration);
            StartFeatureModeContinuation(task, activeTasks, historyTasks, moveToHistory);
        }

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
            // Set to Verifying while summary + verification run; final status is set afterwards
            task.Status = AgentTaskStatus.Verifying;
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            var duration = (DateTime.Now) - task.StartTime;
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

        private void StartFeatureModeContinuation(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var continuationPrompt = TaskLauncher.BuildFeatureModeContinuationPrompt(task.CurrentIteration, task.MaxIterations, task.Id);

            var promptFile = Path.Combine(_scriptDir, $"feature_{task.Id}_{task.CurrentIteration}.txt");
            File.WriteAllText(promptFile, continuationPrompt, Encoding.UTF8);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var teamFlag = task.SpawnTeam ? " --spawn-team" : "";
            var resumeFlag = !string.IsNullOrEmpty(task.ConversationId)
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose{resumeFlag} --output-format stream-json $prompt";

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
                        _outputProcessor.AppendOutput(task.Id, $"\n[Feature Mode] Iteration timeout ({FeatureModeIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                        try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck feature mode process for task {task.Id}", ex); }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[Feature Mode] ERROR starting continuation: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
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
        /// Mirrors the logic in HandleFeatureModeIteration without side effects.
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
