using System;
using System.Collections.Generic;
using System.Linq;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Applies low-risk improvements automatically based on feedback insights.
    /// Currently supports: adding project rules from high-confidence suggestions.
    /// Large improvements are delegated to ImprovementTaskGenerator.
    /// </summary>
    public class FeedbackApplicator
    {
        private readonly Func<string, ProjectEntry?> _getProjectEntry;
        private readonly Action _saveProjects;
        private readonly ProjectRulesManager? _rulesManager;
        private readonly ImprovementTaskGenerator _taskGenerator;

        /// <summary>Minimum confidence required to auto-apply a rule suggestion.</summary>
        private const double AutoApplyConfidence = 0.6;

        /// <summary>Maximum rules to auto-add per analysis cycle to prevent rule bloat.</summary>
        private const int MaxAutoAddRules = 2;

        /// <summary>
        /// Raised when a rule is automatically added to a project.
        /// Parameters: projectPath, rule text.
        /// </summary>
        public event Action<string, string>? RuleAutoAdded;

        /// <summary>
        /// Raised when an improvement task is generated.
        /// Parameters: title, description.
        /// </summary>
        public event Action<string, string>? ImprovementTaskSuggested;

        public FeedbackApplicator(
            Func<string, ProjectEntry?> getProjectEntry,
            Action saveProjects,
            ProjectRulesManager? rulesManager,
            ImprovementTaskGenerator taskGenerator)
        {
            _getProjectEntry = getProjectEntry;
            _saveProjects = saveProjects;
            _rulesManager = rulesManager;
            _taskGenerator = taskGenerator;
        }

        /// <summary>
        /// Applies an insight: auto-adds high-confidence rules, generates improvement tasks for large changes.
        /// </summary>
        public void ApplyInsight(FeedbackInsight insight)
        {
            try
            {
                ApplyRuleSuggestions(insight);
                GenerateImprovementTasks(insight);
                insight.AutoApplied = true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackApplicator", "Failed to apply feedback insight", ex);
            }
        }

        private void ApplyRuleSuggestions(FeedbackInsight insight)
        {
            if (insight.SuggestedRuleAdditions.Count == 0) return;

            var entry = _getProjectEntry(insight.ProjectPath);
            if (entry == null) return;

            var rulesAdded = 0;
            foreach (var suggestion in insight.SuggestedRuleAdditions)
            {
                if (rulesAdded >= MaxAutoAddRules) break;

                // Skip if a similar rule already exists
                if (entry.ProjectRules.Any(r =>
                    r.Contains(suggestion[..Math.Min(40, suggestion.Length)], StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Tag auto-added rules so they can be identified later
                var taggedRule = $"[auto-feedback] {suggestion}";
                entry.ProjectRules.Add(taggedRule);
                rulesAdded++;

                AppLogger.Info("FeedbackApplicator",
                    $"Auto-added rule to {entry.DisplayName}: {suggestion}");
                RuleAutoAdded?.Invoke(insight.ProjectPath, taggedRule);
            }

            if (rulesAdded > 0)
            {
                _saveProjects();
                _rulesManager?.NotifyRulesChanged(entry);
            }
        }

        private void GenerateImprovementTasks(FeedbackInsight insight)
        {
            var largeImprovements = insight.LargeImprovements
                .Where(i => i.IsLargeChange && i.Priority >= 0.7)
                .OrderByDescending(i => i.Priority)
                .Take(3)
                .ToList();

            if (largeImprovements.Count == 0) return;

            foreach (var improvement in largeImprovements)
            {
                _taskGenerator.QueueSuggestion(insight.ProjectPath, improvement);
                ImprovementTaskSuggested?.Invoke(improvement.Title, improvement.Description);
            }

            insight.TasksGenerated = true;
        }
    }
}
