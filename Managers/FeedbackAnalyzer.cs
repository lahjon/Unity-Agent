using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Analyzes accumulated feedback entries to discover patterns and generate insights.
    /// Uses both statistical analysis (cheap, always runs) and optional LLM analysis
    /// (Haiku-based, runs when enough data accumulates).
    /// </summary>
    public class FeedbackAnalyzer
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>JSON schema for the LLM feedback analysis response.</summary>
        private const string AnalysisJsonSchema =
            """{"type":"object","properties":{"success_patterns":{"type":"array","items":{"type":"object","properties":{"pattern":{"type":"string"},"confidence":{"type":"number"}},"required":["pattern","confidence"]}},"failure_patterns":{"type":"array","items":{"type":"object","properties":{"pattern":{"type":"string"},"confidence":{"type":"number"}},"required":["pattern","confidence"]}},"rule_additions":{"type":"array","items":{"type":"string"}},"rule_removals":{"type":"array","items":{"type":"string"}},"improvements":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"priority":{"type":"number"},"is_large":{"type":"boolean"}},"required":["title","description","category","priority","is_large"]}}},"required":["success_patterns","failure_patterns","rule_additions","rule_removals","improvements"]}""";

        /// <summary>
        /// Performs statistical analysis of feedback entries to produce an insight.
        /// This is the fast path — no LLM calls, always runs.
        /// </summary>
        public FeedbackInsight AnalyzeEntries(List<FeedbackEntry> entries, string projectPath)
        {
            var insight = new FeedbackInsight
            {
                ProjectPath = projectPath,
                TasksAnalyzed = entries.Count
            };

            if (entries.Count == 0) return insight;

            // Aggregate metrics
            var completedCount = entries.Count(e =>
                e.Status == "Completed" || e.Status == "Recommendation");
            insight.SuccessRate = (double)completedCount / entries.Count;
            insight.AverageDurationMinutes = entries.Average(e => e.DurationMinutes);
            insight.AverageCost = entries.Average(e => e.EstimatedCost);
            insight.AverageIterations = entries.Average(e => e.IterationCount);

            // Analyze success factor frequency
            var successFactorCounts = new Dictionary<string, int>();
            var failureFactorCounts = new Dictionary<string, int>();

            foreach (var entry in entries)
            {
                foreach (var factor in entry.SuccessFactors)
                    successFactorCounts[factor] = successFactorCounts.GetValueOrDefault(factor) + 1;
                foreach (var factor in entry.FailureFactors)
                    failureFactorCounts[factor] = failureFactorCounts.GetValueOrDefault(factor) + 1;
            }

            // Convert to patterns with confidence scores
            insight.SuccessPatterns = successFactorCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new PatternEntry
                {
                    Pattern = kv.Key,
                    Occurrences = kv.Value,
                    Confidence = (double)kv.Value / entries.Count
                }).ToList();

            insight.FailurePatterns = failureFactorCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new PatternEntry
                {
                    Pattern = kv.Key,
                    Occurrences = kv.Value,
                    Confidence = (double)kv.Value / entries.Count
                }).ToList();

            // Generate rule suggestions from statistical patterns
            GenerateStatisticalRuleSuggestions(insight, entries);

            // Generate improvement suggestions from failure patterns
            GenerateImprovementSuggestions(insight, entries);

            // Fire-and-forget LLM-based deep analysis if we have enough data
            if (entries.Count >= AppConstants.FeedbackLlmAnalysisThreshold)
                _ = EnhanceWithLlmAnalysisAsync(insight, entries);

            return insight;
        }

        private static void GenerateStatisticalRuleSuggestions(FeedbackInsight insight, List<FeedbackEntry> entries)
        {
            // If build failures are common, suggest a build-check rule
            var buildFailureRate = entries.Count(e => e.FailureFactors.Contains("build_failure"))
                / (double)entries.Count;
            if (buildFailureRate > 0.2)
            {
                insight.SuggestedRuleAdditions.Add(
                    "Always run `dotnet build` before marking a task complete — build failures detected in " +
                    $"{buildFailureRate:P0} of recent tasks");
            }

            // If verification frequently fails, suggest more thorough testing
            var verifyFailRate = entries.Count(e => e.FailureFactors.Contains("verification_failed"))
                / (double)entries.Count;
            if (verifyFailRate > 0.3)
            {
                insight.SuggestedRuleAdditions.Add(
                    "Include a verification step in task output — verification failures detected in " +
                    $"{verifyFailRate:P0} of recent tasks");
            }

            // If extended planning correlates with success
            var planningTasks = entries.Where(e => e.UsedExtendedPlanning).ToList();
            var nonPlanningTasks = entries.Where(e => !e.UsedExtendedPlanning).ToList();
            if (planningTasks.Count >= 3 && nonPlanningTasks.Count >= 3)
            {
                var planningSuccessRate = planningTasks.Count(e =>
                    e.Status == "Completed" || e.Status == "Recommendation") / (double)planningTasks.Count;
                var nonPlanningSuccessRate = nonPlanningTasks.Count(e =>
                    e.Status == "Completed" || e.Status == "Recommendation") / (double)nonPlanningTasks.Count;

                if (planningSuccessRate > nonPlanningSuccessRate + 0.2)
                {
                    insight.SuggestedRuleAdditions.Add(
                        $"Consider using Extended Planning for complex tasks — success rate is {planningSuccessRate:P0} " +
                        $"with planning vs {nonPlanningSuccessRate:P0} without");
                }
            }

            // If tasks are consistently running long, suggest timeout adjustment
            var longRunningRate = entries.Count(e => e.DurationMinutes > 60) / (double)entries.Count;
            if (longRunningRate > 0.3)
            {
                insight.SuggestedRuleAdditions.Add(
                    "Break large tasks into smaller subtasks — " +
                    $"{longRunningRate:P0} of tasks exceeded 60 minutes");
            }
        }

        private static void GenerateImprovementSuggestions(FeedbackInsight insight, List<FeedbackEntry> entries)
        {
            // High failure rate warrants investigation
            if (insight.SuccessRate < 0.5 && entries.Count >= 5)
            {
                insight.LargeImprovements.Add(new ImprovementSuggestion
                {
                    Title = "Investigate high task failure rate",
                    Description = $"Task success rate is only {insight.SuccessRate:P0} across the last " +
                        $"{entries.Count} tasks. Common failure factors: " +
                        string.Join(", ", insight.FailurePatterns.Take(3).Select(p => p.Pattern)),
                    Category = "reliability",
                    Priority = 0.9,
                    IsLargeChange = true
                });
            }

            // High cost tasks suggest optimization opportunity
            var highCostTasks = entries.Where(e => e.EstimatedCost > 1.0).ToList();
            if (highCostTasks.Count > entries.Count * 0.3)
            {
                insight.LargeImprovements.Add(new ImprovementSuggestion
                {
                    Title = "Optimize high-cost task patterns",
                    Description = $"{highCostTasks.Count} tasks exceeded $1.00 in estimated cost. " +
                        $"Average cost: ${insight.AverageCost:F2}. Consider context reduction or model downgrades for simpler tasks.",
                    Category = "cost-optimization",
                    Priority = 0.7,
                    IsLargeChange = false
                });
            }

            // Repeated test failures suggest missing test infrastructure
            var testFailures = entries.Count(e => e.FailureFactors.Contains("test_failure"));
            if (testFailures >= 3)
            {
                insight.LargeImprovements.Add(new ImprovementSuggestion
                {
                    Title = "Improve test reliability",
                    Description = $"{testFailures} tasks failed due to test failures. " +
                        "Consider adding test setup instructions to project rules or investigating flaky tests.",
                    Category = "testing",
                    Priority = 0.8,
                    IsLargeChange = true
                });
            }

            // No-change failures suggest prompt/context issues
            var noChangeTasks = entries.Count(e => e.FailureFactors.Contains("no_changes_made"));
            if (noChangeTasks >= 3)
            {
                insight.LargeImprovements.Add(new ImprovementSuggestion
                {
                    Title = "Improve task context for failed-to-start tasks",
                    Description = $"{noChangeTasks} tasks completed with no file changes. " +
                        "This suggests the task description or project context may be insufficient.",
                    Category = "prompt-quality",
                    Priority = 0.8,
                    IsLargeChange = true
                });
            }
        }

        /// <summary>
        /// Enhances an existing insight with LLM-based deep analysis using Haiku.
        /// Fire-and-forget — updates the insight in-place and re-saves.
        /// </summary>
        private static async Task EnhanceWithLlmAnalysisAsync(FeedbackInsight insight, List<FeedbackEntry> entries)
        {
            try
            {
                var prompt = BuildAnalysisPrompt(entries, insight);

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 1 " +
                                $"--json-schema \"{AnalysisJsonSchema.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

                using var process = new Process { StartInfo = psi };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                process.Start();

                await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);

                var processedText = FormatHelpers.StripAnsiCodes(output).Trim();
                ParseLlmResponse(processedText, insight);

                AppLogger.Info("FeedbackAnalyzer", $"LLM analysis complete: {insight.LargeImprovements.Count} improvements found");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeedbackAnalyzer", "LLM feedback analysis failed (non-critical)", ex);
            }
        }

        private static string BuildAnalysisPrompt(List<FeedbackEntry> entries, FeedbackInsight insight)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Analyze these AI coding task execution results and identify patterns for improvement.");
            sb.AppendLine();
            sb.AppendLine($"Project: {insight.ProjectPath}");
            sb.AppendLine($"Tasks analyzed: {entries.Count}");
            sb.AppendLine($"Success rate: {insight.SuccessRate:P0}");
            sb.AppendLine($"Average duration: {insight.AverageDurationMinutes:F1} minutes");
            sb.AppendLine($"Average cost: ${insight.AverageCost:F2}");
            sb.AppendLine();
            sb.AppendLine("## Task Results Summary");
            sb.AppendLine();

            foreach (var entry in entries.TakeLast(20))
            {
                sb.AppendLine($"- [{entry.Status}] {entry.Description}");
                sb.AppendLine($"  Duration: {entry.DurationMinutes:F1}min, Files: {entry.ChangedFileCount}, " +
                    $"Cost: ${entry.EstimatedCost:F2}");
                if (entry.SuccessFactors.Count > 0)
                    sb.AppendLine($"  Success factors: {string.Join(", ", entry.SuccessFactors)}");
                if (entry.FailureFactors.Count > 0)
                    sb.AppendLine($"  Failure factors: {string.Join(", ", entry.FailureFactors)}");
                if (!string.IsNullOrEmpty(entry.Recommendations))
                    sb.AppendLine($"  Recommendations: {entry.Recommendations[..Math.Min(200, entry.Recommendations.Length)]}");
                sb.AppendLine();
            }

            sb.AppendLine("## Statistical Patterns Already Detected");
            foreach (var p in insight.SuccessPatterns)
                sb.AppendLine($"- Success: {p.Pattern} ({p.Occurrences}x, {p.Confidence:P0})");
            foreach (var p in insight.FailurePatterns)
                sb.AppendLine($"- Failure: {p.Pattern} ({p.Occurrences}x, {p.Confidence:P0})");

            sb.AppendLine();
            sb.AppendLine("Based on this data, identify:");
            sb.AppendLine("1. Additional success/failure patterns not yet detected");
            sb.AppendLine("2. Specific project rules to add or remove");
            sb.AppendLine("3. Improvement suggestions (mark large architectural changes as is_large=true)");

            return sb.ToString();
        }

        private static void ParseLlmResponse(string responseText, FeedbackInsight insight)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                // Navigate to structured_output if present
                if (root.TryGetProperty("structured_output", out var structured)
                    && structured.ValueKind == JsonValueKind.Object)
                    root = structured;
                else if (root.TryGetProperty("result", out var resultEl) &&
                    resultEl.ValueKind == JsonValueKind.String)
                {
                    var resultStr = resultEl.GetString()!;
                    if (resultStr.TrimStart().StartsWith("{"))
                    {
                        using var innerDoc = JsonDocument.Parse(resultStr);
                        root = innerDoc.RootElement.Clone();
                    }
                }

                // Merge LLM patterns with statistical ones
                if (root.TryGetProperty("success_patterns", out var sp))
                {
                    foreach (var p in sp.EnumerateArray())
                    {
                        var pattern = p.GetProperty("pattern").GetString() ?? "";
                        var confidence = p.GetProperty("confidence").GetDouble();
                        if (!insight.SuccessPatterns.Any(x => x.Pattern == pattern))
                        {
                            insight.SuccessPatterns.Add(new PatternEntry
                            {
                                Pattern = pattern,
                                Confidence = confidence,
                                Occurrences = 0 // LLM-inferred
                            });
                        }
                    }
                }

                if (root.TryGetProperty("failure_patterns", out var fp))
                {
                    foreach (var p in fp.EnumerateArray())
                    {
                        var pattern = p.GetProperty("pattern").GetString() ?? "";
                        var confidence = p.GetProperty("confidence").GetDouble();
                        if (!insight.FailurePatterns.Any(x => x.Pattern == pattern))
                        {
                            insight.FailurePatterns.Add(new PatternEntry
                            {
                                Pattern = pattern,
                                Confidence = confidence,
                                Occurrences = 0
                            });
                        }
                    }
                }

                if (root.TryGetProperty("rule_additions", out var ra))
                {
                    foreach (var r in ra.EnumerateArray())
                    {
                        var rule = r.GetString();
                        if (!string.IsNullOrEmpty(rule) && !insight.SuggestedRuleAdditions.Contains(rule))
                            insight.SuggestedRuleAdditions.Add(rule);
                    }
                }

                if (root.TryGetProperty("rule_removals", out var rr))
                {
                    foreach (var r in rr.EnumerateArray())
                    {
                        var rule = r.GetString();
                        if (!string.IsNullOrEmpty(rule))
                            insight.SuggestedRuleRemovals.Add(rule);
                    }
                }

                if (root.TryGetProperty("improvements", out var impr))
                {
                    foreach (var i in impr.EnumerateArray())
                    {
                        var title = i.GetProperty("title").GetString() ?? "";
                        if (!insight.LargeImprovements.Any(x => x.Title == title))
                        {
                            insight.LargeImprovements.Add(new ImprovementSuggestion
                            {
                                Title = title,
                                Description = i.GetProperty("description").GetString() ?? "",
                                Category = i.GetProperty("category").GetString() ?? "",
                                Priority = i.GetProperty("priority").GetDouble(),
                                IsLargeChange = i.TryGetProperty("is_large", out var isLarge) && isLarge.GetBoolean()
                            });
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                AppLogger.Debug("FeedbackAnalyzer", $"Failed to parse LLM analysis response: {ex.Message}");
            }
        }
    }
}
