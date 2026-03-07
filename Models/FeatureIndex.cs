using System.Collections.Generic;

namespace Spritely.Models
{
    /// <summary>
    /// The <c>_index.json</c> manifest that lists all features in a project's
    /// <c>.spritely/features/</c> directory. Kept lightweight for fast reads.
    /// </summary>
    public class FeatureIndex
    {
        /// <summary>Schema version — bump when the format changes.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Ordered list of every feature registered in this project.</summary>
        public List<FeatureIndexEntry> Features { get; set; } = new();
    }

    /// <summary>
    /// One entry in the feature index — just enough to identify the feature
    /// without loading its full JSON file.
    /// </summary>
    public class FeatureIndexEntry
    {
        /// <summary>Kebab-case feature id (matches the JSON filename).</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name.</summary>
        public string Name { get; set; } = "";
    }
}
