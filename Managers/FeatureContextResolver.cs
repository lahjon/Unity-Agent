using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// At task start, matches the task description to relevant features in the registry
    /// and builds a context block for prompt injection. Uses a fast Haiku call to confirm
    /// relevance and detect new features. Degrades gracefully — never blocks task launch.
    /// </summary>
    public class FeatureContextResolver
    {
        /// <summary>
        /// Builds a module-level preamble when two or more resolved features share a module.
        /// Returns null if no modules qualify or module lookup fails.
        /// </summary>
        private async Task<string?> BuildModulePreambleAsync(
            string projectPath,
            List<FeatureEntry> primaryFeatures)
        {
            if (_moduleRegistryManager == null || primaryFeatures.Count < 2)
                return null;

            try
            {
                // Group features by module, ignoring those without a parent module
                var byModule = primaryFeatures
                    .Where(f => !string.IsNullOrEmpty(f.ParentModuleId))
                    .GroupBy(f => f.ParentModuleId!)
                    .Where(g => g.Count() >= 2)
                    .ToList();

                if (byModule.Count == 0)
                    return null;

                var sb = new System.Text.StringBuilder();

                foreach (var group in byModule)
                {
                    var module = await _moduleRegistryManager.LoadModuleAsync(projectPath, group.Key);
                    if (module == null)
                        continue;

                    sb.AppendLine($"## Module: {module.Name}");
                    if (!string.IsNullOrWhiteSpace(module.Description))
                        sb.AppendLine(module.Description);
                    sb.AppendLine($"**Features in this task:** {string.Join(", ", group.Select(f => f.Name))}");
                    sb.AppendLine();
                }

                var result = sb.ToString();
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureContextResolver",
                    $"Module preamble build failed, continuing without: {ex.Message}");
                return null;
            }
        }

        private const string ResolverJsonSchema =
            """{"type":"object","properties":{"relevant_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"confidence":{"type":"number"}},"required":["id","confidence"]}},"is_new_feature":{"type":"boolean"},"new_feature_name":{"type":"string"},"new_feature_id":{"type":"string"},"new_feature_keywords":{"type":"array","items":{"type":"string"}}},"required":["relevant_features","is_new_feature"]}""";

        private const double MinConfidenceThreshold = 0.3;
        private const double TreeExpansionConfidenceThreshold = 0.7;
        private const double SiblingScoreMultiplier = 0.5;
        private static readonly TimeSpan HaikuTimeout = TimeSpan.FromMinutes(2);

        private const int EmbeddingCacheMaxEntries = 200;
        private static readonly TimeSpan EmbeddingCacheTtl = TimeSpan.FromMinutes(30);

        private readonly FeatureRegistryManager _registryManager;
        private readonly CodebaseIndexManager? _codebaseIndexManager;
        private readonly ModuleRegistryManager? _moduleRegistryManager;
        private readonly EmbeddingService? _embeddingService;
        private readonly VectorStore? _vectorStore;

        // Cache for task-description embeddings (keyed by description text, evicted by TTL + LRU)
        private readonly Dictionary<string, (float[] Vector, DateTime CachedAt)> _taskEmbeddingCache = new();
        // Cache for feature-description embeddings (keyed by feature ID, invalidated on save)
        private readonly Dictionary<string, float[]> _featureEmbeddingCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new();

        public FeatureContextResolver(
            FeatureRegistryManager registryManager,
            CodebaseIndexManager? codebaseIndexManager = null,
            ModuleRegistryManager? moduleRegistryManager = null,
            EmbeddingService? embeddingService = null,
            VectorStore? vectorStore = null)
        {
            _registryManager = registryManager;
            _codebaseIndexManager = codebaseIndexManager;
            _moduleRegistryManager = moduleRegistryManager;
            _embeddingService = embeddingService;
            _vectorStore = vectorStore;

            // Invalidate feature embedding cache when features are saved
            _registryManager.FeatureSaved += InvalidateFeatureCache;
        }

        /// <summary>
        /// Invalidates cached feature embedding when a feature is saved/updated.
        /// </summary>
        public void InvalidateFeatureCache(string featureId)
        {
            lock (_cacheLock)
            {
                _featureEmbeddingCache.Remove(featureId);
            }
        }

        /// <summary>
        /// Clears all cached embeddings.
        /// </summary>
        public void ClearEmbeddingCaches()
        {
            lock (_cacheLock)
            {
                _taskEmbeddingCache.Clear();
                _featureEmbeddingCache.Clear();
            }
        }

        private float[]? GetCachedTaskEmbedding(string taskDescription)
        {
            lock (_cacheLock)
            {
                if (_taskEmbeddingCache.TryGetValue(taskDescription, out var entry))
                {
                    if (DateTime.UtcNow - entry.CachedAt < EmbeddingCacheTtl)
                        return entry.Vector;
                    _taskEmbeddingCache.Remove(taskDescription);
                }
                return null;
            }
        }

        private void CacheTaskEmbedding(string taskDescription, float[] vector)
        {
            lock (_cacheLock)
            {
                // Evict expired entries first
                var expired = _taskEmbeddingCache
                    .Where(kv => DateTime.UtcNow - kv.Value.CachedAt >= EmbeddingCacheTtl)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var key in expired)
                    _taskEmbeddingCache.Remove(key);

                // LRU eviction: remove oldest entries if over capacity
                while (_taskEmbeddingCache.Count >= EmbeddingCacheMaxEntries)
                {
                    var oldest = _taskEmbeddingCache
                        .OrderBy(kv => kv.Value.CachedAt)
                        .First().Key;
                    _taskEmbeddingCache.Remove(oldest);
                }

                _taskEmbeddingCache[taskDescription] = (vector, DateTime.UtcNow);
            }
        }

        private float[]? GetCachedFeatureEmbedding(string featureId)
        {
            lock (_cacheLock)
            {
                return _featureEmbeddingCache.TryGetValue(featureId, out var vector) ? vector : null;
            }
        }

        private void CacheFeatureEmbedding(string featureId, float[] vector)
        {
            lock (_cacheLock)
            {
                _featureEmbeddingCache[featureId] = vector;
            }
        }

        /// <summary>
        /// Resolves which features are relevant to the given task and builds a context block
        /// for prompt injection. Returns null if no registry exists, no features match, or
        /// an error occurs.
        /// </summary>
        public async Task<FeatureContextResult?> ResolveAsync(
            string projectPath, string taskDescription, CancellationToken ct = default)
        {
            try
            {
                if (!_registryManager.RegistryExists(projectPath))
                    return null;

                var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
                if (allFeatures.Count == 0)
                    return null;

                // Load symbol index for enhanced matching (nullable — degrades gracefully)
                CodebaseSymbolIndex? symbolIndex = null;
                if (_codebaseIndexManager != null)
                {
                    try { symbolIndex = await _codebaseIndexManager.LoadIndexAsync(projectPath); }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("FeatureContextResolver", $"Symbol index load failed, continuing without: {ex.Message}");
                    }
                }

                // Load feature index for keyword map lookup
                var featureIndex = await _registryManager.LoadIndexAsync(projectPath);

                // Enhanced pre-filter: keyword + symbol matching (uses keyword map for O(1) candidate lookup)
                var candidates = _registryManager.FindMatchingFeaturesEnhanced(
                    taskDescription, allFeatures, symbolIndex, FeatureConstants.MaxFeaturesPerTask,
                    _codebaseIndexManager, featureIndex);

                // Add module siblings as lower-confidence candidates
                if (_moduleRegistryManager != null && candidates.Count > 0)
                {
                    try
                    {
                        var allModules = await _moduleRegistryManager.LoadAllModulesAsync(projectPath);
                        if (allModules.Count > 0)
                        {
                            var candidateIds = new HashSet<string>(candidates.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                            var siblingCandidates = new List<FeatureEntry>();

                            foreach (var candidate in candidates)
                            {
                                if (string.IsNullOrEmpty(candidate.ParentModuleId))
                                    continue;

                                var siblings = ModuleRegistryManager.GetRelatedFeaturesViaModule(
                                    candidate.Id, allFeatures, allModules);

                                foreach (var sibling in siblings)
                                {
                                    if (candidateIds.Add(sibling.Id))
                                        siblingCandidates.Add(sibling);
                                }
                            }

                            candidates.AddRange(siblingCandidates);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("FeatureContextResolver", $"Module sibling lookup failed, continuing: {ex.Message}");
                    }
                }

                bool isLikelyNewFeature = candidates.Count == 0;

                // Fast-path: skip Haiku call when pre-filter scores are unambiguous
                if (candidates.Count > 0 && candidates.Count <= FeatureConstants.FastPathMaxCandidates)
                {
                    var tokens = FeatureRegistryManager.Tokenize(taskDescription);
                    var scoredCandidates = candidates
                        .Select(f => (Feature: f, Score: FeatureRegistryManager.ScoreFeature(tokens, taskDescription, f)))
                        .ToList();

                    if (scoredCandidates.All(s => s.Score >= FeatureConstants.FastPathConfidenceThreshold))
                    {
                        AppLogger.Info("FeatureContextResolver",
                            $"Fast-path: skipping Haiku call — {scoredCandidates.Count} candidate(s) above threshold " +
                            $"({string.Join(", ", scoredCandidates.Select(s => $"{s.Feature.Id}={s.Score:F2}"))})");

                        var fastPathFeatures = scoredCandidates
                            .OrderByDescending(s => s.Score)
                            .Select(s => new MatchedFeature
                            {
                                FeatureId = s.Feature.Id,
                                FeatureName = s.Feature.Name,
                                Confidence = 1.0
                            })
                            .ToList();

                        // Refresh stale signatures
                        foreach (var entry in candidates)
                            await _registryManager.RefreshStaleSignaturesAsync(projectPath, entry);

                        // Tree expansion for fast-path confirmed features
                        var fpSecondary = new List<FeatureEntry>();
                        var fpPrimaryIds = new HashSet<string>(candidates.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            var graph = _registryManager.BuildDependencyGraph(allFeatures);
                            foreach (var c in candidates)
                            {
                                var neighborhood = _registryManager.GetFeatureWithDependencies(c.Id, allFeatures, graph, maxDepth: 1);
                                foreach (var neighbor in neighborhood)
                                {
                                    if (!fpPrimaryIds.Contains(neighbor.Id) && fpSecondary.All(s => s.Id != neighbor.Id))
                                        fpSecondary.Add(neighbor);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("FeatureContextResolver", $"Fast-path tree expansion failed: {ex.Message}");
                        }

                        var fpModulePreamble = await BuildModulePreambleAsync(projectPath, candidates);
                        var fastContextBlock = _registryManager.BuildFeatureContextBlock(
                            candidates, fpSecondary.Count > 0 ? fpSecondary : null, fpModulePreamble,
                            fastPathFeatures);

                        return new FeatureContextResult
                        {
                            RelevantFeatures = fastPathFeatures,
                            IsNewFeature = false,
                            ContextBlock = fastContextBlock
                        };
                    }
                }

                // ── Local vector search path: skip Haiku entirely when embeddings are available ──
                if (_embeddingService?.IsAvailable == true && _vectorStore?.IsInitialized == true)
                {
                    var vectorResult = await TryLocalVectorResolveAsync(
                        projectPath, taskDescription, allFeatures, candidates, ct);
                    if (vectorResult != null)
                        return vectorResult;
                    // Fall through to Haiku if vector search confidence was too low
                }

                // Build candidates JSON for Haiku — includes symbolNames for symbol-level matching
                var candidatesArray = candidates.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    description = f.Description,
                    keywords = f.Keywords,
                    symbolNames = f.SymbolNames,
                    primaryFiles = f.PrimaryFiles.Select(Path.GetFileName).Distinct().ToList()
                });
                var candidatesJson = JsonSerializer.Serialize(candidatesArray,
                    new JsonSerializerOptions { WriteIndented = false });

                // Load prompt template and format
                var template = PromptLoader.Load("FeatureContextResolverPrompt.md");
                var prompt = string.Format(template, taskDescription, candidatesJson);

                // Call Haiku CLI for intelligent matching
                AppLogger.Info("FeatureContextResolver", $"Calling Haiku CLI for feature resolution. Prompt length: {prompt.Length} chars");

                var rootResult = await FeatureSystemCliRunner.RunAsync(
                    prompt, ResolverJsonSchema, "FeatureContextResolver", HaikuTimeout, ct);

                if (rootResult is null)
                    return null;

                var root = rootResult.Value;

                // Parse relevant features from the response
                var relevantFeatures = new List<MatchedFeature>();
                if (root.TryGetProperty("relevant_features", out var featuresArray))
                {
                    foreach (var item in featuresArray.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetString() ?? "";
                        var confidence = item.GetProperty("confidence").GetDouble();

                        if (confidence < MinConfidenceThreshold)
                            continue;

                        // Look up the full feature entry for the display name
                        var entry = allFeatures.FirstOrDefault(f =>
                            string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (entry is null)
                            continue;

                        relevantFeatures.Add(new MatchedFeature
                        {
                            FeatureId = entry.Id,
                            FeatureName = entry.Name,
                            Confidence = confidence
                        });
                    }
                }

                // Order by confidence descending
                relevantFeatures = relevantFeatures
                    .OrderByDescending(f => f.Confidence)
                    .ToList();

                // Refresh stale signatures for confirmed features before building context
                var confirmedEntries = relevantFeatures
                    .Select(mf => allFeatures.FirstOrDefault(f => f.Id == mf.FeatureId))
                    .Where(e => e is not null)
                    .Cast<FeatureEntry>()
                    .ToList();

                foreach (var entry in confirmedEntries)
                    await _registryManager.RefreshStaleSignaturesAsync(projectPath, entry);

                // Tree expansion: for high-confidence features, include dependencies as secondary context
                var primaryFeatures = confirmedEntries;
                var secondaryFeatures = new List<FeatureEntry>();

                var highConfidenceIds = relevantFeatures
                    .Where(f => f.Confidence >= TreeExpansionConfidenceThreshold)
                    .Select(f => f.FeatureId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (highConfidenceIds.Count > 0)
                {
                    var primaryIds = new HashSet<string>(
                        confirmedEntries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

                    try
                    {
                        var graph = _registryManager.BuildDependencyGraph(allFeatures);

                        foreach (var featureId in highConfidenceIds)
                        {
                            var neighborhood = _registryManager.GetFeatureWithDependencies(
                                featureId, allFeatures, graph, maxDepth: 1);

                            foreach (var neighbor in neighborhood)
                            {
                                if (!primaryIds.Contains(neighbor.Id) &&
                                    secondaryFeatures.All(s => s.Id != neighbor.Id))
                                {
                                    secondaryFeatures.Add(neighbor);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("FeatureContextResolver",
                            $"Dependency tree expansion failed, continuing without: {ex.Message}");
                    }

                    // Also add module siblings as secondary if not already primary
                    if (_moduleRegistryManager != null)
                    {
                        try
                        {
                            var allModules = await _moduleRegistryManager.LoadAllModulesAsync(projectPath);
                            if (allModules.Count > 0)
                            {
                                foreach (var featureId in highConfidenceIds)
                                {
                                    var siblings = ModuleRegistryManager.GetRelatedFeaturesViaModule(
                                        featureId, allFeatures, allModules);

                                    foreach (var sibling in siblings)
                                    {
                                        if (!primaryIds.Contains(sibling.Id) &&
                                            secondaryFeatures.All(s => s.Id != sibling.Id))
                                        {
                                            secondaryFeatures.Add(sibling);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("FeatureContextResolver",
                                $"Module sibling expansion failed, continuing without: {ex.Message}");
                        }
                    }
                }

                // Build module-level preamble when multiple features share a module
                var modulePreamble = await BuildModulePreambleAsync(projectPath, primaryFeatures);

                // Build context block with primary and secondary features
                var contextBlock = _registryManager.BuildFeatureContextBlock(
                    primaryFeatures, secondaryFeatures.Count > 0 ? secondaryFeatures : null, modulePreamble,
                    relevantFeatures);

                // Check if Haiku identified a new feature
                var isNewFeature = root.TryGetProperty("is_new_feature", out var newFlag) && newFlag.GetBoolean();
                string? suggestedName = null;
                List<string>? suggestedKeywords = null;

                string? suggestedId = null;
                if (isNewFeature)
                {
                    suggestedName = root.TryGetProperty("new_feature_name", out var nameEl)
                        ? nameEl.GetString()
                        : null;

                    suggestedId = root.TryGetProperty("new_feature_id", out var idEl)
                        ? idEl.GetString()
                        : null;

                    if (root.TryGetProperty("new_feature_keywords", out var kwArray))
                    {
                        suggestedKeywords = new List<string>();
                        foreach (var kw in kwArray.EnumerateArray())
                        {
                            var val = kw.GetString();
                            if (!string.IsNullOrWhiteSpace(val))
                                suggestedKeywords.Add(val);
                        }
                    }
                }

                if (relevantFeatures.Count > 0)
                {
                    var featureSummary = string.Join(", ", relevantFeatures.Select(f => $"{f.FeatureId} ({f.Confidence:F2})"));
                    AppLogger.Info("FeatureContextResolver", $"Feature context resolved: {featureSummary}");
                }
                else
                {
                    AppLogger.Info("FeatureContextResolver", "Feature context resolved: no matching features found");
                }

                return new FeatureContextResult
                {
                    RelevantFeatures = relevantFeatures,
                    IsNewFeature = isNewFeature,
                    SuggestedNewFeatureId = suggestedId,
                    SuggestedNewFeatureName = suggestedName,
                    SuggestedKeywords = suggestedKeywords,
                    ContextBlock = contextBlock
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureContextResolver", $"Feature resolution failed, skipping context injection: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Attempts to resolve features using local vector similarity search over pre-computed
        /// embeddings. Returns null if confidence is too low (caller should fall back to Haiku).
        /// This eliminates the 20-40s Haiku call for ~80% of tasks.
        /// </summary>
        private async Task<FeatureContextResult?> TryLocalVectorResolveAsync(
            string projectPath, string taskDescription,
            List<FeatureEntry> allFeatures, List<FeatureEntry> keywordCandidates,
            CancellationToken ct)
        {
            try
            {
                // Check cache before calling embedding API
                var queryEmbedding = GetCachedTaskEmbedding(taskDescription);
                if (queryEmbedding == null)
                {
                    queryEmbedding = await _embeddingService!.EmbedQueryAsync(
                        taskDescription, EmbeddingConstants.VoyageCodeModel, ct);
                    if (queryEmbedding == null)
                        return null;
                    CacheTaskEmbedding(taskDescription, queryEmbedding);
                }

                // Search for matching feature vectors (descriptions + signatures)
                var vectorResults = await _vectorStore!.SearchAsync(
                    queryEmbedding, EmbeddingConstants.DefaultTopK * 2,
                    new[] { EmbeddingConstants.CategoryFeatureDesc, EmbeddingConstants.CategoryFeatureSigs },
                    EmbeddingConstants.MinVectorScore);

                // Fuse vector scores with keyword scores
                var tokens = FeatureRegistryManager.Tokenize(taskDescription);
                var keywordScoreMap = keywordCandidates.ToDictionary(
                    f => f.Id,
                    f => FeatureRegistryManager.ScoreFeature(tokens, taskDescription, f),
                    StringComparer.OrdinalIgnoreCase);

                var maxKw = keywordScoreMap.Values.Count > 0 ? keywordScoreMap.Values.Max() : 1.0;
                if (maxKw <= 0) maxKw = 1.0;

                // Build candidate set from both vector hits and keyword hits
                var candidateIds = vectorResults
                    .Where(r => r.FeatureId != null)
                    .Select(r => r.FeatureId!)
                    .Union(keywordCandidates.Select(f => f.Id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var fusedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (var featureId in candidateIds)
                {
                    var vectorScore = vectorResults
                        .Where(r => r.FeatureId != null &&
                                    r.FeatureId.Equals(featureId, StringComparison.OrdinalIgnoreCase))
                        .Select(r => (float?)r.VectorScore)
                        .Max() ?? 0f;

                    var kwScore = keywordScoreMap.TryGetValue(featureId, out var ks)
                        ? (float)(ks / maxKw) : 0f;

                    // Symbol match boost
                    var symbolScore = keywordCandidates.Any(f =>
                        f.Id.Equals(featureId, StringComparison.OrdinalIgnoreCase)
                        && f.SymbolNames?.Count > 0)
                        ? (float)FeatureConstants.SymbolMatchScoreBoost : 0f;

                    var fused =
                        vectorScore * EmbeddingConstants.FusionWeightVector +
                        kwScore * EmbeddingConstants.FusionWeightKeyword +
                        symbolScore * EmbeddingConstants.FusionWeightSymbol;

                    fusedScores[featureId] = fused;
                }

                var ranked = fusedScores
                    .OrderByDescending(kv => kv.Value)
                    .Take(FeatureConstants.MaxFeaturesPerTask)
                    .ToList();

                if (ranked.Count == 0)
                    return null;

                var topScore = ranked[0].Value;

                // Only use vector results if confidence is sufficient to skip Haiku
                if (topScore < EmbeddingConstants.HaikuSkipMediumConfidence)
                {
                    AppLogger.Info("FeatureContextResolver",
                        $"Vector search low confidence ({topScore:F3}) — falling back to Haiku");
                    return null;
                }

                AppLogger.Info("FeatureContextResolver",
                    $"Vector search resolved features (score={topScore:F3}) — skipping Haiku call");

                var matchedFeatures = new List<MatchedFeature>();
                var primaryFeatures = new List<FeatureEntry>();

                foreach (var (featureId, score) in ranked)
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
                }

                if (primaryFeatures.Count == 0)
                    return null;

                // Refresh stale signatures
                foreach (var entry in primaryFeatures)
                    await _registryManager.RefreshStaleSignaturesAsync(projectPath, entry);

                // Tree expansion for high-confidence features
                var secondaryFeatures = new List<FeatureEntry>();
                var primaryIds = new HashSet<string>(
                    primaryFeatures.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

                try
                {
                    var graph = _registryManager.BuildDependencyGraph(allFeatures);
                    foreach (var feature in primaryFeatures.Where(f =>
                        matchedFeatures.First(m => m.FeatureId == f.Id).Confidence >= TreeExpansionConfidenceThreshold))
                    {
                        var neighbors = _registryManager.GetFeatureWithDependencies(
                            feature.Id, allFeatures, graph, maxDepth: 1);
                        foreach (var neighbor in neighbors)
                        {
                            if (!primaryIds.Contains(neighbor.Id) &&
                                secondaryFeatures.All(s => s.Id != neighbor.Id))
                                secondaryFeatures.Add(neighbor);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureContextResolver",
                        $"Vector path tree expansion failed: {ex.Message}");
                }

                var modulePreamble = await BuildModulePreambleAsync(projectPath, primaryFeatures);
                var contextBlock = _registryManager.BuildFeatureContextBlock(
                    primaryFeatures, secondaryFeatures.Count > 0 ? secondaryFeatures : null, modulePreamble,
                    matchedFeatures);

                return new FeatureContextResult
                {
                    RelevantFeatures = matchedFeatures,
                    IsNewFeature = false,
                    ContextBlock = contextBlock
                };
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureContextResolver",
                    $"Local vector resolve failed, will fall back to Haiku: {ex.Message}");
                return null;
            }
        }
    }
}
