using System;

namespace Spritely.Models
{
    /// <summary>
    /// A single embedding vector stored in the vector database, with metadata
    /// linking it back to features, files, or tasks.
    /// </summary>
    public class EmbeddingVector
    {
        /// <summary>Unique identifier (e.g., "feature:git-integration:sigs", "file:Managers/GitHelper.cs:chunk:0").</summary>
        public string Id { get; set; } = "";

        /// <summary>Category for filtered search (feature_desc, feature_sigs, file_chunk, task, image).</summary>
        public string Category { get; set; } = "";

        /// <summary>Associated feature ID, if this vector represents feature content.</summary>
        public string? FeatureId { get; set; }

        /// <summary>Associated file path (relative), if this vector represents file content.</summary>
        public string? FilePath { get; set; }

        /// <summary>Chunk index within the file (0 for single-chunk files).</summary>
        public int ChunkIndex { get; set; }

        /// <summary>Starting line number in the source file (1-based).</summary>
        public int? LineStart { get; set; }

        /// <summary>Ending line number in the source file (1-based).</summary>
        public int? LineEnd { get; set; }

        /// <summary>First ~200 chars of the embedded content for display in search results.</summary>
        public string ContentPreview { get; set; } = "";

        /// <summary>The dense embedding vector (float32[], typically 1024 dimensions).</summary>
        public float[] Embedding { get; set; } = Array.Empty<float>();

        /// <summary>Binary quantized vector for fast hamming-distance pre-filtering.</summary>
        public byte[]? BinaryEmbedding { get; set; }

        /// <summary>Content hash for staleness detection (same scheme as SignatureExtractor).</summary>
        public string ContentHash { get; set; } = "";

        /// <summary>Which embedding model produced this vector.</summary>
        public string ModelId { get; set; } = "";

        /// <summary>When this vector was first created.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When this vector was last updated.</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
