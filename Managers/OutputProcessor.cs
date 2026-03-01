using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Handles output trimming, completion summary extraction, recommendation parsing,
    /// and subtask result injection. Accepts its dependencies via constructor injection.
    /// </summary>
    public class OutputProcessor
    {
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<bool> _getAutoVerify;

        /// <summary>Fires when a completion summary has been generated for a task.</summary>
        public event Action<string>? CompletionSummaryGenerated;

        public OutputProcessor(OutputTabManager outputTabManager, Func<bool>? getAutoVerify = null)
        {
            _outputTabManager = outputTabManager;
            _getAutoVerify = getAutoVerify ?? (() => false);
        }

        public void AppendOutput(string taskId, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _outputTabManager.AppendOutput(taskId, text, activeTasks, historyTasks);
        }

        public void AppendColoredOutput(string taskId, string text, Brush foreground,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _outputTabManager.AppendColoredOutput(taskId, text, foreground, activeTasks, historyTasks);
        }

        /// <summary>
        /// Generates a git-diff-based completion summary and optionally runs result verification.
        /// </summary>
        /// <param name="expectedStatus">The status to use for summary generation (the final status
        /// based on exit code), since the task may still be in Verifying state.</param>
        public async System.Threading.Tasks.Task AppendCompletionSummary(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks,
            AgentTaskStatus? expectedStatus = null)
        {
            var summaryStatus = expectedStatus ?? task.Status;
            try
            {
                var fullOutput = task.OutputBuilder.ToString();
                var outputText = task.LastIterationOutputStart > 0 && task.LastIterationOutputStart < fullOutput.Length
                    ? fullOutput[task.LastIterationOutputStart..]
                    : fullOutput;

                var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;

                var duration = (task.EndTime ?? DateTime.Now) - task.StartTime;
                try
                {
                    var summary = await TaskLauncher.GenerateCompletionSummaryAsync(
                        task.ProjectPath, task.GitStartHash, summaryStatus, duration, ct);
                    task.CompletionSummary = summary;
                    AppendOutput(task.Id, summary, activeTasks, historyTasks);
                }
                catch (OperationCanceledException)
                {
                    var summary = TaskLauncher.FormatCompletionSummary(summaryStatus, duration, null);
                    task.CompletionSummary = summary;
                    AppendOutput(task.Id, summary, activeTasks, historyTasks);
                }

                // Run result verification if auto-verify is enabled
                if (_getAutoVerify())
                    await RunResultVerificationAsync(task, outputText, activeTasks, historyTasks);

                CompletionSummaryGenerated?.Invoke(task.Id);
            }
            catch (Exception ex)
            {
                AppLogger.Error("OutputProcessor", $"AppendCompletionSummary failed for task {task.Id}", ex);
            }
        }

        /// <summary>
        /// Runs LLM-based result verification on a completed task.
        /// Can be called automatically after completion or manually via the Verify button.
        /// </summary>
        public async System.Threading.Tasks.Task RunResultVerificationAsync(AgentTask task,
            string? outputText,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;

            if (string.IsNullOrEmpty(outputText))
            {
                var fullOutput = task.OutputBuilder.ToString();
                outputText = task.LastIterationOutputStart > 0 && task.LastIterationOutputStart < fullOutput.Length
                    ? fullOutput[task.LastIterationOutputStart..]
                    : fullOutput;
            }

            try
            {
                var result = await TaskLauncher.VerifyResultAsync(
                    outputText, task.Description, task.CompletionSummary, ct);

                if (result != null)
                {
                    task.IsVerified = result.Passed;
                    task.VerificationResult = result.Summary;
                    var label = result.Passed ? "PASSED" : "FAILED";
                    AppendOutput(task.Id,
                        $"\n[HappyEngine] Result Verification: {label} — {result.Summary}\n",
                        activeTasks, historyTasks);
                    AppLogger.Debug("TaskExecution",
                        $"Task {task.Id}: Result verification {label} — {result.Summary}");
                }
                else
                {
                    task.VerificationResult = "Verification unavailable";
                    AppLogger.Debug("TaskExecution",
                        $"Task {task.Id}: Result verification could not parse response");
                }
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception ex)
            {
                task.VerificationResult = "Verification failed";
                AppLogger.Debug("TaskExecution", "Result verification failed", ex);
            }
        }

        /// <summary>
        /// If the completing task has a ParentTaskId, finds the parent and injects the subtask result.
        /// </summary>
        public void TryInjectSubtaskResult(AgentTask child,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (child.ParentTaskId == null) return;

            var parent = activeTasks.FirstOrDefault(t => t.Id == child.ParentTaskId)
                      ?? historyTasks.FirstOrDefault(t => t.Id == child.ParentTaskId);
            if (parent == null)
            {
                AppLogger.Warn("TaskExecution", $"Subtask {child.Id} completed but parent {child.ParentTaskId} not found");
                return;
            }

            InjectSubtaskResult(parent, child);
        }

        /// <summary>
        /// Injects a subtask's result into the parent task's message bus inbox.
        /// Writes a JSON file with type='subtask_result' containing the child's
        /// summary, status, file changes, and recommendations.
        /// </summary>
        public void InjectSubtaskResult(AgentTask parent, AgentTask child)
        {
            try
            {
                var busDir = Path.Combine(parent.ProjectPath, ".agent-bus");
                var inboxDir = Path.Combine(busDir, "inbox");
                Directory.CreateDirectory(inboxDir);

                // Gather file changes from git diff
                List<object>? fileChangesList = null;
                try
                {
                    var changes = TaskLauncher.GetGitFileChanges(child.ProjectPath, child.GitStartHash);
                    if (changes != null)
                    {
                        fileChangesList = new List<object>();
                        foreach (var (name, added, removed) in changes)
                            fileChangesList.Add(new { file = name, added, removed });
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("TaskExecution", $"Failed to get file changes for subtask result {child.Id}", ex);
                }

                // Extract recommendations from child output
                var childOutput = child.OutputBuilder.ToString();
                var recommendations = TaskLauncher.ExtractRecommendations(childOutput) ?? "";

                var payload = new
                {
                    from = child.Id,
                    type = "subtask_result",
                    topic = $"Subtask #{child.TaskNumber} result",
                    parent_task_id = parent.Id,
                    child_task_id = child.Id,
                    child_task_number = child.TaskNumber,
                    child_description = child.Description,
                    status = child.Status.ToString(),
                    summary = child.CompletionSummary,
                    recommendations,
                    file_changes = fileChangesList,
                    body = $"Subtask #{child.TaskNumber} ({child.Status}): {child.Description}"
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var fileName = $"{timestamp}_{child.Id}_subtask_result.json";
                var filePath = Path.Combine(inboxDir, fileName);

                File.WriteAllText(filePath, json);
                AppLogger.Info("TaskExecution", $"Injected subtask result for #{child.TaskNumber} into parent #{parent.TaskNumber} bus at {filePath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskExecution", $"Failed to inject subtask result for {child.Id} into parent {parent.Id}", ex);
            }
        }
    }
}
