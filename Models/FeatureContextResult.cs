using System.Collections.Generic;

namespace Spritely.Models
{
    /// <summary>
    /// Result of matching a task prompt to the feature registry — contains the
    /// relevant features, an optional new-feature suggestion, and a pre-built
    /// context block ready for prompt injection.
    /// </summary>
    public class FeatureContextResult
    {
        /// <summary>Features that matched the task, ordered by confidence descending.</summary>
        public List<MatchedFeature> RelevantFeatures { get; set; } = new();

        /// <summary>Whether the task appears to introduce a brand-new feature.</summary>
        public bool IsNewFeature { get; set; }

        /// <summary>Suggested kebab-case id for the new feature, when <see cref="IsNewFeature"/> is true.</summary>
        public string? SuggestedNewFeatureId { get; set; }

        /// <summary>Suggested name for the new feature, when <see cref="IsNewFeature"/> is true.</summary>
        public string? SuggestedNewFeatureName { get; set; }

        /// <summary>Suggested keywords for the new feature, when <see cref="IsNewFeature"/> is true.</summary>
        public List<string>? SuggestedKeywords { get; set; }

        /// <summary>Pre-built markdown block for injection into the task prompt.</summary>
        public string ContextBlock { get; set; } = "";
    }

    /// <summary>
    /// A single feature match with a confidence score indicating how relevant
    /// the feature is to the current task.
    /// </summary>
    public class MatchedFeature
    {
        /// <summary>Kebab-case feature id.</summary>
        public string FeatureId { get; set; } = "";

        /// <summary>Human-readable feature name.</summary>
        public string FeatureName { get; set; } = "";

        /// <summary>Match confidence between 0.0 (no match) and 1.0 (perfect match).</summary>
        public double Confidence { get; set; }
    }
}
