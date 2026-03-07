namespace Spritely.Constants
{
    /// <summary>
    /// Constants for the Feature System — registry paths, limits, and tunables.
    /// See also <see cref="AppConstants"/> for general application constants.
    /// </summary>
    public static class FeatureConstants
    {
        // ── Directory & File Layout ────────────────────────────────────

        /// <summary>Top-level directory created in the project root for Spritely metadata.</summary>
        public const string SpritelyDir = ".spritely";

        /// <summary>Subdirectory under <see cref="SpritelyDir"/> where feature JSON files live.</summary>
        public const string FeaturesDir = "features";

        /// <summary>Filename of the feature index manifest inside the features directory.</summary>
        public const string IndexFileName = "_index.json";

        // ── Context Budget ─────────────────────────────────────────────

        /// <summary>Maximum number of features that can be injected into a single task prompt.</summary>
        public const int MaxFeaturesPerTask = 5;

        /// <summary>Approximate token cap for context injected from a single feature.</summary>
        public const int MaxTokensPerFeature = 500;

        /// <summary>Approximate total token cap for all feature context injected into a prompt.</summary>
        public const int MaxTotalFeatureContextTokens = 3000;

        // ── Signature Extraction ───────────────────────────────────────

        /// <summary>Number of hex characters kept from the SHA-256 hash for staleness detection.</summary>
        public const int SignatureHashLength = 12;

        /// <summary>Maximum source files sent to Sonnet in a single init-scan chunk.</summary>
        public const int MaxFilesPerSonnetChunk = 400;

        /// <summary>Maximum signature lines extracted per source file.</summary>
        public const int MaxSignatureLinesPerFile = 20;

        // ── Concurrency & Safety ───────────────────────────────────────

        /// <summary>Timeout in milliseconds when acquiring the feature-registry file mutex.</summary>
        public const int MutexTimeoutMs = 5000;

        /// <summary>Number of retry attempts for mutex acquisition before giving up.</summary>
        public const int MutexRetryCount = 3;

        // ── Ignored Directories ────────────────────────────────────────

        /// <summary>
        /// Directories to skip when scanning a project for source files during
        /// feature initialisation and signature extraction.
        /// </summary>
        public static readonly string[] IgnoredDirectories =
        {
            "node_modules",
            "Library",
            "Temp",
            "bin",
            "obj",
            ".git",
            "Builds",
            "Logs",
            ".vs",
            ".idea",
            "packages",
            "dist",
            "build",
            "__pycache__",
            ".spritely"
        };
    }
}
