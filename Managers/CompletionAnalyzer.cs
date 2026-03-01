using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public class CompletionAnalyzer : ICompletionAnalyzer
    {
        private readonly IGitHelper _gitHelper;

        public CompletionAnalyzer(IGitHelper gitHelper)
        {
            _gitHelper = gitHelper;
        }

        // ── Keyword Arrays ──────────────────────────────────────────

        private static readonly string[] RecommendationHeaders = {
            "next steps", "next step", "recommendations", "recommended next",
            "suggestions", "suggested next", "follow-up", "follow up",
            "remaining work", "future improvements", "action items",
            "things to consider", "to do next", "what's next"
        };

        private static readonly string[] CompletionIndicators = {
            "task is complete", "completed all", "all changes have been",
            "implementation is complete", "successfully completed",
            "everything is set up", "changes are complete",
            "all tasks are done", "have been implemented",
            "is now complete", "are now complete",
            "this completes", "that completes",
            "finished implementing", "all requested changes",
            "completed the", "i've made all", "i have made all",
            "everything is working", "everything works",
            "all the changes", "successfully implemented",
            "has been completed", "have been completed"
        };

        private static readonly string[] IncompletionIndicators = {
            "remaining work", "couldn't complete", "wasn't able to",
            "could not complete", "still needs", "still need to",
            "not yet implemented", "not yet complete", "incomplete",
            "blocked by", "unable to complete", "failed to complete",
            "needs to be done", "need to be done", "todo", "to-do",
            "unfinished", "not finished", "partially"
        };

        // ── Completion Detection ────────────────────────────────────

        public bool IsTaskOutputComplete(string[] lines, int recommendationLine)
        {
            var searchStart = Math.Max(0, recommendationLine - Constants.AppConstants.RecommendationContextBefore);
            var searchEnd = Math.Min(lines.Length, recommendationLine + Constants.AppConstants.RecommendationContextAfter);

            bool hasCompletion = false;
            bool hasIncompletion = false;

            for (int i = searchStart; i < searchEnd; i++)
            {
                var lower = lines[i].ToLowerInvariant();

                if (!hasCompletion)
                {
                    foreach (var indicator in CompletionIndicators)
                    {
                        if (lower.Contains(indicator)) { hasCompletion = true; break; }
                    }
                }

                if (!hasIncompletion)
                {
                    foreach (var indicator in IncompletionIndicators)
                    {
                        if (lower.Contains(indicator)) { hasIncompletion = true; break; }
                    }
                }
            }

            return hasCompletion && !hasIncompletion;
        }

        // ── Recommendation Extraction ───────────────────────────────

        public string? ExtractRecommendations(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            var summaryIdx = output.LastIndexOf("═══════════════════════════════════════════", StringComparison.Ordinal);
            var text = summaryIdx > 0 ? output[..summaryIdx] : output;

            var searchText = text.Length > 4000 ? text[^4000..] : text;
            var lines = searchText.Split('\n');

            var startLine = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var lower = lines[i].ToLowerInvariant().Trim();
                if (string.IsNullOrEmpty(lower)) continue;

                foreach (var header in RecommendationHeaders)
                {
                    if (lower.Contains(header))
                    {
                        startLine = i;
                        break;
                    }
                }
                if (startLine >= 0) break;
            }

            if (startLine < 0) return null;

            if (IsTaskOutputComplete(lines, startLine))
                return null;

            var endLine = Math.Min(startLine + 10, lines.Length);
            var captured = new List<string>();
            for (int i = startLine; i < endLine; i++)
            {
                var line = lines[i].TrimEnd();
                if (string.IsNullOrEmpty(line) && captured.Count > 0 && string.IsNullOrEmpty(captured[^1]))
                    break;
                captured.Add(line);
            }

            while (captured.Count > 0 && string.IsNullOrWhiteSpace(captured[^1]))
                captured.RemoveAt(captured.Count - 1);

            if (captured.Count == 0) return null;
            var result = string.Join("\n", captured).Trim();
            return result.Length > 0 ? result : null;
        }


        // ── Result Verification ─────────────────────────────────────

        public async Task<ResultVerification?> VerifyResultAsync(
            string outputTail, string taskDescription, string? completionSummary,
            CancellationToken ct = default)
        {
            try
            {
                var contextTail = outputTail.Length > 2000 ? outputTail[^2000..] : outputTail;

                var summaryBlock = !string.IsNullOrWhiteSpace(completionSummary)
                    ? $"COMPLETION SUMMARY:\n{completionSummary}\n\n"
                    : "";

                var prompt =
                    "You are verifying an AI coding agent's work to confirm it actually accomplished the requested task.\n\n" +
                    $"TASK DESCRIPTION:\n{taskDescription}\n\n" +
                    $"AGENT'S FINAL OUTPUT (tail):\n{contextTail}\n\n" +
                    summaryBlock +
                    "QUESTION: Did the agent's work actually accomplish what was requested? Verify the result quality.\n\n" +
                    "Rules:\n" +
                    "- Check that the core requirements from the task description were addressed\n" +
                    "- If the agent made the requested changes and they appear correct → PASS\n" +
                    "- If the agent encountered errors, made incorrect changes, or missed key requirements → FAIL\n" +
                    "- If the task failed or was cancelled, verify whether any partial work was done correctly\n" +
                    "- Focus on correctness, not style or optional improvements\n\n" +
                    "Respond with EXACTLY one line in this format:\n" +
                    "PASS|<one-sentence summary of what was verified>\n" +
                    "or\n" +
                    "FAIL|<one-sentence description of what went wrong or was missed>\n\n" +
                    "Examples:\n" +
                    "PASS|Authentication endpoint added with proper JWT validation and error handling\n" +
                    "FAIL|The database migration was created but the API endpoint was not updated to use the new schema\n\n" +
                    "IMPORTANT: Output ONLY the single PASS or FAIL line. No explanation, preamble, or follow-up text.";

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "-p --output-format text --model claude-haiku-4-5-20251001 --max-turns 1 --append-system-prompt \"Respond with exactly one line. No additional text.\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");

                using var process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                var processedText = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                foreach (var line in processedText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("PASS|", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ResultVerification
                        {
                            Passed = true,
                            Summary = trimmed["PASS|".Length..].Trim()
                        };
                    }
                    if (trimmed.StartsWith("FAIL|", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ResultVerification
                        {
                            Passed = false,
                            Summary = trimmed["FAIL|".Length..].Trim()
                        };
                    }
                }

                AppLogger.Debug("CompletionAnalyzer", $"Could not parse result verification response: {processedText}");
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Result verification failed", ex);
                return null;
            }
        }

        // ── Completion Summary ──────────────────────────────────────

        public string FormatCompletionSummary(
            AgentTaskStatus status,
            TimeSpan duration,
            List<(string name, int added, int removed)>? fileChanges,
            long inputTokens = 0, long outputTokens = 0,
            long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine(" TASK COMPLETION SUMMARY");
            sb.AppendLine("───────────────────────────────────────────");
            sb.AppendLine($" Status: {status}");
            sb.AppendLine($" Duration: {(int)duration.TotalMinutes}m {duration.Seconds}s");
            if (inputTokens > 0 || outputTokens > 0)
            {
                var totalAll = inputTokens + outputTokens + cacheReadTokens + cacheCreationTokens;
                var cost = Helpers.FormatHelpers.EstimateCost(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
                var costStr = Helpers.FormatHelpers.FormatCost(cost);
                sb.AppendLine($" Tokens: {Helpers.FormatHelpers.FormatTokenCount(totalAll)} total (~{costStr})");
                sb.AppendLine($"         {Helpers.FormatHelpers.FormatTokenCount(inputTokens)} in / {Helpers.FormatHelpers.FormatTokenCount(outputTokens)} out");
                if (cacheReadTokens > 0 || cacheCreationTokens > 0)
                    sb.AppendLine($"         {Helpers.FormatHelpers.FormatTokenCount(cacheReadTokens)} cache read / {Helpers.FormatHelpers.FormatTokenCount(cacheCreationTokens)} cache created");
            }
            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }

        public string GenerateCompletionSummary(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration,
            long inputTokens = 0, long outputTokens = 0,
            long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            try
            {
                var fileChanges = _gitHelper.GetGitFileChanges(projectPath, gitStartHash);
                return FormatCompletionSummary(status, duration, fileChanges, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Failed to get git file changes for completion summary", ex);
                return FormatCompletionSummary(status, duration, null, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
            }
        }

        public async Task<string> GenerateCompletionSummaryAsync(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration,
            long inputTokens = 0, long outputTokens = 0,
            long cacheReadTokens = 0, long cacheCreationTokens = 0,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var fileChanges = await _gitHelper.GetGitFileChangesAsync(projectPath, gitStartHash, cancellationToken).ConfigureAwait(false);
                return FormatCompletionSummary(status, duration, fileChanges, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
            }
            catch (OperationCanceledException)
            {
                return FormatCompletionSummary(status, duration, null, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Failed to get git file changes for completion summary", ex);
                return FormatCompletionSummary(status, duration, null, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
            }
        }

        // ── Feature Mode & Token Limit ─────────────────────────────────

        public bool CheckFeatureModeComplete(string output)
        {
            var lines = output.Split('\n');
            var start = Math.Max(0, lines.Length - Constants.AppConstants.MaxOutputTailLines);
            for (var i = lines.Length - 1; i >= start; i--)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == "STATUS: COMPLETE") return true;
                if (trimmed == "STATUS: NEEDS_MORE_WORK") return false;
            }
            return false;
        }

        public bool IsTokenLimitError(string output)
        {
            var tail = output.Length > Constants.AppConstants.MaxOutputCharLength ? output[^Constants.AppConstants.MaxOutputCharLength..] : output;
            var lower = tail.ToLowerInvariant();
            return lower.Contains("rate limit") ||
                   lower.Contains("token limit") ||
                   lower.Contains("overloaded") ||
                   lower.Contains("529") ||
                   lower.Contains("capacity") ||
                   lower.Contains("too many requests");
        }
    }
}
