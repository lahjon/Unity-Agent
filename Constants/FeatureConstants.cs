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

        /// <summary>Filename of the codebase-wide symbol index.</summary>
        public const string CodebaseIndexFileName = "_codebase_index.json";

        /// <summary>Filename of the module index manifest.</summary>
        public const string ModuleIndexFileName = "_module_index.json";

        /// <summary>File extension for individual module JSON files.</summary>
        public const string ModuleFileExtension = ".module.json";

        // ── Context Budget ─────────────────────────────────────────────

        /// <summary>Maximum number of primary features that can be injected into a single task prompt.</summary>
        public const int MaxFeaturesPerTask = 8;

        /// <summary>Maximum secondary (dependency/sibling) features injected for cross-feature context.</summary>
        public const int MaxSecondaryFeaturesPerTask = 5;

        /// <summary>Approximate token cap for context injected from a single secondary feature.</summary>
        public const int MaxTokensPerSecondaryFeature = 150;

        /// <summary>Approximate token cap for context injected from a single feature.</summary>
        public const int MaxTokensPerFeature = 500;

        /// <summary>Approximate total token cap for all feature context injected into a prompt.</summary>
        public const int MaxTotalFeatureContextTokens = 3000;

        // ── Signature Extraction ───────────────────────────────────────

        /// <summary>Number of hex characters kept from the SHA-256 hash for staleness detection.</summary>
        public const int SignatureHashLength = 12;

        /// <summary>Maximum source files sent to Sonnet in a single init-scan chunk.</summary>
        public const int MaxFilesPerSonnetChunk = 400;

        /// <summary>Alias for <see cref="MaxFilesPerSonnetChunk"/> — used by newer scan code.</summary>
        public const int MaxFilesPerChunk = MaxFilesPerSonnetChunk;

        /// <summary>Maximum signature lines extracted per source file.</summary>
        public const int MaxSignatureLinesPerFile = 30;

        // ── Module Limits ─────────────────────────────────────────────

        /// <summary>Maximum number of modules allowed per project.</summary>
        public const int MaxModulesPerProject = 20;

        /// <summary>Maximum number of features a single module can contain.</summary>
        public const int MaxFeaturesPerModule = 30;

        // ── Symbol Matching ──────────────────────────────────────────────

        /// <summary>Score boost applied when a symbol name matches during feature resolution.</summary>
        public const double SymbolMatchScoreBoost = 0.4;

        /// <summary>
        /// Pre-filter score threshold above which the Haiku confirmation call is skipped.
        /// When 1-3 candidates all score above this, they are treated as confirmed with confidence 1.0.
        /// </summary>
        public const double FastPathConfidenceThreshold = 0.85;

        /// <summary>Maximum candidate count eligible for the fast-path (skip Haiku) shortcut.</summary>
        public const int FastPathMaxCandidates = 3;

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
