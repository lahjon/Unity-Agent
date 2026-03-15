using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Aggregates anonymized feedback patterns across all registered projects into a shared
    /// insights store. Contributes after per-project analysis completes, and provides
    /// cross-project hints for prompt injection.
    /// </summary>
    public class CrossProjectInsightsManager
    {
        private readonly string _insightsFile;
        private readonly object _lock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Maximum rules to keep in the global store.</summary>
        private const int MaxGlobalRules = 50;

        /// <summary>Minimum confidence to include a pattern as a global rule.</summary>
        private const double MinConfidence = 0.4;

        /// <summary>Maximum number of cross-project hints to inject into a prompt.</summary>
        private const int MaxHints = 3;

        public CrossProjectInsightsManager(string appDataDir)
        {
            _insightsFile = Path.Combine(appDataDir, "cross_project_insights.json");
        }

        /// <summary>
        /// Contributes anonymized patterns from a per-project FeedbackInsight into the shared store.
        /// Called after FeedbackAnalyzer completes per-project analysis.
        /// Only rule/outcome signals are stored — no file paths or project-specific data.
        /// </summary>
        public void ContributeInsights(FeedbackInsight insight)
        {
            try
            {
                lock (_lock)
                {
                    var store = LoadStore();

                    // Contribute success patterns as global rules
                    foreach (var pattern in insight.SuccessPatterns.Where(p => p.Confidence >= MinConfidence))
                    {
                        var existing = store.GlobalRules.FirstOrDefault(r =>
                            r.Rule.Equals(pattern.Pattern, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.ProjectCount++;
                            existing.TotalOccurrences += pattern.Occurrences;
                            existing.AverageConfidence = (existing.AverageConfidence + pattern.Confidence) / 2.0;
                            existing.LastSeen = DateTime.Now;
                        }
                        else
                        {
                            store.GlobalRules.Add(new CrossProjectRule
                            {
                                Rule = pattern.Pattern,
                                Source = "success_pattern",
                                ProjectCount = 1,
                                TotalOccurrences = pattern.Occurrences,
                                AverageConfidence = pattern.Confidence,
                                LastSeen = DateTime.Now
                            });
                        }
                    }

                    // Contribute suggested rule additions (already anonymized text)
                    foreach (var rule in insight.SuggestedRuleAdditions)
                    {
                        var existing = store.GlobalRules.FirstOrDefault(r =>
                            r.Rule.Equals(rule, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            existing.ProjectCount++;
                            existing.LastSeen = DateTime.Now;
                        }
                        else
                        {
                            store.GlobalRules.Add(new CrossProjectRule
                            {
                                Rule = rule,
                                Source = "rule_suggestion",
                                ProjectCount = 1,
                                TotalOccurrences = 1,
                                AverageConfidence = 0.5,
                                LastSeen = DateTime.Now
                            });
                        }
                    }

                    // Record aggregate outcome signal
                    store.ContributionCount++;
                    store.LastContribution = DateTime.Now;
                    store.AggregateSuccessRate = store.ContributionCount == 1
                        ? insight.SuccessRate
                        : (store.AggregateSuccessRate * (store.ContributionCount - 1) + insight.SuccessRate) / store.ContributionCount;

                    // Prune to keep only top rules by score
                    if (store.GlobalRules.Count > MaxGlobalRules)
                    {
                        store.GlobalRules = store.GlobalRules
                            .OrderByDescending(r => r.Score)
                            .Take(MaxGlobalRules)
                            .ToList();
                    }

                    SaveStore(store);
                }

                AppLogger.Info("CrossProjectInsights",
                    $"Contributed insights from project analysis ({insight.TasksAnalyzed} tasks analyzed)");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CrossProjectInsights", "Failed to contribute insights (non-critical)", ex);
            }
        }

        /// <summary>
        /// Returns a prompt block containing the top globally-successful rules to inject.
        /// Returns empty string if no useful cross-project insights exist.
        /// </summary>
        public string GetCrossProjectHints()
        {
            try
            {
                CrossProjectInsightsStore store;
                lock (_lock)
                {
                    store = LoadStore();
                }

                if (store.GlobalRules.Count == 0)
                    return "";

                // Pick top rules seen across multiple projects with decent confidence
                var topRules = store.GlobalRules
                    .Where(r => r.ProjectCount >= 2 || r.AverageConfidence >= 0.6)
                    .OrderByDescending(r => r.Score)
                    .Take(MaxHints)
                    .ToList();

                if (topRules.Count == 0)
                    return "";

                var lines = new List<string>
                {
                    "# CROSS-PROJECT INSIGHTS",
                    "These rules have been successful across multiple projects:"
                };

                foreach (var rule in topRules)
                    lines.Add($"- {rule.Rule}");

                lines.Add("");
                return string.Join("\n", lines) + "\n";
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CrossProjectInsights", "Failed to get cross-project hints", ex);
                return "";
            }
        }

        private CrossProjectInsightsStore LoadStore()
        {
            if (!File.Exists(_insightsFile))
                return new CrossProjectInsightsStore();

            try
            {
                var json = File.ReadAllText(_insightsFile);
                return JsonSerializer.Deserialize<CrossProjectInsightsStore>(json, JsonOptions)
                       ?? new CrossProjectInsightsStore();
            }
            catch
            {
                return new CrossProjectInsightsStore();
            }
        }

        private void SaveStore(CrossProjectInsightsStore store)
        {
            var json = JsonSerializer.Serialize(store, JsonOptions);
            SafeFileWriter.WriteInBackground(_insightsFile, json, "CrossProjectInsights");
        }
    }

    /// <summary>Persisted store for cross-project insights.</summary>
    public class CrossProjectInsightsStore
    {
        public int ContributionCount { get; set; }
        public DateTime LastContribution { get; set; }
        public double AggregateSuccessRate { get; set; }
        public List<CrossProjectRule> GlobalRules { get; set; } = new();
    }

    /// <summary>An anonymized rule/pattern that has proven successful across projects.</summary>
    public class CrossProjectRule
    {
        public string Rule { get; set; } = "";
        public string Source { get; set; } = "";
        public int ProjectCount { get; set; }
        public int TotalOccurrences { get; set; }
        public double AverageConfidence { get; set; }
        public DateTime LastSeen { get; set; }

        /// <summary>Composite score for ranking: weighs confidence, cross-project spread, and recency.</summary>
        public double Score =>
            AverageConfidence * 0.4
            + Math.Min(ProjectCount / 5.0, 1.0) * 0.4
            + Math.Max(0, 1.0 - (DateTime.Now - LastSeen).TotalDays / 90.0) * 0.2;
    }
}
