namespace Spritely.Constants
{
    /// <summary>
    /// Constants for the Hybrid Semantic Index — embedding models, dimensions, thresholds, and tunables.
    /// </summary>
    public static class EmbeddingConstants
    {
        // ── Embedding Models ────────────────────────────────────────────

        /// <summary>Primary model for code and text embeddings (Voyage Code 3).</summary>
        public const string VoyageCodeModel = "voyage-code-3";

        /// <summary>Model for natural language text (task summaries, descriptions).</summary>
        public const string VoyageTextModel = "voyage-3-large";

        /// <summary>Model for multimodal embeddings (images + text).</summary>
        public const string VoyageMultimodalModel = "voyage-multimodal-3";

        /// <summary>Embedding dimension for all Voyage models.</summary>
        public const int EmbeddingDimension = 1024;

        /// <summary>Size in bytes of a single float32 embedding vector.</summary>
        public const int EmbeddingByteSize = EmbeddingDimension * sizeof(float);

        /// <summary>Size in bytes of the binary quantized vector (1 bit per dimension).</summary>
        public const int BinaryEmbeddingByteSize = EmbeddingDimension / 8;

        // ── API Configuration ───────────────────────────────────────────

        /// <summary>Maximum number of texts per Voyage API batch call.</summary>
        public const int MaxBatchSize = 128;

        /// <summary>Maximum total tokens per Voyage API batch call.</summary>
        public const int MaxBatchTokens = 100_000;

        /// <summary>Timeout for a single embedding API call.</summary>
        public static readonly System.TimeSpan ApiTimeout = System.TimeSpan.FromSeconds(30);

        /// <summary>Maximum retry attempts for failed API calls.</summary>
        public const int MaxApiRetries = 3;

        /// <summary>Base delay between retries (doubled each attempt).</summary>
        public static readonly System.TimeSpan RetryBaseDelay = System.TimeSpan.FromMilliseconds(500);

        // ── Vector Store ────────────────────────────────────────────────

        /// <summary>Subdirectory under .spritely for embedding storage.</summary>
        public const string EmbeddingsDir = "embeddings";

        /// <summary>SQLite database filename for vector storage.</summary>
        public const string VectorDbFileName = "vectors.db";

        /// <summary>Binary cache file extension for persistent embedding cache at %LOCALAPPDATA%.</summary>
        public const string EmbeddingCacheExtension = ".bin";

        /// <summary>Current schema version for migration tracking.</summary>
        public const int SchemaVersion = 1;

        // ── Search Tuning ───────────────────────────────────────────────

        /// <summary>Number of candidates selected by binary pre-filter before full cosine rerank.</summary>
        public const int PreFilterK = 100;

        /// <summary>Default number of top results returned by vector search.</summary>
        public const int DefaultTopK = 10;

        /// <summary>Minimum cosine similarity score to include in results.</summary>
        public const float MinVectorScore = 0.25f;

        // ── Fusion Weights ──────────────────────────────────────────────
        // Reciprocal Rank Fusion weights for combining different scoring signals.

        /// <summary>Weight for dense vector cosine similarity in the fused score.</summary>
        public const double FusionWeightVector = 0.45;

        /// <summary>Weight for keyword/symbol matching score in the fused score.</summary>
        public const double FusionWeightKeyword = 0.25;

        /// <summary>Weight for symbol name match boost in the fused score.</summary>
        public const double FusionWeightSymbol = 0.15;

        /// <summary>Weight for historical task affinity in the fused score.</summary>
        public const double FusionWeightHistory = 0.10;

        /// <summary>Weight for dependency graph proximity in the fused score.</summary>
        public const double FusionWeightDependency = 0.05;

        // ── Haiku Skip Thresholds ───────────────────────────────────────

        /// <summary>
        /// Fused score threshold above which Haiku confirmation is skipped entirely.
        /// When top candidates exceed this, vector + keyword scores are trusted directly.
        /// </summary>
        public const double HaikuSkipHighConfidence = 0.80;

        /// <summary>
        /// Fused score threshold for medium confidence — Haiku is still skipped but
        /// more results are included to compensate for lower certainty.
        /// </summary>
        public const double HaikuSkipMediumConfidence = 0.60;

        /// <summary>Maximum candidates eligible for high-confidence Haiku skip.</summary>
        public const int HaikuSkipMaxCandidates = 3;

        // ── Chunking ────────────────────────────────────────────────────

        /// <summary>Target chunk size in tokens for code file chunking.</summary>
        public const int TargetChunkTokens = 512;

        /// <summary>Overlap in tokens between adjacent chunks.</summary>
        public const int ChunkOverlapTokens = 64;

        /// <summary>Maximum number of chunks per file (cap for very large files).</summary>
        public const int MaxChunksPerFile = 20;

        /// <summary>Minimum chunk size in characters — smaller chunks are merged with neighbors.</summary>
        public const int MinChunkChars = 100;

        // ── Staleness & Refresh ─────────────────────────────────────────

        /// <summary>Maximum number of files to re-embed in a single post-task update.</summary>
        public const int MaxReembedFilesPerUpdate = 50;

        /// <summary>Maximum task history embeddings kept per project (oldest pruned).</summary>
        public const int MaxTaskHistoryEmbeddings = 500;

        // ── Vector Categories ───────────────────────────────────────────

        public const string CategoryFeatureDesc = "feature_desc";
        public const string CategoryFeatureSigs = "feature_sigs";
        public const string CategoryFileChunk = "file_chunk";
        public const string CategoryTask = "task";
        public const string CategoryImage = "image";

        // ── Feature Flag ────────────────────────────────────────────────

        /// <summary>
        /// Environment variable name to enable hybrid search. Set to "true" to activate.
        /// Falls back to keyword + Haiku when not set or "false".
        /// </summary>
        public const string EnableHybridSearchEnvVar = "SPRITELY_HYBRID_SEARCH";
    }
}
