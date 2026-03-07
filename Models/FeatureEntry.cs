using System;
using System.Collections.Generic;

namespace Spritely.Models
{
    /// <summary>
    /// Represents a single project feature stored as one JSON file in the
    /// <c>.spritely/features/</c> directory. The <see cref="Id"/> doubles as
    /// the filename (kebab-case slug + ".json").
    /// </summary>
    public class FeatureEntry
    {
        /// <summary>Kebab-case slug used as both the unique key and the filename.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>One-to-three sentence summary of what this feature does.</summary>
        public string Description { get; set; } = "";

        /// <summary>Broad category (Core, UI, Integration, Model, etc.).</summary>
        public string Category { get; set; } = "";

        /// <summary>Searchable terms for matching tasks to this feature (always sorted).</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Relative paths to the files most central to this feature (sorted).</summary>
        public List<string> PrimaryFiles { get; set; } = new();

        /// <summary>Relative paths to files that support this feature but are not central (sorted).</summary>
        public List<string> SecondaryFiles { get; set; } = new();

        /// <summary>Ids of features that are closely related to this one (sorted).</summary>
        public List<string> RelatedFeatureIds { get; set; } = new();

        /// <summary>Hierarchical code context — signatures, types, patterns, and dependencies.</summary>
        public FeatureContext Context { get; set; } = new();

        /// <summary>Number of times this feature has been touched by tasks.</summary>
        public int TouchCount { get; set; }

        /// <summary>When this feature entry was last updated.</summary>
        public DateTime LastUpdatedAt { get; set; }

        /// <summary>The task that last updated this entry, if known.</summary>
        public string? LastUpdatedByTaskId { get; set; }
    }

    /// <summary>
    /// Hierarchical code context for a feature — compact signatures, key types,
    /// architectural patterns, and cross-feature dependencies.
    /// </summary>
    public class FeatureContext
    {
        /// <summary>Relative file path to extracted signature for that file.</summary>
        public Dictionary<string, FileSignature> Signatures { get; set; } = new();

        /// <summary>Type definitions expressed as compact strings (e.g. "class Foo : IBar").</summary>
        public List<string> KeyTypes { get; set; } = new();

        /// <summary>Architectural patterns or invariants relevant to this feature.</summary>
        public List<string> Patterns { get; set; } = new();

        /// <summary>Cross-feature relationships expressed as freeform strings.</summary>
        public List<string> Dependencies { get; set; } = new();
    }

    /// <summary>
    /// Extracted code signature for a single file — a short content hash for
    /// staleness detection plus compact class/method signature text.
    /// </summary>
    public class FileSignature
    {
        /// <summary>Short hex hash (truncated SHA-256) of the file content for staleness checks.</summary>
        public string Hash { get; set; } = "";

        /// <summary>Class and method signatures as compact text.</summary>
        public string Content { get; set; } = "";
    }
}
