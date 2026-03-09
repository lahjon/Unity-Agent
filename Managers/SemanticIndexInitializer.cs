using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Bootstraps the vector store for a project by chunking and embedding all source
    /// files and feature definitions. Uses a persistent <see cref="EmbeddingCache"/> to
    /// avoid redundant Voyage API calls when content hasn't changed between sessions.
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
        /// Build the full vector index for a project. Uses a persistent embedding cache
        /// at %LOCALAPPDATA%\Spritely\embeddings\ to skip API calls for unchanged content.
        /// </summary>
        public async Task<int> BuildIndexAsync(string projectPath, CancellationToken ct = default)
        {
            if (!_embeddingService.IsAvailable)
            {
                ReportProgress("Embedding service unavailable — skipping vector index build");
                AppLogger.Warn("SemanticIndexInit", "No Voyage API key configured, skipping embedding index");
                return 0;
            }

            // Phase 1: Initialize store and load embedding cache
            ReportProgress("Initializing vector store...");
            await _vectorStore.InitializeAsync(projectPath);

            var cache = new EmbeddingCache();
            await cache.LoadAsync(projectPath);
            var allValidHashes = new HashSet<string>(StringComparer.Ordinal);

            // Phase 2: Index features (with cache)
            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
            ReportProgress($"Embedding {allFeatures.Count} features...");

            int featureVectors = 0;
            int featureCacheHits = 0;
            foreach (var feature in allFeatures)
            {
                ct.ThrowIfCancellationRequested();
                var hits = await IndexFeatureWithCacheAsync(feature, cache, allValidHashes);
                featureCacheHits += hits;
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

            // Phase 4: Batch embed chunks (with cache)
            ReportProgress("Embedding code chunks...");

            int embeddedChunks = 0;
            int chunkCacheHits = 0;
            var batchSize = EmbeddingConstants.MaxBatchSize;

            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = chunks.GetRange(i, Math.Min(batchSize, chunks.Count - i));
                var result = await EmbedChunkBatchWithCacheAsync(
                    batch, projectPath, cache, allValidHashes, ct);

                embeddedChunks += result.total;
                chunkCacheHits += result.cacheHits;

                if (embeddedChunks % 200 == 0 || i + batchSize >= chunks.Count)
                    ReportProgress($"Embedded {embeddedChunks}/{chunks.Count} chunks...");
            }

            // Phase 5: Save cache and report
            cache.Prune(allValidHashes);
            await cache.SaveAsync();

            var totalVectors = await _vectorStore.GetVectorCountAsync();
            var totalCacheHits = featureCacheHits + chunkCacheHits;
            var estimatedTokens = chunks.Count * EmbeddingConstants.TargetChunkTokens;
            var estimatedCost = estimatedTokens * 0.06 / 1_000_000;

            ReportProgress($"Vector index complete: {totalVectors} vectors, {totalCacheHits} cache hits (~${estimatedCost:F3} max cost)");
            AppLogger.Info("SemanticIndexInit",
                $"Built {totalVectors} vectors for {projectPath} ({featureVectors} features + {embeddedChunks} file chunks, {totalCacheHits} cache hits)");

            return totalVectors;
        }

        /// <summary>
        /// Index a single feature's desc + signatures vectors, using the cache to skip API calls.
        /// Returns the number of cache hits.
        /// </summary>
        private async Task<int> IndexFeatureWithCacheAsync(
            FeatureEntry feature, EmbeddingCache cache, HashSet<string> validHashes)
        {
            int cacheHits = 0;
            var vectors = new List<EmbeddingVector>();

            // Embed description + keywords
            var descText = $"{feature.Name}: {feature.Description}\nKeywords: {string.Join(", ", feature.Keywords ?? new List<string>())}";
            var descHash = EmbeddingCache.HashText(descText);
            validHashes.Add(descHash);

            float[]? descEmbedding;
            if (cache.TryGet(descHash, out var cachedDesc))
            {
                descEmbedding = cachedDesc;
                cacheHits++;
            }
            else
            {
                descEmbedding = await _embeddingService.EmbedCodeAsync(descText);
                if (descEmbedding != null)
                    cache.Put(descHash, descEmbedding);
            }

            if (descEmbedding != null)
            {
                vectors.Add(new EmbeddingVector
                {
                    Id = $"feature:{feature.Id}:desc",
                    Category = EmbeddingConstants.CategoryFeatureDesc,
                    FeatureId = feature.Id,
                    ContentPreview = descText.Length > 200 ? descText[..200] : descText,
                    Embedding = descEmbedding,
                    BinaryEmbedding = EmbeddingService.ToBinaryVector(descEmbedding),
                    ContentHash = descHash,
                    ModelId = EmbeddingConstants.VoyageCodeModel
                });
            }

            // Embed code signatures
            if (feature.Context?.Signatures != null && feature.Context.Signatures.Count > 0)
            {
                var sigsText = string.Join("\n", feature.Context.Signatures.Values
                    .Select(s => s.Content)
                    .Where(c => !string.IsNullOrWhiteSpace(c)));

                if (sigsText.Length > 0)
                {
                    var sigsHash = EmbeddingCache.HashText(sigsText);
                    validHashes.Add(sigsHash);

                    float[]? sigsEmbedding;
                    if (cache.TryGet(sigsHash, out var cachedSigs))
                    {
                        sigsEmbedding = cachedSigs;
                        cacheHits++;
                    }
                    else
                    {
                        sigsEmbedding = await _embeddingService.EmbedCodeAsync(sigsText);
                        if (sigsEmbedding != null)
                            cache.Put(sigsHash, sigsEmbedding);
                    }

                    if (sigsEmbedding != null)
                    {
                        vectors.Add(new EmbeddingVector
                        {
                            Id = $"feature:{feature.Id}:sigs",
                            Category = EmbeddingConstants.CategoryFeatureSigs,
                            FeatureId = feature.Id,
                            ContentPreview = sigsText.Length > 200 ? sigsText[..200] : sigsText,
                            Embedding = sigsEmbedding,
                            BinaryEmbedding = EmbeddingService.ToBinaryVector(sigsEmbedding),
                            ContentHash = sigsHash,
                            ModelId = EmbeddingConstants.VoyageCodeModel
                        });
                    }
                }
            }

            if (vectors.Count > 0)
                await _vectorStore.UpsertBatchAsync(vectors);

            return cacheHits;
        }

        /// <summary>
        /// Embed a batch of code chunks, using cache for hits and only calling the API for misses.
        /// </summary>
        private async Task<(int total, int cacheHits)> EmbedChunkBatchWithCacheAsync(
            List<CodeChunk> batch, string projectPath,
            EmbeddingCache cache, HashSet<string> validHashes,
            CancellationToken ct)
        {
            // Separate cached vs uncached chunks
            var chunkHashes = new string[batch.Count];
            var cachedEmbeddings = new float[batch.Count][];
            var uncachedIndices = new List<int>();

            for (int j = 0; j < batch.Count; j++)
            {
                var hash = EmbeddingCache.HashText(batch[j].Content);
                chunkHashes[j] = hash;
                validHashes.Add(hash);

                if (cache.TryGet(hash, out var cached))
                    cachedEmbeddings[j] = cached;
                else
                    uncachedIndices.Add(j);
            }

            int cacheHits = batch.Count - uncachedIndices.Count;

            // Call API only for uncached chunks
            if (uncachedIndices.Count > 0)
            {
                var uncachedTexts = uncachedIndices.Select(idx => batch[idx].Content).ToArray();
                var apiEmbeddings = await _embeddingService.EmbedBatchAsync(
                    uncachedTexts, EmbeddingConstants.VoyageCodeModel, ct);

                if (apiEmbeddings != null)
                {
                    for (int k = 0; k < uncachedIndices.Count && k < apiEmbeddings.Length; k++)
                    {
                        var idx = uncachedIndices[k];
                        cachedEmbeddings[idx] = apiEmbeddings[k];
                        cache.Put(chunkHashes[idx], apiEmbeddings[k]);
                    }
                }
            }

            // Build vectors for all chunks that have embeddings
            var vectors = new List<EmbeddingVector>();
            for (int j = 0; j < batch.Count; j++)
            {
                if (cachedEmbeddings[j] == null) continue;

                var chunk = batch[j];
                var fullPath = Path.Combine(projectPath, chunk.FilePath);
                var fileHash = File.Exists(fullPath) ? SignatureExtractor.ComputeFileHash(fullPath) : "";

                vectors.Add(new EmbeddingVector
                {
                    Id = $"file:{chunk.FilePath}:chunk:{chunk.ChunkIndex}",
                    Category = EmbeddingConstants.CategoryFileChunk,
                    FilePath = chunk.FilePath,
                    ChunkIndex = chunk.ChunkIndex,
                    LineStart = chunk.LineStart,
                    LineEnd = chunk.LineEnd,
                    ContentPreview = chunk.Content.Length > 200 ? chunk.Content[..200] : chunk.Content,
                    Embedding = cachedEmbeddings[j],
                    BinaryEmbedding = EmbeddingService.ToBinaryVector(cachedEmbeddings[j]),
                    ContentHash = fileHash,
                    ModelId = EmbeddingConstants.VoyageCodeModel
                });
            }

            if (vectors.Count > 0)
                await _vectorStore.UpsertBatchAsync(vectors);

            return (vectors.Count, cacheHits);
        }

        private void ReportProgress(string message)
        {
            ProgressChanged?.Invoke(message);
            AppLogger.Info("SemanticIndexInit", message);
        }
    }
}
