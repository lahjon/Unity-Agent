using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
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

        // ── LLM-Based Continue Verification ─────────────────────────

        public async Task<ContinueVerification?> VerifyContinueNeededAsync(
            string outputTail, string? recommendations, string taskDescription,
            CancellationToken ct = default)
        {
            try
            {
                var contextTail = outputTail.Length > 2000 ? outputTail[^2000..] : outputTail;

                var hasRecommendations = !string.IsNullOrWhiteSpace(recommendations);
                var recommendationsBlock = hasRecommendations
                    ? $"EXTRACTED RECOMMENDATIONS:\n{recommendations}\n\n"
                    : "";

                var prompt =
                    "You are analyzing an AI coding agent's output to determine if the task needs more work.\n\n" +
                    $"TASK DESCRIPTION:\n{taskDescription}\n\n" +
                    $"AGENT'S FINAL OUTPUT (tail):\n{contextTail}\n\n" +
                    recommendationsBlock +
                    "QUESTION: Did the agent complete the core task, or is there genuinely unfinished work?\n\n" +
                    "Rules:\n" +
                    "- If the agent completed what was asked and any mentions of next steps are just optional improvements, " +
                    "nice-to-haves, testing suggestions, or future enhancements → COMPLETE\n" +
                    "- If the agent explicitly states it couldn't finish something, hit errors, left work undone, " +
                    "or is requesting more information from the user → INCOMPLETE\n" +
                    "- If the agent is asking questions or waiting for user input/clarification → INCOMPLETE\n" +
                    "- Suggestions like \"add tests\", \"consider adding\", \"you might want to\" are optional → COMPLETE\n" +
                    "- Items like \"still need to implement X\", \"couldn't fix Y\", \"remaining: Z\" are unfinished → INCOMPLETE\n\n" +
                    "Respond with EXACTLY one line in this format:\n" +
                    "COMPLETE|<one-sentence summary of what was done>\n" +
                    "or\n" +
                    "INCOMPLETE|<one-sentence description of what still needs to be done or what info is being requested>\n\n" +
                    "Examples:\n" +
                    "COMPLETE|All requested changes implemented successfully\n" +
                    "INCOMPLETE|Error handling for the API endpoint was not implemented due to build errors\n" +
                    "INCOMPLETE|Agent is asking which database driver to use for the connection layer";

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "-p --output-format text --model claude-haiku-4-5-20251001 --max-turns 1",
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
                    if (trimmed.StartsWith("COMPLETE|", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ContinueVerification
                        {
                            ShouldContinue = false,
                            Reason = trimmed["COMPLETE|".Length..].Trim()
                        };
                    }
                    if (trimmed.StartsWith("INCOMPLETE|", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ContinueVerification
                        {
                            ShouldContinue = true,
                            Reason = trimmed["INCOMPLETE|".Length..].Trim()
                        };
                    }
                }

                AppLogger.Debug("CompletionAnalyzer", $"Could not parse continue verification response: {processedText}");
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Continue verification failed", ex);
                return null;
            }
        }

        // ── Completion Summary ──────────────────────────────────────

        public string FormatCompletionSummary(
            AgentTaskStatus status,
            TimeSpan duration,
            List<(string name, int added, int removed)>? fileChanges)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine(" TASK COMPLETION SUMMARY");
            sb.AppendLine("───────────────────────────────────────────");
            sb.AppendLine($" Status: {status}");
            sb.AppendLine($" Duration: {(int)duration.TotalMinutes}m {duration.Seconds}s");

            if (fileChanges != null)
            {
                var totalAdded = 0;
                var totalRemoved = 0;
                foreach (var (_, added, removed) in fileChanges)
                {
                    totalAdded += added;
                    totalRemoved += removed;
                }

                sb.AppendLine($" Files modified: {fileChanges.Count}");
                sb.AppendLine($" Lines changed: +{totalAdded} / -{totalRemoved}");

                if (fileChanges.Count > 0)
                {
                    sb.AppendLine("───────────────────────────────────────────");
                    sb.AppendLine(" Modified files:");
                    foreach (var (name, added, removed) in fileChanges)
                        sb.AppendLine($"   {name} | +{added} -{removed}");
                }
            }
            else
            {
                sb.AppendLine(" Files modified: (git not available)");
            }

            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }

        public string GenerateCompletionSummary(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration)
        {
            try
            {
                var fileChanges = _gitHelper.GetGitFileChanges(projectPath, gitStartHash);
                return FormatCompletionSummary(status, duration, fileChanges);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Failed to get git file changes for completion summary", ex);
                return FormatCompletionSummary(status, duration, null);
            }
        }

        public async Task<string> GenerateCompletionSummaryAsync(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var fileChanges = await _gitHelper.GetGitFileChangesAsync(projectPath, gitStartHash, cancellationToken).ConfigureAwait(false);
                return FormatCompletionSummary(status, duration, fileChanges);
            }
            catch (OperationCanceledException)
            {
                return FormatCompletionSummary(status, duration, null);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CompletionAnalyzer", "Failed to get git file changes for completion summary", ex);
                return FormatCompletionSummary(status, duration, null);
            }
        }

        // ── Overnight & Token Limit ─────────────────────────────────

        public bool CheckOvernightComplete(string output)
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
