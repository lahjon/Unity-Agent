namespace Spritely.Models
{
    /// <summary>
    /// A single result from hybrid semantic search, combining vector similarity
    /// with keyword and structural scores into a fused relevance score.
    /// </summary>
    public class SemanticSearchResult
    {
        /// <summary>Vector ID from the store.</summary>
        public string VectorId { get; set; } = "";

        /// <summary>Category of the matched vector (feature_desc, file_chunk, etc.).</summary>
        public string Category { get; set; } = "";

        /// <summary>Associated feature ID, if the match is feature-related.</summary>
        public string? FeatureId { get; set; }

        /// <summary>Associated file path, if the match is a file chunk.</summary>
        public string? FilePath { get; set; }

        /// <summary>Preview of the matched content.</summary>
        public string ContentPreview { get; set; } = "";

        /// <summary>Raw cosine similarity from vector search (0.0-1.0).</summary>
        public float VectorScore { get; set; }

        /// <summary>Keyword/symbol matching score from existing FeatureRegistryManager (0.0-1.0).</summary>
        public float KeywordScore { get; set; }

        /// <summary>Historical task affinity score (0.0-1.0).</summary>
        public float HistoryScore { get; set; }

        /// <summary>Combined score after reciprocal rank fusion.</summary>
        public double FusedScore { get; set; }
    }
}
