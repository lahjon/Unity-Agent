using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
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

                // Skip STATUS marker lines and prompt-echo boilerplate - they aren't recommendation headers
                if (lower.StartsWith("status:")) continue;
                if (lower.Contains("status:") && lower.Contains("complete")) continue;
                if (lower.Contains("message bus") || lower.Contains("feature log") || lower.Contains("machine-parsed")) continue;

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

            // If the task explicitly declared COMPLETE WITH RECOMMENDATIONS, always extract
            bool hasExplicitRecommendationStatus = false;
            for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - AppConstants.MaxOutputTailLines); i--)
            {
                if (lines[i].Trim() == "STATUS: COMPLETE WITH RECOMMENDATIONS")
                {
                    hasExplicitRecommendationStatus = true;
                    break;
                }
            }

            if (!hasExplicitRecommendationStatus && IsTaskOutputComplete(lines, startLine))
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
            if (result.Length == 0) return null;

            // Reject if only the header line was captured with no actual recommendation items
            if (captured.Count <= 1) return null;

            return result;
        }


        // ── Result Verification ─────────────────────────────────────

        /// <summary>JSON schema for structured result verification output.</summary>
        private const string VerificationJsonSchema =
            """{"type":"object","properties":{"result":{"type":"string","enum":["PASS","FAIL"]},"summary":{"type":"string"},"next_steps":{"type":"string"}},"required":["result","summary","next_steps"]}""";

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

                var prompt = string.Format(PromptBuilder.ResultVerificationPromptTemplate,
                    taskDescription, contextTail, summaryBlock);

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 1 --output-schema '{VerificationJsonSchema}'",
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

                // Try structured JSON parse first (constrained output mode)
                try
                {
                    using var doc = JsonDocument.Parse(processedText);
                    var wrapper = doc.RootElement;

                    // The CLI with --output-format json wraps the result; extract the "result" field
                    JsonElement root;
                    if (wrapper.TryGetProperty("result", out var resultElement) &&
                        resultElement.ValueKind == JsonValueKind.String)
                    {
                        var resultStr = resultElement.GetString()!;
                        // Check if the result is a JSON object (schema output) vs a plain string
                        if (resultStr.TrimStart().StartsWith("{"))
                        {
                            using var innerDoc = JsonDocument.Parse(resultStr);
                            root = innerDoc.RootElement.Clone();
                        }
                        else
                        {
                            root = wrapper;
                        }
                    }
                    else
                    {
                        root = wrapper;
                    }

                    var result = root.GetProperty("result").GetString() ?? "";
                    var summary = root.GetProperty("summary").GetString() ?? "";
                    var nextSteps = root.TryGetProperty("next_steps", out var ns) ? ns.GetString() ?? "" : "";
                    // Normalize "none" to empty
                    if (nextSteps.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                        nextSteps.Equals("n/a", StringComparison.OrdinalIgnoreCase))
                        nextSteps = "";
                    return new ResultVerification
                    {
                        Passed = result.Equals("PASS", StringComparison.OrdinalIgnoreCase),
                        Summary = summary,
                        NextSteps = nextSteps
                    };
                }
                catch (JsonException)
                {
                    // Fallback: parse legacy PASS|/FAIL| text format
                    foreach (var line in processedText.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("PASS|", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed["PASS|".Length..].Split('|', 2);
                            var nextStepsFallback = parts.Length > 1 ? parts[1].Trim() : "";
                            if (nextStepsFallback.Equals("none", StringComparison.OrdinalIgnoreCase))
                                nextStepsFallback = "";
                            return new ResultVerification
                            {
                                Passed = true,
                                Summary = parts[0].Trim(),
                                NextSteps = nextStepsFallback
                            };
                        }
                        if (trimmed.StartsWith("FAIL|", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed["FAIL|".Length..].Split('|', 2);
                            var nextStepsFallback = parts.Length > 1 ? parts[1].Trim() : "";
                            if (nextStepsFallback.Equals("none", StringComparison.OrdinalIgnoreCase))
                                nextStepsFallback = "";
                            return new ResultVerification
                            {
                                Passed = false,
                                Summary = parts[0].Trim(),
                                NextSteps = nextStepsFallback
                            };
                        }
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
            // Status is already shown in the task tab header — don't duplicate it in the output
            return "";
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
                if (trimmed == "STATUS: COMPLETE" || trimmed == "STATUS: COMPLETE WITH RECOMMENDATIONS") return true;
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
