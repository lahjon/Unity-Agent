namespace Spritely.Models
{
    /// <summary>
    /// Configuration for a hybrid semantic search query combining vector similarity,
    /// keyword matching, and optional multimodal inputs.
    /// </summary>
    public class HybridSearchRequest
    {
        /// <summary>The task description or natural language query.</summary>
        public string Query { get; set; } = "";

        /// <summary>Optional filter to specific vector categories (feature_desc, file_chunk, etc.).</summary>
        public string[]? Categories { get; set; }

        /// <summary>Optional filter to vectors associated with specific feature IDs.</summary>
        public string[]? FeatureIds { get; set; }

        /// <summary>Maximum number of results to return.</summary>
        public int TopK { get; set; } = 10;

        /// <summary>Minimum fused score threshold for inclusion in results.</summary>
        public float MinScore { get; set; } = 0.3f;

        /// <summary>Whether to include file chunk vectors in the search.</summary>
        public bool IncludeFileChunks { get; set; } = true;

        /// <summary>Whether to factor in task history embeddings for relevance boosting.</summary>
        public bool IncludeTaskHistory { get; set; } = true;

        /// <summary>Optional image data for multimodal queries (e.g., screenshot matching).</summary>
        public byte[]? ImageData { get; set; }
    }
}
