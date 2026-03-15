using System;

namespace Spritely.Models
{
    /// <summary>
    /// Represents a mutated prompt variant being A/B tested.
    /// Stored in SQLite at %LOCALAPPDATA%\Spritely\prompt_evolution.db.
    /// </summary>
    public class PromptVariant
    {
        public long Id { get; set; }
        public string VariantHash { get; set; } = "";
        public string ProjectPath { get; set; } = "";

        /// <summary>The prompt block name that was mutated (e.g. "DefaultSystemPrompt").</summary>
        public string BlockName { get; set; } = "";

        /// <summary>The original prompt text before mutation.</summary>
        public string OriginalText { get; set; } = "";

        /// <summary>The mutated prompt text to A/B test.</summary>
        public string MutatedText { get; set; } = "";

        /// <summary>Failure patterns that motivated this mutation.</summary>
        public string FailurePatterns { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Whether this variant is currently active for A/B testing.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Number of tasks that used this variant.</summary>
        public int TrialCount { get; set; }

        /// <summary>Number of successful tasks that used this variant.</summary>
        public int SuccessCount { get; set; }

        /// <summary>Number of tasks assigned to the control group (original prompt).</summary>
        public int ControlTrialCount { get; set; }

        /// <summary>Number of successful tasks in the control group.</summary>
        public int ControlSuccessCount { get; set; }

        public double SuccessRate => TrialCount > 0 ? (double)SuccessCount / TrialCount : 0;
        public double ControlSuccessRate => ControlTrialCount > 0 ? (double)ControlSuccessCount / ControlTrialCount : 0;

        /// <summary>Whether enough trials have completed to make a decision.</summary>
        public bool IsDecisionReady => TrialCount + ControlTrialCount >= 5;

        /// <summary>Whether the variant outperforms the control.</summary>
        public bool IsWinner => IsDecisionReady && SuccessRate > ControlSuccessRate;
    }
}
