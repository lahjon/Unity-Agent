using System;
using System.Collections.Generic;

namespace Spritely.Models
{
    /// <summary>
    /// Captures feedback from a single task execution for the self-improving feedback loop.
    /// Stored per-project in %LOCALAPPDATA%\Spritely\feedback\.
    /// </summary>
    public class FeedbackEntry
    {
        public string TaskId { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // Outcome
        public string Status { get; set; } = "";
        public bool VerificationPassed { get; set; }
        public string VerificationSummary { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public string CompletionSummary { get; set; } = "";

        // Metrics
        public double DurationMinutes { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public int ChangedFileCount { get; set; }
        public int IterationCount { get; set; }

        // Task configuration that led to this outcome
        public bool WasFeatureMode { get; set; }
        public bool UsedMcp { get; set; }
        public bool UsedExtendedPlanning { get; set; }
        public bool UsedAutoDecompose { get; set; }
        public string Model { get; set; } = "";

        // Derived analysis (populated by FeedbackAnalyzer)
        public List<string> SuccessFactors { get; set; } = new();
        public List<string> FailureFactors { get; set; } = new();
        public string AnalysisSummary { get; set; } = "";

        /// <summary>Estimated cost in USD based on token usage.</summary>
        public double EstimatedCost =>
            (InputTokens * 15.0 / 1_000_000) +
            (OutputTokens * 75.0 / 1_000_000) +
            (CacheReadTokens * 1.5 / 1_000_000) +
            (CacheCreationTokens * 18.75 / 1_000_000);
    }

    /// <summary>
    /// Aggregated insight derived from analyzing multiple FeedbackEntry records.
    /// Used by FeedbackApplicator to decide what improvements to apply.
    /// </summary>
    public class FeedbackInsight
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public string ProjectPath { get; set; } = "";
        public int TasksAnalyzed { get; set; }

        // Aggregate metrics
        public double SuccessRate { get; set; }
        public double AverageDurationMinutes { get; set; }
        public double AverageCost { get; set; }
        public double AverageIterations { get; set; }

        // Patterns discovered
        public List<PatternEntry> SuccessPatterns { get; set; } = new();
        public List<PatternEntry> FailurePatterns { get; set; } = new();
        public List<string> SuggestedRuleAdditions { get; set; } = new();
        public List<string> SuggestedRuleRemovals { get; set; } = new();
        public List<ImprovementSuggestion> LargeImprovements { get; set; } = new();

        // Whether auto-apply has been run for this insight
        public bool AutoApplied { get; set; }
        public bool TasksGenerated { get; set; }
    }

    public class PatternEntry
    {
        public string Pattern { get; set; } = "";
        public int Occurrences { get; set; }
        public double Confidence { get; set; }
    }

    public class ImprovementSuggestion
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public double Priority { get; set; }
        public bool IsLargeChange { get; set; }
    }
}
