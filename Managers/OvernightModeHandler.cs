using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages overnight retry timers, iteration tracking, and consecutive failure counting.
    /// Accepts its dependencies via constructor injection and exposes events for status changes.
    /// </summary>
    public class OvernightModeHandler
    {
        private readonly string _scriptDir;
        private readonly TaskProcessLauncher _processLauncher;
        private readonly OutputProcessor _outputProcessor;
        private readonly MessageBusManager _messageBusManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<int> _getTokenLimitRetryMinutes;

        internal const int OvernightMaxRuntimeHours = 12;
        internal const int OvernightIterationTimeoutMinutes = 30;
        internal const int OvernightMaxConsecutiveFailures = 3;
        internal const int OvernightOutputCapChars = 100_000;

        /// <summary>Fires when a new overnight iteration starts (taskId, iteration).</summary>
        public event Action<string, int>? IterationStarted;

        /// <summary>Fires when the overnight mode finishes for a task (taskId, finalStatus).</summary>
        public event Action<string, AgentTaskStatus>? OvernightFinished;

        public OvernightModeHandler(
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

        public void HandleOvernightIteration(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }

            if (task.Status != AgentTaskStatus.Running) return;

            var fullOutput = task.OutputBuilder.ToString();
            var iterationOutput = task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;

            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= OvernightMaxRuntimeHours)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Total runtime cap ({OvernightMaxRuntimeHours}h) reached. Stopping.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (TaskLauncher.CheckOvernightComplete(iterationOutput))
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Task finished.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (task.CurrentIteration >= task.MaxIterations)
            {
                _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Max iterations ({task.MaxIterations}) reached. Stopping.\n", activeTasks, historyTasks);
                FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                return;
            }

            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveFailures++;
                _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Iteration exited with code {exitCode} (failure {task.ConsecutiveFailures}/{OvernightMaxConsecutiveFailures})\n", activeTasks, historyTasks);
                if (task.ConsecutiveFailures >= OvernightMaxConsecutiveFailures)
                {
                    _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] {OvernightMaxConsecutiveFailures} consecutive failures detected (crash loop). Stopping.\n", activeTasks, historyTasks);
                    FinishOvernightTask(task, AgentTaskStatus.Failed, activeTasks, historyTasks, moveToHistory);
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
                _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Token limit hit. Retrying in {retryMinutes} minutes...\n", activeTasks, historyTasks);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(retryMinutes) };
                task.OvernightRetryTimer = timer;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    task.OvernightRetryTimer = null;
                    if (task.Status != AgentTaskStatus.Running) return;
                    if ((DateTime.Now - task.StartTime).TotalHours >= OvernightMaxRuntimeHours)
                    {
                        _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Runtime cap reached during retry wait. Stopping.\n", activeTasks, historyTasks);
                        FinishOvernightTask(task, AgentTaskStatus.Completed, activeTasks, historyTasks, moveToHistory);
                        return;
                    }
                    _outputProcessor.AppendOutput(task.Id, "[Overnight] Retrying...\n", activeTasks, historyTasks);
                    StartOvernightContinuation(task, activeTasks, historyTasks, moveToHistory);
                };
                timer.Start();
                return;
            }

            if (task.OutputBuilder.Length > OvernightOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - OvernightOutputCapChars, OvernightOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = 0;
            }

            task.CurrentIteration++;
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Starting iteration {task.CurrentIteration}/{task.MaxIterations}...\n\n", activeTasks, historyTasks);
            IterationStarted?.Invoke(task.Id, task.CurrentIteration);
            StartOvernightContinuation(task, activeTasks, historyTasks, moveToHistory);
        }

        private void FinishOvernightTask(AgentTask task, AgentTaskStatus status,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            // Set to Verifying while summary + verification run; final status is set afterwards
            task.Status = AgentTaskStatus.Verifying;
            if (task.UseMessageBus)
                _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
            var duration = (DateTime.Now) - task.StartTime;
            _outputProcessor.AppendOutput(task.Id, $"[Overnight] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = CompleteOvernightWithVerificationAsync(task, status, activeTasks, historyTasks, moveToHistory);
        }

        private async System.Threading.Tasks.Task CompleteOvernightWithVerificationAsync(AgentTask task, AgentTaskStatus finalStatus,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            await _outputProcessor.AppendCompletionSummary(task, activeTasks, historyTasks, finalStatus);
            _outputProcessor.TryInjectSubtaskResult(task, activeTasks, historyTasks);

            task.Status = finalStatus;
            task.EndTime = DateTime.Now;
            _outputTabManager.UpdateTabHeader(task);
            moveToHistory(task);
            OvernightFinished?.Invoke(task.Id, finalStatus);
        }

        private void StartOvernightContinuation(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var continuationPrompt = TaskLauncher.BuildOvernightContinuationPrompt(task.CurrentIteration, task.MaxIterations, task.Id);

            var promptFile = Path.Combine(_scriptDir, $"overnight_{task.Id}_{task.CurrentIteration}.txt");
            File.WriteAllText(promptFile, continuationPrompt, Encoding.UTF8);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var teamFlag = task.SpawnTeam ? " --spawn-team" : "";
            var resumeFlag = !string.IsNullOrEmpty(task.ConversationId)
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose{resumeFlag} --output-format stream-json $prompt";

            var ps1File = Path.Combine(_scriptDir, $"overnight_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);
                HandleOvernightIteration(task, exitCode, activeTasks, historyTasks, moveToHistory);
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);

                var iterationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(OvernightIterationTimeoutMinutes)
                };
                task.OvernightIterationTimer = iterationTimer;
                iterationTimer.Tick += (_, _) =>
                {
                    iterationTimer.Stop();
                    task.OvernightIterationTimer = null;
                    if (task.Process is { HasExited: false })
                    {
                        _outputProcessor.AppendOutput(task.Id, $"\n[Overnight] Iteration timeout ({OvernightIterationTimeoutMinutes}min). Killing stuck process.\n", activeTasks, historyTasks);
                        try { task.Process.Kill(true); } catch (Exception ex) { AppLogger.Warn("TaskExecution", $"Failed to kill stuck overnight process for task {task.Id}", ex); }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[Overnight] ERROR starting continuation: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
                moveToHistory(task);
            }
        }

        // ── Overnight iteration decision logic (extracted for testability) ──

        internal enum OvernightAction { Skip, Finish, RetryAfterDelay, Continue }

        internal struct OvernightDecision
        {
            public OvernightAction Action;
            public AgentTaskStatus FinishStatus;
            public int ConsecutiveFailures;
            public bool TrimOutput;
        }

        /// <summary>
        /// Pure decision function that evaluates what the overnight loop should do next.
        /// Mirrors the logic in HandleOvernightIteration without side effects.
        /// </summary>
        internal static OvernightDecision EvaluateOvernightIteration(
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
                return new OvernightDecision { Action = OvernightAction.Skip };

            if (totalRuntime.TotalHours >= OvernightMaxRuntimeHours)
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (TaskLauncher.CheckOvernightComplete(iterationOutput))
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            if (currentIteration >= maxIterations)
                return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Completed };

            var newFailures = consecutiveFailures;
            if (exitCode != 0 && !TaskLauncher.IsTokenLimitError(iterationOutput))
            {
                newFailures++;
                if (newFailures >= OvernightMaxConsecutiveFailures)
                    return new OvernightDecision { Action = OvernightAction.Finish, FinishStatus = AgentTaskStatus.Failed, ConsecutiveFailures = newFailures };
            }
            else
            {
                newFailures = 0;
            }

            if (TaskLauncher.IsTokenLimitError(iterationOutput))
                return new OvernightDecision { Action = OvernightAction.RetryAfterDelay, ConsecutiveFailures = newFailures };

            return new OvernightDecision
            {
                Action = OvernightAction.Continue,
                ConsecutiveFailures = newFailures,
                TrimOutput = outputLength > OvernightOutputCapChars
            };
        }
    }
}
