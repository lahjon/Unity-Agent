using System;

namespace Spritely.Models
{
    /// <summary>
    /// Small metadata file (<c>_metadata.json</c>) stored alongside the JSONL database.
    /// Contains schema version and symbol index version hash.
    /// </summary>
    public class FeatureMetadata
    {
        /// <summary>Schema version — bump when the JSONL format changes.</summary>
        public int Version { get; set; } = 2;

        /// <summary>
        /// Hash of all tracked file hashes — used to detect when the symbol index is stale.
        /// </summary>
        public string? SymbolIndexVersion { get; set; }

        /// <summary>UTC timestamp of the last full database write.</summary>
        public DateTime LastUpdatedAt { get; set; }
    }
}
