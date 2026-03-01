using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Handles token/rate limit detection and automatic retry scheduling for non-feature-mode tasks.
    /// Accepts its dependencies via constructor injection and exposes events for status changes.
    /// </summary>
    public class TokenLimitHandler
    {
        private readonly string _scriptDir;
        private readonly TaskProcessLauncher _processLauncher;
        private readonly OutputProcessor _outputProcessor;
        private readonly FileLockManager _fileLockManager;
        private readonly MessageBusManager _messageBusManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<int> _getTokenLimitRetryMinutes;

        /// <summary>Fires when the handler finishes a task normally after a retry (taskId).</summary>
        public event Action<string>? TaskCompleted;

        /// <summary>Fires when a retry timer is scheduled (taskId, delayMinutes).</summary>
        public event Action<string, int>? RetryScheduled;

        /// <summary>Fires when a retry continuation actually starts (taskId).</summary>
        public event Action<string>? RetryStarted;

        public TokenLimitHandler(
            string scriptDir,
            TaskProcessLauncher processLauncher,
            OutputProcessor outputProcessor,
            FileLockManager fileLockManager,
            MessageBusManager messageBusManager,
            OutputTabManager outputTabManager,
            Func<int> getTokenLimitRetryMinutes)
        {
            _scriptDir = scriptDir;
            _processLauncher = processLauncher;
            _outputProcessor = outputProcessor;
            _fileLockManager = fileLockManager;
            _messageBusManager = messageBusManager;
            _outputTabManager = outputTabManager;
            _getTokenLimitRetryMinutes = getTokenLimitRetryMinutes;
        }

        /// <summary>
        /// Checks if a non-feature-mode task failed due to a token/rate limit error.
        /// If so, schedules an automatic retry after the configured interval.
        /// Returns true if a retry was scheduled (caller should skip normal completion).
        /// </summary>
        public bool HandleTokenLimitRetry(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            var output = task.OutputBuilder.ToString();
            var tail = output.Length > 3000 ? output[^3000..] : output;
            if (!TaskLauncher.IsTokenLimitError(tail)) return false;

            var retryMinutes = _getTokenLimitRetryMinutes();
            _outputProcessor.AppendOutput(task.Id, $"\n[HappyEngine] Token/rate limit detected. Retrying in {retryMinutes} minutes...\n", activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(retryMinutes) };
            task.TokenLimitRetryTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                task.TokenLimitRetryTimer = null;
                if (task.Status != AgentTaskStatus.Running)
                {
                    // Task was cancelled while waiting
                    _fileLockManager.ReleaseTaskLocks(task.Id);
                    if (task.UseMessageBus)
                        _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
                    _outputTabManager.UpdateTabHeader(task);
                    return;
                }
                _outputProcessor.AppendOutput(task.Id, "[HappyEngine] Retrying after token limit...\n", activeTasks, historyTasks);
                _outputTabManager.UpdateTabHeader(task);
                RetryStarted?.Invoke(task.Id);

                SendTokenLimitRetryContinuation(task, activeTasks, historyTasks, moveToHistory);
            };
            timer.Start();
            RetryScheduled?.Invoke(task.Id, retryMinutes);
            return true;
        }

        /// <summary>
        /// Sends a --continue/--resume follow-up after a token limit retry, with an exit handler
        /// that re-checks for token limits so retries can chain indefinitely.
        /// </summary>
        private void SendTokenLimitRetryContinuation(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            Action<AgentTask> moveToHistory)
        {
            task.Status = AgentTaskStatus.Running;
            task.EndTime = null;
            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            _outputTabManager.UpdateTabHeader(task);

            var hasSessionId = !string.IsNullOrEmpty(task.ConversationId);
            var resumeFlag = hasSessionId
                ? $" --resume {task.ConversationId}"
                : " --continue";
            var resumeLabel = hasSessionId
                ? $"--resume {task.ConversationId}"
                : "--continue";

            var continuePrompt = "Continue where you left off. The previous attempt was interrupted by a token/rate limit. Pick up from where you stopped.";
            _outputProcessor.AppendOutput(task.Id, $"\n[HappyEngine] Sending retry with {resumeLabel}...\n\n", activeTasks, historyTasks);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var followUpFile = Path.Combine(_scriptDir, $"retry_{task.Id}_{DateTime.Now.Ticks}.txt");
            File.WriteAllText(followUpFile, continuePrompt, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"retry_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{followUpFile}'\n" +
                $"claude -p{skipFlag}{resumeFlag} --verbose --output-format stream-json $prompt\n",
                Encoding.UTF8);

            var process = _processLauncher.CreateManagedProcess(ps1File, task.Id, activeTasks, historyTasks, exitCode =>
            {
                _processLauncher.CleanupScripts(task.Id);

                if (task.Status is AgentTaskStatus.Queued or AgentTaskStatus.Cancelled)
                {
                    _fileLockManager.CheckQueuedTasks(activeTasks);
                    TaskCompleted?.Invoke(task.Id);
                    return;
                }

                // Check if this retry also hit a token limit — chain another retry
                if (exitCode != 0 && task.Status == AgentTaskStatus.Running && HandleTokenLimitRetry(task, activeTasks, historyTasks, moveToHistory))
                    return;

                _fileLockManager.ReleaseTaskLocks(task.Id);
                if (task.UseMessageBus)
                    _messageBusManager.LeaveBus(task.ProjectPath, task.Id);
                // Set to Verifying while summary + verification run; final status is set afterwards
                task.Status = AgentTaskStatus.Verifying;
                _outputTabManager.UpdateTabHeader(task);
                _ = CompleteRetryWithVerificationAsync(task, exitCode, activeTasks, historyTasks);
            });

            try
            {
                _processLauncher.StartManagedProcess(task, process);
            }
            catch (Exception ex)
            {
                _outputProcessor.AppendOutput(task.Id, $"[HappyEngine] Retry error: {ex.Message}\n", activeTasks, historyTasks);
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.UpdateTabHeader(task);
            }
        }
        /// <summary>
        /// Runs verification after a token-limit retry completes, then sets the final status.
        /// </summary>
        private async System.Threading.Tasks.Task CompleteRetryWithVerificationAsync(AgentTask task, int exitCode,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var expectedStatus = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;

            await _outputProcessor.AppendCompletionSummary(task, activeTasks, historyTasks, expectedStatus);
            await _outputProcessor.TryInjectSubtaskResultAsync(task, activeTasks, historyTasks);

            task.Status = expectedStatus;
            task.EndTime = DateTime.Now;
            var statusColor = exitCode == 0
                ? Application.Current?.TryFindResource("Success") as Brush ?? Brushes.Green
                : Application.Current?.TryFindResource("DangerBright") as Brush ?? Brushes.Red;
            _outputProcessor.AppendColoredOutput(task.Id,
                $"\n[HappyEngine] Process finished (exit code: {exitCode}).\n",
                statusColor, activeTasks, historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _fileLockManager.CheckQueuedTasks(activeTasks);
            TaskCompleted?.Invoke(task.Id);
        }
    }
}
