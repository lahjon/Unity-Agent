using System;
using System.Collections.Generic;

namespace Spritely.Models
{
    /// <summary>
    /// A high-level grouping of related features. Stored as one JSON file per module
    /// in <c>.spritely/features/</c> (e.g. <c>core.module.json</c>).
    /// </summary>
    public class ModuleEntry
    {
        /// <summary>Kebab-case slug used as the unique key and filename prefix.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name (e.g. "Task Orchestration").</summary>
        public string Name { get; set; } = "";

        /// <summary>One-to-three sentence summary of what this module encompasses.</summary>
        public string Description { get; set; } = "";

        /// <summary>Broad category (Core, UI, Integration, etc.).</summary>
        public string Category { get; set; } = "";

        /// <summary>Feature IDs that belong to this module (sorted).</summary>
        public List<string> FeatureIds { get; set; } = new();

        /// <summary>Searchable terms for matching tasks to this module (sorted).</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>When this module entry was last updated.</summary>
        public DateTime LastUpdatedAt { get; set; }
    }

    /// <summary>
    /// The <c>_module_index.json</c> manifest listing all modules in a project.
    /// </summary>
    public class ModuleIndex
    {
        /// <summary>Schema version — bump when the format changes.</summary>
        public int Version { get; set; } = 1;

        /// <summary>All modules registered in this project.</summary>
        public List<ModuleIndexEntry> Modules { get; set; } = new();
    }

    /// <summary>
    /// One entry in the module index — just enough to identify the module
    /// and its feature membership without loading the full JSON file.
    /// </summary>
    public class ModuleIndexEntry
    {
        /// <summary>Kebab-case module id.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable display name.</summary>
        public string Name { get; set; } = "";

        /// <summary>Feature IDs belonging to this module.</summary>
        public List<string> FeatureIds { get; set; } = new();
    }
}
