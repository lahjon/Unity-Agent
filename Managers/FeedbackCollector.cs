using System;
using System.Collections.Generic;
using System.Linq;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Collects feedback data from completed tasks. Called fire-and-forget from
    /// the task completion flow, similar to FeatureUpdateAgent.
    /// After collecting, checks if enough entries have accumulated to trigger analysis.
    /// </summary>
    public class FeedbackCollector
    {
        private readonly FeedbackStore _store;
        private readonly FeedbackAnalyzer _analyzer;
        private readonly FeedbackApplicator _applicator;
        private readonly CrossProjectInsightsManager? _crossProjectInsights;

        public FeedbackCollector(FeedbackStore store, FeedbackAnalyzer analyzer, FeedbackApplicator applicator,
            CrossProjectInsightsManager? crossProjectInsights = null)
        {
            _store = store;
            _analyzer = analyzer;
            _applicator = applicator;
            _crossProjectInsights = crossProjectInsights;
        }

        /// <summary>
        /// Collects feedback from a completed task and persists it.
        /// If enough entries have accumulated since the last analysis, triggers a new analysis cycle.
        /// </summary>
        public void CollectAndAnalyze(AgentTask task)
        {
            try
            {
                var entry = BuildFeedbackEntry(task);
                _store.SaveEntry(entry);

                // Check if we should trigger analysis
                var sinceLastInsight = _store.GetEntriesSinceLastInsight(task.ProjectPath);
                if (sinceLastInsight >= AppConstants.FeedbackAnalysisThreshold)
                {
                    var entries = _store.LoadEntries(task.ProjectPath);
                    var recentEntries = entries
                        .OrderByDescending(e => e.Timestamp)
                        .Take(AppConstants.FeedbackAnalysisWindow)
                        .ToList();

                    var insight = _analyzer.AnalyzeEntries(recentEntries, task.ProjectPath);
                    _store.SaveInsight(insight);

                    // Apply low-risk improvements automatically
                    _applicator.ApplyInsight(insight);

                    // Contribute anonymized patterns to cross-project shared store
                    _crossProjectInsights?.ContributeInsights(insight);

                    AppLogger.Info("FeedbackCollector",
                        $"Feedback analysis completed for {task.ProjectDisplayName}: " +
                        $"{insight.SuccessRate:P0} success rate, {insight.SuggestedRuleAdditions.Count} rule suggestions, " +
                        $"{insight.LargeImprovements.Count} improvement suggestions");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackCollector", "Failed to collect/analyze feedback", ex);
            }
        }

        private static FeedbackEntry BuildFeedbackEntry(AgentTask task)
        {
            var duration = task.EndTime.HasValue
                ? (task.EndTime.Value - task.StartTime).TotalMinutes
                : 0;

            var entry = new FeedbackEntry
            {
                TaskId = task.Id,
                ProjectPath = task.ProjectPath,
                Description = task.Description,
                Timestamp = DateTime.Now,
                Status = task.Status.ToString(),
                VerificationPassed = task.IsVerified && !string.IsNullOrEmpty(task.VerificationResult)
                    && task.VerificationResult.Contains("PASS", StringComparison.OrdinalIgnoreCase),
                VerificationSummary = task.VerificationResult,
                Recommendations = task.Recommendations ?? "",
                CompletionSummary = task.CompletionSummary ?? "",
                DurationMinutes = duration,
                InputTokens = task.InputTokens,
                OutputTokens = task.OutputTokens,
                CacheReadTokens = task.CacheReadTokens,
                CacheCreationTokens = task.CacheCreationTokens,
                ChangedFileCount = task.ChangedFiles?.Count ?? 0,
                IterationCount = task.CurrentIteration,
                WasTeamsMode = task.IsTeamsMode,
                UsedMcp = task.UseMcp,
                UsedExtendedPlanning = task.ExtendedPlanning,
                UsedAutoDecompose = task.AutoDecompose,
                Model = task.Model.ToString()
            };

            // Populate feature context tracking from runtime context
            if (task.Runtime != null)
            {
                if (task.Runtime.InjectedFeatureIds?.Count > 0)
                    entry.InjectedFeatureIds = new List<string>(task.Runtime.InjectedFeatureIds);
                entry.TaskCategory = task.Runtime.TaskCategory;
                entry.ContextTokensUsed = task.Runtime.ContextTokensUsed;
            }

            // Quick local analysis of success/failure factors
            AnalyzeFactors(entry, task);

            return entry;
        }

        private static void AnalyzeFactors(FeedbackEntry entry, AgentTask task)
        {
            var isSuccess = task.Status == AgentTaskStatus.Completed
                         || task.Status == AgentTaskStatus.Recommendation;

            if (isSuccess)
            {
                if (entry.DurationMinutes < 5)
                    entry.SuccessFactors.Add("fast_completion");
                if (entry.ChangedFileCount > 0 && entry.ChangedFileCount <= 5)
                    entry.SuccessFactors.Add("focused_changes");
                if (entry.VerificationPassed)
                    entry.SuccessFactors.Add("verification_passed");
                if (entry.WasTeamsMode && entry.IterationCount <= 2)
                    entry.SuccessFactors.Add("efficient_teams_mode");
                if (entry.UsedExtendedPlanning)
                    entry.SuccessFactors.Add("extended_planning_helped");
            }
            else
            {
                if (entry.DurationMinutes > 60)
                    entry.FailureFactors.Add("long_running");
                if (entry.ChangedFileCount == 0)
                    entry.FailureFactors.Add("no_changes_made");
                if (!entry.VerificationPassed && !string.IsNullOrEmpty(entry.VerificationSummary))
                    entry.FailureFactors.Add("verification_failed");
                if (entry.IterationCount > 3)
                    entry.FailureFactors.Add("many_iterations");

                // Check output for common failure patterns
                var output = task.FullOutput ?? "";
                if (output.Contains("build failed", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("compilation error", StringComparison.OrdinalIgnoreCase))
                    entry.FailureFactors.Add("build_failure");
                if (output.Contains("test failed", StringComparison.OrdinalIgnoreCase))
                    entry.FailureFactors.Add("test_failure");
                if (output.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
                    entry.FailureFactors.Add("permission_issue");
            }
        }
    }
}
