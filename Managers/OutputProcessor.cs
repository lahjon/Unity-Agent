using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace AgenticEngine.Managers
{
    /// <summary>
    /// Handles output trimming, completion summary extraction, recommendation parsing,
    /// and subtask result injection. Accepts its dependencies via constructor injection.
    /// </summary>
    public class OutputProcessor
    {
        private readonly OutputTabManager _outputTabManager;

        /// <summary>Fires when a completion summary has been generated for a task.</summary>
        public event Action<string>? CompletionSummaryGenerated;

        public OutputProcessor(OutputTabManager outputTabManager)
        {
            _outputTabManager = outputTabManager;
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
        /// Extracts recommendations from the current iteration output, verifies them with the LLM,
        /// and generates a git-diff-based completion summary.
        /// </summary>
        public async void AppendCompletionSummary(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            // Detect recommendations from the current iteration output only (avoids re-detecting old recommendations)
            var fullOutput = task.OutputBuilder.ToString();
            var outputText = task.LastIterationOutputStart > 0 && task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;
            var heuristicRecommendations = TaskLauncher.ExtractRecommendations(outputText);
            task.ContinueReason = "";

            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;

            // Always evaluate task completion with LLM — pass heuristic recommendations as context if available
            try
            {
                var verification = await TaskLauncher.VerifyContinueNeededAsync(
                    outputText, heuristicRecommendations, task.Description, ct);

                if (verification != null)
                {
                    if (verification.ShouldContinue)
                    {
                        // Use heuristic recommendations if available, otherwise use LLM's reason as the recommendation
                        task.Recommendations = !string.IsNullOrEmpty(heuristicRecommendations)
                            ? heuristicRecommendations
                            : verification.Reason;
                        task.ContinueReason = verification.Reason;
                        AppLogger.Debug("TaskExecution", $"Task {task.Id}: LLM verified continue needed — {verification.Reason}");
                    }
                    else
                    {
                        task.Recommendations = "";
                        AppLogger.Debug("TaskExecution", $"Task {task.Id}: LLM verified complete — {verification.Reason}");
                    }
                }
                else
                {
                    // Verification failed to parse — fall back to heuristic result if available
                    if (!string.IsNullOrEmpty(heuristicRecommendations))
                    {
                        task.Recommendations = heuristicRecommendations;
                        task.ContinueReason = "Agent left recommendations (verification unavailable)";
                    }
                    else
                    {
                        task.Recommendations = "";
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("TaskExecution", "Continue verification failed", ex);
                if (!string.IsNullOrEmpty(heuristicRecommendations))
                {
                    task.Recommendations = heuristicRecommendations;
                    task.ContinueReason = "Agent left recommendations (verification unavailable)";
                }
                else
                {
                    task.Recommendations = "";
                }
            }

            var duration = (task.EndTime ?? DateTime.Now) - task.StartTime;
            try
            {
                var summary = await TaskLauncher.GenerateCompletionSummaryAsync(
                    task.ProjectPath, task.GitStartHash, task.Status, duration, ct);
                task.CompletionSummary = summary;
                AppendOutput(task.Id, summary, activeTasks, historyTasks);
            }
            catch (OperationCanceledException)
            {
                var summary = TaskLauncher.FormatCompletionSummary(task.Status, duration, null);
                task.CompletionSummary = summary;
                AppendOutput(task.Id, summary, activeTasks, historyTasks);
            }

            CompletionSummaryGenerated?.Invoke(task.Id);
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
