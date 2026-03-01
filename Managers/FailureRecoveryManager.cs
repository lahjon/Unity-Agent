using System;
using System.Collections.ObjectModel;
using System.Text;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Detects failed tasks and spawns diagnostic child tasks to attempt automatic recovery.
    /// Extracts error patterns from task output and constructs a corrective prompt
    /// that includes the original task description plus failure context.
    /// </summary>
    public class FailureRecoveryManager
    {
        private readonly TaskExecutionManager _taskExecutionManager;
        private readonly OutputTabManager _outputTabManager;
        private readonly Func<bool> _getAutoRecover;

        public FailureRecoveryManager(
            TaskExecutionManager taskExecutionManager,
            OutputTabManager outputTabManager,
            Func<bool> getAutoRecover)
        {
            _taskExecutionManager = taskExecutionManager;
            _outputTabManager = outputTabManager;
            _getAutoRecover = getAutoRecover;
        }

        /// <summary>
        /// Checks whether a failed task should trigger automatic recovery, and if so, spawns
        /// a diagnostic child task. Returns true if a recovery task was spawned.
        /// </summary>
        public bool TrySpawnRecoveryTask(AgentTask failedTask,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (!_getAutoRecover())
                return false;

            // Only recover tasks that actually failed
            if (failedTask.Status != AgentTaskStatus.Failed)
                return false;

            // Don't recover recovery tasks (prevent infinite loops)
            if (failedTask.IsRecoveryTask)
            {
                AppLogger.Info("FailureRecovery",
                    $"Skipping recovery for task #{failedTask.TaskNumber} — it is already a recovery task");
                return false;
            }

            // Don't recover tasks that are subtasks of feature mode (those have their own retry logic)
            if (failedTask.IsFeatureMode)
                return false;

            try
            {
                var recoveryPrompt = BuildRecoveryPrompt(failedTask);
                var child = _taskExecutionManager.SpawnSubtask(failedTask, recoveryPrompt);
                child.IsRecoveryTask = true;
                child.AutoDecompose = false;
                child.SpawnTeam = false;
                child.Summary = $"[Recovery] Fix: {failedTask.Summary ?? TaskLauncher.GenerateLocalSummary(failedTask.Description)}";

                _outputTabManager.AppendOutput(failedTask.Id,
                    $"\n[HappyEngine] Auto-Recovery: Spawning diagnostic task #{child.TaskNumber} to fix failure.\n",
                    activeTasks, historyTasks);

                AppLogger.Info("FailureRecovery",
                    $"Spawned recovery task #{child.TaskNumber} for failed task #{failedTask.TaskNumber}");

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FailureRecovery",
                    $"Failed to spawn recovery task for #{failedTask.TaskNumber}", ex);
                return false;
            }
        }

        /// <summary>
        /// Builds a corrective prompt that includes the FailureRecoveryBlock,
        /// the original task description, and failure context (output tail with errors/stack traces).
        /// </summary>
        private static string BuildRecoveryPrompt(AgentTask failedTask)
        {
            var sb = new StringBuilder();

            sb.AppendLine(PromptBuilder.FailureRecoveryBlock);

            // Original task description (capped to avoid bloating recovery prompt)
            const int maxDescriptionLength = 2000;
            var originalDescription = !string.IsNullOrEmpty(failedTask.StoredPrompt)
                ? failedTask.StoredPrompt
                : failedTask.Description;
            if (originalDescription != null && originalDescription.Length > maxDescriptionLength)
                originalDescription = originalDescription[..maxDescriptionLength] + "\n… [truncated]";
            sb.AppendLine("## ORIGINAL TASK");
            sb.AppendLine(originalDescription);
            sb.AppendLine();

            // Failure context — extract error-relevant portions from the output
            var fullOutput = failedTask.OutputBuilder.ToString();
            var errorContext = ExtractErrorContext(fullOutput);
            if (!string.IsNullOrWhiteSpace(errorContext))
            {
                sb.AppendLine("## FAILURE OUTPUT (error context)");
                sb.AppendLine("```");
                sb.AppendLine(errorContext);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Completion summary if available
            if (!string.IsNullOrWhiteSpace(failedTask.CompletionSummary))
            {
                sb.AppendLine("## COMPLETION SUMMARY FROM FAILED ATTEMPT");
                sb.AppendLine(failedTask.CompletionSummary);
                sb.AppendLine();
            }

            // Verification result if available
            if (!string.IsNullOrWhiteSpace(failedTask.VerificationResult))
            {
                sb.AppendLine("## VERIFICATION RESULT");
                sb.AppendLine(failedTask.VerificationResult);
                sb.AppendLine();
            }

            sb.AppendLine("## INSTRUCTIONS");
            sb.AppendLine("Fix the issue described above. The original task should be fully completed after your fix.");

            return sb.ToString();
        }

        /// <summary>
        /// Extracts error-relevant context from the full output. Looks for error patterns,
        /// stack traces, and the tail of the output, then combines them into a focused
        /// error summary within a character budget.
        /// </summary>
        private static string ExtractErrorContext(string fullOutput)
        {
            if (string.IsNullOrWhiteSpace(fullOutput))
                return "";

            const int maxContextLength = 3000;

            // Take the tail of the output which typically contains the final error
            var tail = fullOutput.Length > maxContextLength
                ? fullOutput[^maxContextLength..]
                : fullOutput;

            // Look for error-heavy lines and stack traces
            var lines = tail.Split('\n');
            var sb = new StringBuilder();
            var inErrorBlock = false;

            foreach (var line in lines)
            {
                var lower = line.ToLowerInvariant();
                var isErrorLine = lower.Contains("error") ||
                                  lower.Contains("exception") ||
                                  lower.Contains("failed") ||
                                  lower.Contains("stack trace") ||
                                  lower.Contains("   at ") ||
                                  lower.Contains("traceback") ||
                                  lower.Contains("fatal") ||
                                  lower.Contains("cannot find") ||
                                  lower.Contains("does not exist") ||
                                  lower.Contains("not found") ||
                                  lower.Contains("compilation") ||
                                  lower.Contains("build failed") ||
                                  lower.Contains("syntax error");

                if (isErrorLine)
                {
                    inErrorBlock = true;
                }
                else if (inErrorBlock)
                {
                    // Non-error line after an error block — stop capturing this block
                    inErrorBlock = false;
                }

                if (inErrorBlock)
                {
                    sb.AppendLine(line);
                    if (sb.Length > maxContextLength)
                        break;
                }
            }

            // If no specific error lines found, just return the output tail
            if (sb.Length == 0)
            {
                var fallbackLength = Math.Min(fullOutput.Length, 4000);
                return fullOutput[^fallbackLength..];
            }

            return sb.ToString();
        }
    }
}
