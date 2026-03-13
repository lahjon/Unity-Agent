using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;

namespace Spritely.Managers
{
    /// <summary>
    /// Handles output trimming, completion summary extraction, recommendation parsing,
    /// and subtask result injection. Accepts its dependencies via constructor injection.
    /// </summary>
    public class OutputProcessor
    {
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<bool> _getAutoVerify;
        private readonly Func<bool> _getShowCodeChanges;
        private readonly ICompletionAnalyzer _completionAnalyzer;
        private readonly IGitHelper _gitHelper;

        /// <summary>Fires when a completion summary has been generated for a task.</summary>
        public event Action<string>? CompletionSummaryGenerated;

        public OutputProcessor(OutputTabManager outputTabManager,
            ICompletionAnalyzer completionAnalyzer, IGitHelper gitHelper,
            Func<bool>? getAutoVerify = null, Func<bool>? getShowCodeChanges = null)
        {
            _outputTabManager = outputTabManager;
            _completionAnalyzer = completionAnalyzer;
            _gitHelper = gitHelper;
            _getAutoVerify = getAutoVerify ?? (() => false);
            _getShowCodeChanges = getShowCodeChanges ?? (() => false);
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
                    var summary = await _completionAnalyzer.GenerateCompletionSummaryAsync(
                        task.ProjectPath, task.GitStartHash, summaryStatus, duration,
                        task.InputTokens, task.OutputTokens, task.CacheReadTokens, task.CacheCreationTokens, ct);
                    task.CompletionSummary = summary;
                    if (!string.IsNullOrEmpty(summary))
                        AppendOutput(task.Id, summary, activeTasks, historyTasks);
                }
                catch (OperationCanceledException)
                {
                    var summary = _completionAnalyzer.FormatCompletionSummary(summaryStatus, duration, null,
                        task.InputTokens, task.OutputTokens, task.CacheReadTokens, task.CacheCreationTokens);
                    task.CompletionSummary = summary;
                    if (!string.IsNullOrEmpty(summary))
                        AppendOutput(task.Id, summary, activeTasks, historyTasks);
                }

                // Display colored code diff if "See Code Changes" is enabled
                if (_getShowCodeChanges())
                    await AppendCodeChangeDiffAsync(task, activeTasks, historyTasks);

                // Run result verification if auto-verify is enabled
                // This now also extracts next steps via Haiku (cheap, no extra LLM call)
                if (_getAutoVerify())
                    await RunResultVerificationAsync(task, outputText, activeTasks, historyTasks);

                // Extract recommendations when the AI outputs STATUS: COMPLETE WITH RECOMMENDATIONS
                // or STATUS: NEEDS_MORE_WORK as a standalone line.
                if (!task.HasRecommendations && HasExplicitRecommendationStatus(outputText))
                {
                    try
                    {
                        var recommendations = _completionAnalyzer.ExtractRecommendations(outputText);
                        if (!string.IsNullOrWhiteSpace(recommendations))
                        {
                            task.Recommendations = recommendations;
                            AppendOutput(task.Id,
                                $"\nRecommendations:\n{recommendations}\n",
                                activeTasks, historyTasks);
                        }
                        else if (HasNeedsMoreWorkStatus(outputText))
                        {
                            // NEEDS_MORE_WORK without explicit recommendation headers —
                            // still enable the Continue button with a generic prompt
                            task.Recommendations = "Continue working on the incomplete task.";
                        }
                        else if (HasCompleteWithRecommendationsStatus(outputText))
                        {
                            // COMPLETE WITH RECOMMENDATIONS without extractable recommendation headers —
                            // still enable the Continue button so the status is properly recognized
                            task.Recommendations = "Continue with the recommended next steps from this task.";
                        }
                    }
                    catch (Exception recEx)
                    {
                        AppLogger.Debug("OutputProcessor", $"Failed to extract recommendations for task {task.Id}", recEx);
                    }
                }

                // Append verification prompt to OutputBuilder for pipeline diagnostics (not displayed in tab)
                if (!string.IsNullOrWhiteSpace(task.Runtime.VerificationPrompt))
                {
                    task.OutputBuilder.AppendLine();
                    task.OutputBuilder.AppendLine("── Verification Prompt (sent to Haiku) ─────");
                    task.OutputBuilder.AppendLine(task.Runtime.VerificationPrompt);
                    task.OutputBuilder.AppendLine("── End Verification Prompt ──────────────────");
                }

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
                // Use Sonnet for build/test tasks — better reasoning on complex build/test scenarios
                var useSonnet = task.Summary is "Crash Log Investigation" or "Test Verification" or "Build Test";
                var verificationModel = useSonnet ? Constants.AppConstants.ClaudeSonnet : null;

                var result = await _completionAnalyzer.VerifyResultAsync(
                    outputText, task.Description, task.CompletionSummary, ct, verificationModel);

