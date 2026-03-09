using System;
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

        /// <summary>
        /// Hash of all tracked file hashes — used to detect when the symbol index is stale
        /// and must be rebuilt. Computed after each full or incremental symbol index build.
        /// </summary>
        public string? SymbolIndexVersion { get; set; }

        /// <summary>Module membership info carried inline so loaders need only one file read.</summary>
        public List<ModuleIndexEntry> Modules { get; set; } = new();

        /// <summary>
        /// Inverted keyword map: token → list of feature IDs containing that token.
        /// Built from each feature's keywords, name, and description during index save.
        /// Enables O(1) candidate lookup during feature matching.
        /// </summary>
        public Dictionary<string, List<string>> KeywordMap { get; set; } = new();
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

        /// <summary>Broad category (Core, UI, Integration, etc.) copied from the feature entry.</summary>
        public string Category { get; set; } = "";

        /// <summary>Number of times this feature has been touched by tasks.</summary>
        public int TouchCount { get; set; }

        /// <summary>Number of primary files in this feature.</summary>
        public int PrimaryFileCount { get; set; }

        /// <summary>UTC timestamp of when this feature's signatures were last indexed/refreshed.</summary>
        public DateTime? LastIndexedAt { get; set; }

        /// <summary>Keywords copied from the feature entry, used for keyword map building.</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Short description copied from the feature entry, used for keyword map building.</summary>
        public string Description { get; set; } = "";
    }

}
