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
    /// Orchestrates hybrid semantic search by fusing dense vector similarity scores
    /// with keyword/symbol matching, dependency graph proximity, and historical task
    /// affinity. Replaces Haiku CLI calls for ~70% of tasks where vector confidence
    /// is high enough, falling back to <see cref="FeatureContextResolver"/> for ambiguous cases.
    /// </summary>
    public class HybridSearchManager : IDisposable
    {
        private readonly EmbeddingService _embeddingService;
        private readonly VectorStore _vectorStore;
        private readonly FeatureRegistryManager _registryManager;
        private readonly FeatureContextResolver _featureContextResolver;
        private readonly CodebaseIndexManager? _codebaseIndexManager;
        private bool _initialized;

        public HybridSearchManager(
            EmbeddingService embeddingService,
            VectorStore vectorStore,
            FeatureRegistryManager registryManager,
            FeatureContextResolver featureContextResolver,
            CodebaseIndexManager? codebaseIndexManager = null)
        {
            _embeddingService = embeddingService;
            _vectorStore = vectorStore;
            _registryManager = registryManager;
            _featureContextResolver = featureContextResolver;
            _codebaseIndexManager = codebaseIndexManager;
        }

        /// <summary>Whether hybrid search is enabled and fully operational.</summary>
        public bool IsAvailable => _embeddingService.IsAvailable && _vectorStore.IsInitialized && _initialized;

        /// <summary>
        /// Initialize the hybrid search manager for a project. Must be called before
        /// <see cref="SearchAsync"/>. Safe to call multiple times.
        /// </summary>
        public async Task InitializeAsync(string projectPath)
        {
            if (_initialized) return;

            try
            {
                if (!_vectorStore.IsInitialized)
                    await _vectorStore.InitializeAsync(projectPath);

                var vectorCount = await _vectorStore.GetVectorCountAsync();
                _initialized = vectorCount > 0;

                if (_initialized)
                    AppLogger.Info("HybridSearch", $"Initialized with {vectorCount} vectors");
                else
                    AppLogger.Info("HybridSearch", "No vectors found — hybrid search will fall back to keyword + Haiku");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("HybridSearch", $"Initialization failed, falling back: {ex.Message}");
                _initialized = false;
            }
        }

        /// <summary>
        /// Perform hybrid search combining vector similarity with keyword/symbol matching.
        /// Falls back to <see cref="FeatureContextResolver.ResolveAsync"/> when vector
        /// confidence is too low or the embedding service is unavailable.
        /// </summary>
        public async Task<FeatureContextResult?> SearchAsync(
            HybridSearchRequest request, string projectPath, CancellationToken ct = default)
        {
            // Fall back to existing resolver if hybrid search isn't available
            if (!IsAvailable)
                return await _featureContextResolver.ResolveAsync(projectPath, request.Query, ct);

            try
            {
                var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
                if (allFeatures.Count == 0)
                    return null;

                // Embed the query using query input_type (optimized for search retrieval)
                var queryEmbedding = await _embeddingService.EmbedQueryAsync(request.Query, EmbeddingConstants.VoyageCodeModel, ct);
                if (queryEmbedding == null)
                    return await _featureContextResolver.ResolveAsync(projectPath, request.Query, ct);

                // Vector search — feature vectors + optionally file chunks
                var searchCategories = new List<string>
                {
                    EmbeddingConstants.CategoryFeatureDesc,
                    EmbeddingConstants.CategoryFeatureSigs
                };
                if (request.IncludeFileChunks)
                    searchCategories.Add(EmbeddingConstants.CategoryFileChunk);

                var vectorResults = await _vectorStore.SearchAsync(
                    queryEmbedding, EmbeddingConstants.DefaultTopK * 2,
                    searchCategories.ToArray(), EmbeddingConstants.MinVectorScore);

                // Extract feature IDs from file chunk results by looking up which features own those files
                var fileChunkFeatureIds = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                if (request.IncludeFileChunks)
                {
                    foreach (var result in vectorResults.Where(r => r.Category == EmbeddingConstants.CategoryFileChunk && r.FilePath != null))
                    {
                        // Find which feature(s) own this file
                        foreach (var feature in allFeatures)
                        {
                            if (feature.PrimaryFiles?.Contains(result.FilePath, StringComparer.OrdinalIgnoreCase) == true ||
                                feature.SecondaryFiles?.Contains(result.FilePath, StringComparer.OrdinalIgnoreCase) == true)
                            {
                                if (!fileChunkFeatureIds.TryGetValue(feature.Id, out var existing) || result.VectorScore > existing)
                                    fileChunkFeatureIds[feature.Id] = result.VectorScore;
                            }
                        }
                    }
                }

                // Keyword + symbol matching (reuse existing infrastructure)
                CodebaseSymbolIndex? symbolIndex = null;
                if (_codebaseIndexManager != null)
                {
                    try { symbolIndex = await _codebaseIndexManager.LoadIndexAsync(projectPath); }
                    catch { /* degrade gracefully */ }
                }

                var keywordMatches = _registryManager.FindMatchingFeaturesEnhanced(
                    request.Query, allFeatures, symbolIndex, FeatureConstants.MaxFeaturesPerTask,
                    _codebaseIndexManager);

                // Compute keyword scores normalized to 0-1
                var tokens = FeatureRegistryManager.Tokenize(request.Query);
                var keywordScores = keywordMatches.ToDictionary(
                    f => f.Id,
                    f => FeatureRegistryManager.ScoreFeature(tokens, request.Query, f),
                    StringComparer.OrdinalIgnoreCase);

                var maxKeywordScore = keywordScores.Values.Count > 0 ? keywordScores.Values.Max() : 1.0;
                if (maxKeywordScore <= 0) maxKeywordScore = 1.0;

                // Build candidate set from all sources: vector hits + keyword hits + file chunk hits
                var candidateFeatureIds = vectorResults
                    .Where(r => r.FeatureId != null)
                    .Select(r => r.FeatureId!)
                    .Union(keywordMatches.Select(f => f.Id))
                    .Union(fileChunkFeatureIds.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Per-feature historical affinity
                var perFeatureHistory = request.IncludeTaskHistory
                    ? await _vectorStore.GetPerFeatureHistoryAsync(candidateFeatureIds)
                    : new Dictionary<string, List<float[]>>();

                // Build dependency graph for proximity scoring
                var graph = _registryManager.BuildDependencyGraph(allFeatures);
                var highConfidenceIds = vectorResults
                    .Where(r => r.FeatureId != null && r.VectorScore >= 0.5f)
                    .Select(r => r.FeatureId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Fuse scores per feature
                var fusedScores = new Dictionary<string, (double score, float vectorScore, float keywordScore)>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var featureId in candidateFeatureIds)
                {
                    // Vector score: max from direct feature vectors or file chunk association
                    var directVectorScore = vectorResults
                        .Where(r => r.FeatureId != null && r.FeatureId.Equals(featureId, StringComparison.OrdinalIgnoreCase))
                        .Select(r => (float?)r.VectorScore)
                        .Max() ?? 0f;
                    var chunkVectorScore = fileChunkFeatureIds.TryGetValue(featureId, out var cs) ? cs * 0.8f : 0f;
                    var vectorScore = Math.Max(directVectorScore, chunkVectorScore);

                    var kwScore = keywordScores.TryGetValue(featureId, out var ks) ? (float)(ks / maxKeywordScore) : 0f;

                    var symbolScore = keywordMatches.Any(f => f.Id.Equals(featureId, StringComparison.OrdinalIgnoreCase)
                        && f.SymbolNames?.Count > 0)
                        ? (float)FeatureConstants.SymbolMatchScoreBoost : 0f;

                    // Per-feature historical affinity: average similarity to past tasks that used THIS feature
                    var historyScore = 0f;
                    if (perFeatureHistory.TryGetValue(featureId, out var featureHistoryEmbs) && featureHistoryEmbs.Count > 0)
                    {
                        historyScore = featureHistoryEmbs
                            .Select(h => EmbeddingService.CosineSimilarity(queryEmbedding, h))
                            .Average();
                    }

                    // Dependency proximity: boost if a high-confidence feature depends on or is depended on by this one
                    var depScore = 0f;
                    if (highConfidenceIds.Count > 0 && !highConfidenceIds.Contains(featureId))
                    {
                        var neighbors = _registryManager.GetFeatureWithDependencies(featureId, allFeatures, graph, maxDepth: 1);
                        if (neighbors.Any(n => highConfidenceIds.Contains(n.Id)))
                            depScore = 1.0f;
                    }

                    var fused =
                        vectorScore * EmbeddingConstants.FusionWeightVector +
                        kwScore * EmbeddingConstants.FusionWeightKeyword +
                        symbolScore * EmbeddingConstants.FusionWeightSymbol +
                        historyScore * EmbeddingConstants.FusionWeightHistory +
                        depScore * EmbeddingConstants.FusionWeightDependency;

                    fusedScores[featureId] = (fused, vectorScore, kwScore);
                }

                // Sort by fused score and determine if Haiku is needed
                var ranked = fusedScores
                    .OrderByDescending(kv => kv.Value.score)
                    .Take(FeatureConstants.MaxFeaturesPerTask)
                    .ToList();

                if (ranked.Count == 0)
                {
                    // No features matched at all — this is likely a new feature
                    return new FeatureContextResult
                    {
                        RelevantFeatures = new List<MatchedFeature>(),
                        IsNewFeature = true,
                        ContextBlock = ""
                    };
                }

                var topScore = ranked[0].Value.score;

                // High confidence: skip Haiku entirely
                if (topScore >= EmbeddingConstants.HaikuSkipHighConfidence &&
                    ranked.Count <= EmbeddingConstants.HaikuSkipMaxCandidates)
                {
                    AppLogger.Info("HybridSearch",
                        $"High confidence — skipping Haiku. Top: {ranked[0].Key}={topScore:F3}");
                    return await BuildResultFromRanked(ranked, allFeatures, projectPath, graph);
                }

                // Medium confidence: still skip Haiku but include more results
                if (topScore >= EmbeddingConstants.HaikuSkipMediumConfidence)
                {
                    AppLogger.Info("HybridSearch",
                        $"Medium confidence — skipping Haiku. Top: {ranked[0].Key}={topScore:F3}");
                    return await BuildResultFromRanked(ranked, allFeatures, projectPath, graph);
                }

                // Low confidence: fall back to Haiku for disambiguation + new feature detection
                AppLogger.Info("HybridSearch",
                    $"Low confidence ({topScore:F3}) — falling back to Haiku resolver");
                return await _featureContextResolver.ResolveAsync(projectPath, request.Query, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("HybridSearch", $"Hybrid search failed, falling back: {ex.Message}");
                return await _featureContextResolver.ResolveAsync(projectPath, request.Query, ct);
            }
        }

        /// <summary>
        /// Index a feature's description and signatures as embedding vectors.
        /// </summary>
        public async Task IndexFeatureAsync(string projectPath, FeatureEntry feature)
        {
            if (!_embeddingService.IsAvailable) return;

            try
            {
                var vectors = new List<EmbeddingVector>();

                // Embed description + keywords
                var descText = $"{feature.Name}: {feature.Description}\nKeywords: {string.Join(", ", feature.Keywords ?? new List<string>())}";
                var descEmbedding = await _embeddingService.EmbedCodeAsync(descText);
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
                        ContentHash = feature.Id, // stable identifier
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
                        var sigsEmbedding = await _embeddingService.EmbedCodeAsync(sigsText);
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
                                ContentHash = string.Join(",", feature.Context.Signatures.Values.Select(s => s.Hash)),
                                ModelId = EmbeddingConstants.VoyageCodeModel
                            });
                        }
                    }
                }

                if (vectors.Count > 0)
                    await _vectorStore.UpsertBatchAsync(vectors);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("HybridSearch", $"Failed to index feature {feature.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Index a single source file by chunking and embedding its content.
        /// </summary>
        public async Task IndexFileAsync(string projectPath, string relativePath)
        {
            if (!_embeddingService.IsAvailable) return;

            try
            {
                var fullPath = Path.Combine(projectPath, relativePath);
                if (!File.Exists(fullPath)) return;

                var content = await File.ReadAllTextAsync(fullPath);
                var chunks = CodeChunker.ChunkFile(relativePath, content);

                if (chunks.Count == 0) return;

                // Remove old chunks for this file
                await _vectorStore.DeleteByFileAsync(relativePath);

                // Batch embed all chunks
                var texts = chunks.Select(c => c.Content).ToArray();
                var embeddings = await _embeddingService.EmbedBatchAsync(texts);
                if (embeddings == null) return;

                var vectors = new List<EmbeddingVector>();
                for (int i = 0; i < chunks.Count && i < embeddings.Length; i++)
                {
                    var chunk = chunks[i];
                    vectors.Add(new EmbeddingVector
                    {
                        Id = $"file:{relativePath}:chunk:{chunk.ChunkIndex}",
                        Category = EmbeddingConstants.CategoryFileChunk,
                        FilePath = relativePath,
                        ChunkIndex = chunk.ChunkIndex,
                        LineStart = chunk.LineStart,
                        LineEnd = chunk.LineEnd,
                        ContentPreview = chunk.Content.Length > 200 ? chunk.Content[..200] : chunk.Content,
                        Embedding = embeddings[i],
                        BinaryEmbedding = EmbeddingService.ToBinaryVector(embeddings[i]),
                        ContentHash = SignatureExtractor.ComputeFileHash(fullPath),
                        ModelId = EmbeddingConstants.VoyageCodeModel
                    });
                }

                await _vectorStore.UpsertBatchAsync(vectors);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("HybridSearch", $"Failed to index file {relativePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Record a completed task's embedding for historical learning.
        /// </summary>
        public async Task IndexTaskCompletionAsync(
            string taskId, string description, string? summary,
            List<string> matchedFeatureIds, string outcome)
        {
            if (!_embeddingService.IsAvailable) return;

            try
            {
                var descEmbedding = await _embeddingService.EmbedCodeAsync(description);
                if (descEmbedding == null) return;

                float[]? summaryEmbedding = null;
                if (!string.IsNullOrWhiteSpace(summary))
                    summaryEmbedding = await _embeddingService.EmbedTextAsync(summary, EmbeddingConstants.VoyageTextModel);

                await _vectorStore.UpsertTaskEmbeddingAsync(taskId, descEmbedding, summaryEmbedding, matchedFeatureIds, outcome);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("HybridSearch", $"Failed to index task completion {taskId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-embed changed files after task completion. Only re-embeds files whose
        /// content hash has changed.
        /// </summary>
        public async Task UpdateChangedFileEmbeddingsAsync(
            string projectPath, List<string> changedFiles, CancellationToken ct = default)
        {
            if (!_embeddingService.IsAvailable || !_vectorStore.IsInitialized) return;

            var filesToReembed = new List<string>();

            foreach (var file in changedFiles.Take(EmbeddingConstants.MaxReembedFilesPerUpdate))
            {
                ct.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(projectPath, file);
                if (!File.Exists(fullPath)) continue;

                var currentHash = SignatureExtractor.ComputeFileHash(fullPath);
                var isStale = await _vectorStore.IsStaleAsync($"file:{file}:chunk:0", currentHash);
                if (isStale)
                    filesToReembed.Add(file);
            }

            foreach (var file in filesToReembed)
            {
                ct.ThrowIfCancellationRequested();
                await IndexFileAsync(projectPath, file);
            }

            if (filesToReembed.Count > 0)
                AppLogger.Info("HybridSearch", $"Re-embedded {filesToReembed.Count} changed files");
        }

        /// <summary>
        /// Full reindex of all features and source files in a project.
        /// Used during initialization or manual rebuild.
        /// </summary>
        public async Task ReindexProjectAsync(
            string projectPath, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            progress?.Report("Loading features...");
            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);

            // Index all features
            progress?.Report($"Embedding {allFeatures.Count} features...");
            foreach (var feature in allFeatures)
            {
                ct.ThrowIfCancellationRequested();
                await IndexFeatureAsync(projectPath, feature);
            }

            // Index source files
            var initializer = new FeatureInitializer(_registryManager);
            var sourceFiles = initializer.ScanSourceFiles(projectPath);
            progress?.Report($"Chunking and embedding {sourceFiles.Count} source files...");

            int processed = 0;
            foreach (var relativePath in sourceFiles)
            {
                ct.ThrowIfCancellationRequested();
                await IndexFileAsync(projectPath, relativePath);
                processed++;

                if (processed % 50 == 0)
                    progress?.Report($"Embedded {processed}/{sourceFiles.Count} files...");
            }

            var totalVectors = await _vectorStore.GetVectorCountAsync();
            progress?.Report($"Reindex complete: {totalVectors} vectors");
            AppLogger.Info("HybridSearch", $"Full reindex complete: {totalVectors} vectors for {projectPath}");
        }

        // ── Private Helpers ─────────────────────────────────────────────

        private async Task<FeatureContextResult?> BuildResultFromRanked(
            List<KeyValuePair<string, (double score, float vectorScore, float keywordScore)>> ranked,
            List<FeatureEntry> allFeatures, string projectPath,
            FeatureDependencyGraph graph)
        {
            var matchedFeatures = new List<MatchedFeature>();
            var primaryFeatures = new List<FeatureEntry>();

            foreach (var (featureId, (score, _, _)) in ranked)
            {
                if (score < EmbeddingConstants.MinVectorScore) continue;

                var entry = allFeatures.FirstOrDefault(f =>
                    f.Id.Equals(featureId, StringComparison.OrdinalIgnoreCase));
                if (entry == null) continue;

                matchedFeatures.Add(new MatchedFeature
                {
                    FeatureId = entry.Id,
                    FeatureName = entry.Name,
                    Confidence = Math.Min(1.0, score)
                });
                primaryFeatures.Add(entry);

                // Refresh stale signatures
                await _registryManager.RefreshStaleSignaturesAsync(projectPath, entry);
            }

            if (primaryFeatures.Count == 0) return null;

            // Tree expansion for secondary features
            var secondaryFeatures = new List<FeatureEntry>();
            var primaryIds = new HashSet<string>(primaryFeatures.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var feature in primaryFeatures.Where(f =>
                matchedFeatures.First(m => m.FeatureId == f.Id).Confidence >= 0.7))
            {
                try
                {
                    var neighbors = _registryManager.GetFeatureWithDependencies(feature.Id, allFeatures, graph, maxDepth: 1);
                    foreach (var neighbor in neighbors)
                    {
                        if (!primaryIds.Contains(neighbor.Id) && secondaryFeatures.All(s => s.Id != neighbor.Id))
                            secondaryFeatures.Add(neighbor);
                    }
                }
                catch { /* non-critical */ }
            }

            var contextBlock = _registryManager.BuildFeatureContextBlock(
                primaryFeatures,
                secondaryFeatures.Count > 0 ? secondaryFeatures : null);

            return new FeatureContextResult
            {
                RelevantFeatures = matchedFeatures,
                IsNewFeature = false,
                ContextBlock = contextBlock
            };
        }

        public void Dispose()
        {
            _vectorStore.Dispose();
            _embeddingService.Dispose();
        }
    }
}