                if (result != null)
                {
                    task.IsVerified = result.Passed;
                    task.VerificationResult = result.Summary;

                    // Store verification prompt for pipeline diagnostics
                    if (!string.IsNullOrWhiteSpace(result.SentPrompt))
                        task.Runtime.VerificationPrompt = result.SentPrompt;

                    var label = result.Passed ? "PASSED" : "FAILED";
                    AppendOutput(task.Id,
                        $"\nResult Verification: {label} — {result.Summary}\n",
                        activeTasks, historyTasks);
                    AppLogger.Debug("TaskExecution",
                        $"Task {task.Id}: Result verification {label} — {result.Summary}");

                    // Display next steps (informational only — does not trigger Recommendation status)
                    if (!string.IsNullOrWhiteSpace(result.NextSteps))
                    {
                        AppendOutput(task.Id,
                            $"Next Steps: {result.NextSteps}\n",
                            activeTasks, historyTasks);
                    }
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
        /// Fetches the full unified diff since task start and appends it to the output
        /// with green coloring for added lines and red coloring for removed lines.
        /// </summary>
        private async System.Threading.Tasks.Task AppendCodeChangeDiffAsync(AgentTask task,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            try
            {
                var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;
                var diff = await _gitHelper.GetFullDiffAsync(task.ProjectPath, task.GitStartHash, ct)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(diff)) return;

                var greenBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E)); // green for additions
                var redBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5D, 0x5D));   // red for removals
                var cyanBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xD6));  // cyan for file headers
                var mutedBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));  // muted for hunk headers
                greenBrush.Freeze();
                redBrush.Freeze();
                cyanBrush.Freeze();
                mutedBrush.Freeze();

                AppendOutput(task.Id, "\n═══ Code Changes ═══════════════════════════\n", activeTasks, historyTasks);

                string? currentFile = null;
                foreach (var line in diff.Split('\n'))
                {
                    if (line.StartsWith("diff --git"))
                    {
                        // Extract file name from "diff --git a/path b/path"
                        var parts = line.Split(" b/", 2);
                        var fileName = parts.Length > 1 ? parts[1] : line;
                        if (currentFile != null)
                            AppendOutput(task.Id, "\n", activeTasks, historyTasks);
                        currentFile = fileName;
                        AppendColoredOutput(task.Id, $"── {fileName} ──\n", cyanBrush, activeTasks, historyTasks);
                    }
                    else if (line.StartsWith("@@"))
                    {
                        AppendColoredOutput(task.Id, line + "\n", mutedBrush, activeTasks, historyTasks);
                    }
                    else if (line.StartsWith("+") && !line.StartsWith("+++"))
                    {
                        AppendColoredOutput(task.Id, line + "\n", greenBrush, activeTasks, historyTasks);
                    }
                    else if (line.StartsWith("-") && !line.StartsWith("---"))
                    {
                        AppendColoredOutput(task.Id, line + "\n", redBrush, activeTasks, historyTasks);
                    }
                    else if (line.StartsWith("---") || line.StartsWith("+++") ||
                             line.StartsWith("index ") || line.StartsWith("new file") ||
                             line.StartsWith("deleted file") || line.StartsWith("Binary"))
                    {
                        // Skip git metadata lines
                    }
                    else
                    {
                        // Context lines
                        AppendOutput(task.Id, line + "\n", activeTasks, historyTasks);
                    }
                }

                AppendOutput(task.Id, "═══════════════════════════════════════════\n", activeTasks, historyTasks);
            }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception ex)
            {
                AppLogger.Debug("OutputProcessor", "Failed to append code change diff", ex);
            }
        }

        /// <summary>
        /// If the completing task has a ParentTaskId, finds the parent and injects the subtask result.
        /// </summary>
        public async System.Threading.Tasks.Task TryInjectSubtaskResultAsync(AgentTask child,
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

            await InjectSubtaskResultAsync(parent, child);
        }

        /// <summary>
        /// Synchronous wrapper kept for backward compatibility (non-UI-thread callers).
        /// </summary>
        public void TryInjectSubtaskResult(AgentTask child,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            _ = TryInjectSubtaskResultAsync(child, activeTasks, historyTasks);
        }

        /// <summary>
        /// Injects a subtask's result into the parent task's message bus inbox.
        /// Writes a JSON file with type='subtask_result' containing the child's
        /// summary, status, file changes, and recommendations.
        /// Uses async git diff to avoid blocking the UI thread.
        /// </summary>
        public async System.Threading.Tasks.Task InjectSubtaskResultAsync(AgentTask parent, AgentTask child)
        {
            try
            {
                var safeProjectName = MessageBusManager.GetSafeProjectName(parent.ProjectPath);
                var busDir = Path.Combine(MessageBusManager.AppDataBusRoot, safeProjectName);
                var inboxDir = Path.Combine(busDir, "inbox");
                Directory.CreateDirectory(inboxDir);

                // Gather file changes from git diff asynchronously to avoid blocking the UI thread
                List<object>? fileChangesList = null;
                try
                {
                    var changes = await _gitHelper.GetGitFileChangesAsync(
                        child.ProjectPath, child.GitStartHash, CancellationToken.None);
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
                var recommendations = _completionAnalyzer.ExtractRecommendations(childOutput) ?? "";

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

        /// <summary>
        /// Synchronous wrapper kept for backward compatibility (non-UI-thread callers).
        /// </summary>
        public void InjectSubtaskResult(AgentTask parent, AgentTask child)
            => _ = InjectSubtaskResultAsync(parent, child);

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

        private static bool HasCompleteWithRecommendationsStatus(string outputText)
        {
            var tail = outputText.Length > 2000 ? outputText[^2000..] : outputText;
            foreach (var line in tail.Split('\n'))
            {
                if (line.Trim() == "STATUS: COMPLETE WITH RECOMMENDATIONS")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks whether the output contains "STATUS: COMPLETE WITH RECOMMENDATIONS" or
        /// "STATUS: NEEDS_MORE_WORK" as a standalone line produced by the AI.
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
    }
}
