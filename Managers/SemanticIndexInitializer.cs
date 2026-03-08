using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Bootstraps the vector store for a project by chunking and embedding all source
    /// files and feature definitions. Runs alongside or after <see cref="FeatureInitializer"/>
    /// and produces the SQLite vector database used by <see cref="HybridSearchManager"/>.
    /// </summary>
    public class SemanticIndexInitializer
    {
        private readonly EmbeddingService _embeddingService;
        private readonly VectorStore _vectorStore;
        private readonly FeatureRegistryManager _registryManager;
        private readonly HybridSearchManager _hybridSearchManager;

        /// <summary>Fired to report progress messages to the UI.</summary>
        public event Action<string>? ProgressChanged;

        public SemanticIndexInitializer(
            EmbeddingService embeddingService,
            VectorStore vectorStore,
            FeatureRegistryManager registryManager,
            HybridSearchManager hybridSearchManager)
        {
            _embeddingService = embeddingService;
            _vectorStore = vectorStore;
            _registryManager = registryManager;
            _hybridSearchManager = hybridSearchManager;
        }

        /// <summary>
        /// Build the full vector index for a project. This is typically called after
        /// <see cref="FeatureInitializer.InitializeAsync"/> completes, or manually via
        /// the "Build Embeddings" button.
        /// </summary>
        public async Task<int> BuildIndexAsync(string projectPath, CancellationToken ct = default)
        {
            if (!_embeddingService.IsAvailable)
            {
                ReportProgress("Embedding service unavailable — skipping vector index build");
                AppLogger.Warn("SemanticIndexInit", "No Voyage API key configured, skipping embedding index");
                return 0;
            }

            // Phase 1: Initialize store
            ReportProgress("Initializing vector store...");
            await _vectorStore.InitializeAsync(projectPath);

            // Phase 2: Index features
            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
            ReportProgress($"Embedding {allFeatures.Count} features...");

            int featureVectors = 0;
            foreach (var feature in allFeatures)
            {
                ct.ThrowIfCancellationRequested();
                await _hybridSearchManager.IndexFeatureAsync(projectPath, feature);
                featureVectors++;
                if (featureVectors % 10 == 0)
                    ReportProgress($"Embedded {featureVectors}/{allFeatures.Count} features...");
            }

            // Phase 3: Discover and chunk source files
            var initializer = new FeatureInitializer(_registryManager);
            var sourceFiles = initializer.ScanSourceFiles(projectPath);
            ReportProgress($"Chunking {sourceFiles.Count} source files...");

            var chunks = new List<CodeChunk>();
            var fileContents = new List<(string path, string content)>();

            foreach (var relativePath in sourceFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fullPath = Path.Combine(projectPath, relativePath);
                    var content = await File.ReadAllTextAsync(fullPath, ct);
                    fileContents.Add((relativePath, content));
                }
                catch { /* skip unreadable files */ }
            }

            chunks = CodeChunker.ChunkFiles(fileContents);
            ReportProgress($"Generated {chunks.Count} chunks from {sourceFiles.Count} files");

            // Phase 4: Batch embed chunks
            ReportProgress("Embedding code chunks (this may take a moment)...");

            int embeddedChunks = 0;
            var batchSize = EmbeddingConstants.MaxBatchSize;

            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = chunks.GetRange(i, Math.Min(batchSize, chunks.Count - i));
                var texts = batch.ConvertAll(c => c.Content).ToArray();

                var embeddings = await _embeddingService.EmbedBatchAsync(texts, EmbeddingConstants.VoyageCodeModel, ct);
                if (embeddings == null)
                {
                    AppLogger.Warn("SemanticIndexInit", $"Batch embedding failed at offset {i}, skipping {batch.Count} chunks");
                    continue;
                }

                var vectors = new List<EmbeddingVector>();
                for (int j = 0; j < batch.Count && j < embeddings.Length; j++)
                {
                    var chunk = batch[j];
                    var fullPath = Path.Combine(projectPath, chunk.FilePath);
                    var hash = File.Exists(fullPath) ? SignatureExtractor.ComputeFileHash(fullPath) : "";

                    vectors.Add(new EmbeddingVector
                    {
                        Id = $"file:{chunk.FilePath}:chunk:{chunk.ChunkIndex}",
                        Category = EmbeddingConstants.CategoryFileChunk,
                        FilePath = chunk.FilePath,
                        ChunkIndex = chunk.ChunkIndex,
                        LineStart = chunk.LineStart,
                        LineEnd = chunk.LineEnd,
                        ContentPreview = chunk.Content.Length > 200 ? chunk.Content[..200] : chunk.Content,
                        Embedding = embeddings[j],
                        BinaryEmbedding = EmbeddingService.ToBinaryVector(embeddings[j]),
                        ContentHash = hash,
                        ModelId = EmbeddingConstants.VoyageCodeModel
                    });
                }

                await _vectorStore.UpsertBatchAsync(vectors);
                embeddedChunks += vectors.Count;

                if (embeddedChunks % 200 == 0 || i + batchSize >= chunks.Count)
                    ReportProgress($"Embedded {embeddedChunks}/{chunks.Count} chunks...");
            }

            // Phase 5: Report results
            var totalVectors = await _vectorStore.GetVectorCountAsync();
            var estimatedTokens = chunks.Count * EmbeddingConstants.TargetChunkTokens;
            var estimatedCost = estimatedTokens * 0.06 / 1_000_000; // $0.06/M tokens

            ReportProgress($"Vector index complete: {totalVectors} vectors (~${estimatedCost:F3} embedding cost)");
            AppLogger.Info("SemanticIndexInit",
                $"Built {totalVectors} vectors for {projectPath} ({featureVectors} features + {embeddedChunks} file chunks)");

            return totalVectors;
        }

        private void ReportProgress(string message)
        {
            ProgressChanged?.Invoke(message);
            AppLogger.Info("SemanticIndexInit", message);
        }
    }
}
