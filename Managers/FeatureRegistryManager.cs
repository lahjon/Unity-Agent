using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Central persistence and search manager for the Feature System.
    /// Handles loading/saving feature files from <c>.spritely/features/</c>,
    /// keyword-based search, and signature staleness detection.
    /// </summary>
    public class FeatureRegistryManager
    {
        /// <summary>
        /// Raised after a feature is saved. Argument is the feature ID.
        /// Used by FeatureContextResolver to invalidate embedding caches.
        /// </summary>
        public event Action<string>? FeatureSaved;

        internal static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could",
            "should", "may", "might", "shall", "can", "need", "must", "to", "of",
            "in", "for", "on", "with", "at", "by", "from", "as", "into", "through",
            "during", "before", "after", "it", "its", "this", "that", "these", "those",
            "i", "we", "you", "they", "he", "she", "and", "or", "but", "not", "so",
            "if", "then", "than", "when", "where", "how", "what", "which", "who",
            "all", "each", "every", "both", "few", "more", "most", "some", "any", "no",
            "make", "made", "just", "also", "very", "please", "add", "fix", "update",
            "change", "modify", "implement", "create", "remove", "delete"
        };

        internal static readonly Regex TokenSplitRegex = new(@"[\s\p{P}]+", RegexOptions.Compiled);

        // ── Persistence ──────────────────────────────────────────────────

        /// <summary>Checks if the feature database exists for the given project.</summary>
        public bool RegistryExists(string projectPath)
            => FeatureDatabase.DatabaseExists(projectPath);

        /// <summary>Returns the absolute path to the features directory.</summary>
        public string GetFeaturesPath(string projectPath)
            => FeatureDatabase.GetFeaturesDir(projectPath);

        /// <summary>
        /// Builds a <see cref="FeatureIndex"/> in memory from the JSONL database.
        /// The keyword map is computed at load time (not persisted) to avoid merge conflicts.
        /// </summary>
        public async Task<FeatureIndex> LoadIndexAsync(string projectPath)
        {
            try
            {
                await FeatureDatabase.MigrateIfNeededAsync(projectPath);

                var features = await FeatureDatabase.LoadFeaturesAsync(projectPath);
                var metadata = await FeatureDatabase.LoadMetadataAsync(projectPath);

                var index = new FeatureIndex
                {
                    Version = metadata.Version,
                    SymbolIndexVersion = metadata.SymbolIndexVersion,
                    Features = features.Select(f => new FeatureIndexEntry
                    {
                        Id = f.Id,
                        Name = f.Name,
                        Category = f.Category,
                        TouchCount = f.TouchCount,
                        PrimaryFileCount = f.PrimaryFiles.Count,
                        LastIndexedAt = f.LastUpdatedAt,
                        Keywords = new List<string>(f.Keywords),
                        Description = f.Description ?? ""
                    }).ToList()
                };

                // Load modules for inline index
                var modules = await FeatureDatabase.LoadModulesAsync(projectPath);
                index.Modules = modules.Select(m => new ModuleIndexEntry
                {
                    Id = m.Id,
                    Name = m.Name,
                    FeatureIds = new List<string>(m.FeatureIds)
                }).ToList();

                RebuildKeywordMap(index);
                return index;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to load index: {ex.Message}", ex);
                return new FeatureIndex();
            }
        }

        /// <summary>Loads a single feature entry by id from the JSONL database.</summary>
        public async Task<FeatureEntry?> LoadFeatureAsync(string projectPath, string featureId)
        {
            try
            {
                await FeatureDatabase.MigrateIfNeededAsync(projectPath);
                return await FeatureDatabase.LoadFeatureByIdAsync(projectPath, featureId);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to load feature '{featureId}': {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>Loads all features from the JSONL database.</summary>
        public async Task<List<FeatureEntry>> LoadAllFeaturesAsync(string projectPath)
        {
            try
            {
                await FeatureDatabase.MigrateIfNeededAsync(projectPath);
                var features = await FeatureDatabase.LoadFeaturesAsync(projectPath);
                await CleanupStalePlaceholdersAsync(projectPath, features);
                return features;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to load all features: {ex.Message}", ex);
                return new List<FeatureEntry>();
            }
        }

        /// <summary>
        /// Removes placeholders older than <see cref="FeatureConstants.PlaceholderExpiryDays"/>
        /// with zero files. Placeholders with files are promoted via signature extraction.
        /// </summary>
        private async Task CleanupStalePlaceholdersAsync(string projectPath, List<FeatureEntry> features)
        {
            var cutoff = DateTime.UtcNow.AddDays(-FeatureConstants.PlaceholderExpiryDays);
            var toRemove = new List<FeatureEntry>();

            foreach (var feature in features)
            {
                if (feature.PlaceholderCreatedAt is null || feature.PlaceholderCreatedAt > cutoff)
                    continue;

                var hasFiles = feature.PrimaryFiles.Count > 0 || feature.SecondaryFiles.Count > 0;

                if (!hasFiles)
                {
                    toRemove.Add(feature);
                    continue;
                }

                // Placeholder has files — promote to full feature
                try
                {
                    await PromotePlaceholderAsync(projectPath, feature);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureRegistry",
                        $"Failed to promote placeholder '{feature.Id}': {ex.Message}", ex);
                }
            }

            foreach (var feature in toRemove)
            {
                try
                {
                    await RemoveFeatureAsync(projectPath, feature.Id);
                    features.Remove(feature);
                    AppLogger.Info("FeatureRegistry",
                        $"Expired stale placeholder '{feature.Id}' (no files, created {feature.PlaceholderCreatedAt:u})");
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureRegistry",
                        $"Failed to remove expired placeholder '{feature.Id}': {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Promotes a placeholder to a full feature by extracting signatures and clearing PlaceholderCreatedAt.
        /// </summary>
        private async Task PromotePlaceholderAsync(string projectPath, FeatureEntry feature)
        {
            foreach (var relPath in feature.PrimaryFiles)
            {
                if (feature.Context.Signatures.ContainsKey(relPath))
                    continue;

                var absPath = Path.Combine(projectPath, relPath);
                if (!File.Exists(absPath))
                    continue;

                var content = SignatureExtractor.ExtractSignatures(absPath);
                if (!string.IsNullOrEmpty(content))
                {
                    feature.Context.Signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = content
                    };
                }

                foreach (var sym in SignatureExtractor.GetSymbolNames(absPath))
                {
                    if (!feature.SymbolNames.Contains(sym, StringComparer.OrdinalIgnoreCase))
                        feature.SymbolNames.Add(sym);
                }
            }

            feature.PlaceholderCreatedAt = null;
            feature.LastUpdatedAt = DateTime.UtcNow;
            await SaveFeatureAsync(projectPath, feature);

            AppLogger.Info("FeatureRegistry",
                $"Promoted placeholder '{feature.Id}' to full feature ({feature.PrimaryFiles.Count} files)");
        }

        /// <summary>
        /// Saves a feature to the JSONL database (upsert).
        /// All <see cref="List{T}"/> properties are sorted alphabetically before serialization.
        /// </summary>
        public async Task SaveFeatureAsync(string projectPath, FeatureEntry feature)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                var hadSymbolNames = feature.SymbolNames.Count > 0;
                PopulateSymbolNamesIfEmpty(feature);
                if (!hadSymbolNames && feature.SymbolNames.Count > 0)
                    feature.SymbolNames = FilterSymbolNames(feature.SymbolNames, feature);
                SortFeatureLists(feature);

                await FeatureDatabase.UpsertFeatureAsync(projectPath, feature);

                FeatureSaved?.Invoke(feature.Id);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to save feature '{feature.Id}': {ex.Message}", ex);
            }
        }

        /// <summary>Removes a feature from the JSONL database.</summary>
        public async Task RemoveFeatureAsync(string projectPath, string featureId)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                await FeatureDatabase.RemoveFeatureAsync(projectPath, featureId);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to remove feature '{featureId}': {ex.Message}", ex);
            }
        }

        /// <summary>Saves the metadata (symbolIndexVersion). The index is computed at load time.</summary>
        public async Task SaveIndexAsync(string projectPath, FeatureIndex index)
        {
            using var guard = AcquireMutex(projectPath);
            var metadata = await FeatureDatabase.LoadMetadataAsync(projectPath);
            metadata.SymbolIndexVersion = index.SymbolIndexVersion;
            await FeatureDatabase.SaveMetadataAsync(projectPath, metadata);
        }

        /// <summary>
        /// Rebuilds the codebase symbol index from all known features without
        /// resetting feature metadata. Delegates to <see cref="CodebaseIndexManager.BuildIndexAsync"/>.
        /// </summary>
        public async Task RebuildSymbolIndexAsync(string projectPath, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            progress?.Report("Loading features...");
            var features = await LoadAllFeaturesAsync(projectPath);
            var indexManager = new CodebaseIndexManager();
            await indexManager.BuildIndexAsync(projectPath, features, ct, progress);

            // Persist the version hash so startup can detect staleness
            var versionHash = ComputeSymbolIndexVersion(features);
            var index = await LoadIndexAsync(projectPath);
            index.SymbolIndexVersion = versionHash;
            await SaveIndexAsync(projectPath, index);
        }

        /// <summary>
        /// Loads the persisted symbol index if it exists and is not stale.
        /// Returns null when the index is missing or the version hash no longer matches,
        /// indicating a full rebuild is required. Avoids O(n) file re-scan on app start.
        /// </summary>
        public async Task<CodebaseSymbolIndex?> LoadSymbolIndexAsync(string projectPath)
        {
            var featureIndex = await LoadIndexAsync(projectPath);
            if (string.IsNullOrEmpty(featureIndex.SymbolIndexVersion))
                return null; // No version recorded — must rebuild

            var codebaseIndexManager = new CodebaseIndexManager();
            var symbolIndex = await codebaseIndexManager.LoadIndexAsync(projectPath);
            if (symbolIndex == null)
                return null; // Index file missing

            // Verify staleness: recompute version from current feature hashes
            var features = await LoadAllFeaturesAsync(projectPath);
            var currentVersion = ComputeSymbolIndexVersion(features);

            if (currentVersion != featureIndex.SymbolIndexVersion)
            {
                AppLogger.Info("FeatureRegistry",
                    $"Symbol index stale (stored={featureIndex.SymbolIndexVersion[..8]}, current={currentVersion[..8]}) — rebuild required");
                return null;
            }

            return symbolIndex;
        }

        /// <summary>
        /// Computes a version hash for the symbol index by hashing all file content hashes
        /// from feature signatures. Changes when any tracked source file is modified.
        /// </summary>
        public static string ComputeSymbolIndexVersion(List<FeatureEntry> features)
        {
            var allHashes = new List<string>();

            foreach (var feature in features)
            {
                foreach (var (filePath, sig) in feature.Context.Signatures)
                {
                    if (!string.IsNullOrEmpty(sig.Hash))
                        allHashes.Add($"{filePath}:{sig.Hash}");
                }
            }

            allHashes.Sort(StringComparer.Ordinal);
            var combined = string.Join("|", allHashes);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        // ── Search ───────────────────────────────────────────────────────

        /// <summary>
        /// Finds features matching a task description using local keyword matching.
        /// When a <paramref name="featureIndex"/> with a populated KeywordMap is provided,
        /// uses O(1) candidate lookup instead of iterating all features.
        /// </summary>
        public List<FeatureEntry> FindMatchingFeatures(
            string taskDescription,
            List<FeatureEntry> allFeatures,
            int maxResults = 5,
            FeatureIndex? featureIndex = null)
        {
            var tokens = Tokenize(taskDescription);
            if (tokens.Count == 0)
                return new List<FeatureEntry>();

            var candidates = GetCandidatesFromKeywordMap(tokens, allFeatures, featureIndex);

            var scored = new List<(FeatureEntry Feature, double Score)>();

            foreach (var feature in candidates)
            {
                var score = ScoreFeature(tokens, taskDescription, feature);
                if (score > 0.1)
                    scored.Add((feature, score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Feature.TouchCount)
                .Take(maxResults)
                .Select(s => s.Feature)
                .ToList();
        }

        /// <summary>
        /// Uses the keyword map for O(1) candidate lookup. Falls back to all features
        /// when the keyword map is empty or not provided.
        /// </summary>
        private static List<FeatureEntry> GetCandidatesFromKeywordMap(
            HashSet<string> tokens,
            List<FeatureEntry> allFeatures,
            FeatureIndex? featureIndex)
        {
            if (featureIndex?.KeywordMap == null || featureIndex.KeywordMap.Count == 0)
                return allFeatures;

            var candidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                if (featureIndex.KeywordMap.TryGetValue(token, out var featureIds))
                {
                    foreach (var id in featureIds)
                        candidateIds.Add(id);
                }
            }

            if (candidateIds.Count == 0)
                return allFeatures;

            return allFeatures.Where(f => candidateIds.Contains(f.Id)).ToList();
        }

        /// <summary>
        /// Finds features by symbol name using the codebase symbol index.
        /// Returns features ordered by match confidence (exact > prefix > contains).
        /// </summary>
        public List<FeatureEntry> FindMatchingFeaturesBySymbol(
            string symbolName,
            CodebaseSymbolIndex symbolIndex,
            List<FeatureEntry> allFeatures,
            CodebaseIndexManager? codebaseIndexManager = null)
        {
            if (string.IsNullOrWhiteSpace(symbolName) || symbolIndex?.Symbols == null)
                return new List<FeatureEntry>();

            var indexManager = codebaseIndexManager ?? new CodebaseIndexManager();
            var results = indexManager.LookupSymbol(symbolIndex, symbolName);

            if (results.Count == 0)
                return new List<FeatureEntry>();

            var featureLookup = allFeatures.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = new List<FeatureEntry>();

            foreach (var result in results)
            {
                if (!string.IsNullOrEmpty(result.FeatureId) &&
                    seen.Add(result.FeatureId) &&
                    featureLookup.TryGetValue(result.FeatureId, out var feature))
                {
                    matched.Add(feature);
                }
            }

            return matched;
        }

        /// <summary>
        /// Enhanced feature matching combining keyword scoring with symbol index lookups.
        /// Falls back to keyword-only <see cref="FindMatchingFeatures"/> when symbolIndex is null.
        /// </summary>
        public List<FeatureEntry> FindMatchingFeaturesEnhanced(
            string taskDescription,
            List<FeatureEntry> allFeatures,
            CodebaseSymbolIndex? symbolIndex,
            int maxResults = 8,
            CodebaseIndexManager? codebaseIndexManager = null,
            FeatureIndex? featureIndex = null)
        {
            if (symbolIndex == null)
                return FindMatchingFeatures(taskDescription, allFeatures, maxResults, featureIndex);

            var tokens = Tokenize(taskDescription);
            if (tokens.Count == 0)
                return new List<FeatureEntry>();

            // Use keyword map for O(1) candidate narrowing when available
            var candidates = GetCandidatesFromKeywordMap(tokens, allFeatures, featureIndex);

            // Score candidates with keyword + symbol scoring
            var scored = new Dictionary<string, (FeatureEntry Feature, double Score)>();

            foreach (var feature in candidates)
            {
                var score = ScoreFeature(tokens, taskDescription, feature);
                if (score > 0.1)
                    scored[feature.Id] = (feature, score);
            }

            // Boost features found via symbol index lookup
            var indexManager = codebaseIndexManager ?? new CodebaseIndexManager();
            foreach (var token in tokens)
            {
                var symbolResults = indexManager.LookupSymbol(symbolIndex, token);
                foreach (var result in symbolResults)
                {
                    if (string.IsNullOrEmpty(result.FeatureId))
                        continue;

                    if (scored.TryGetValue(result.FeatureId, out var existing))
                    {
                        // Boost existing score by symbol confidence
                        scored[result.FeatureId] = (existing.Feature,
                            existing.Score + result.Confidence * FeatureConstants.SymbolMatchScoreBoost);
                    }
                    else
                    {
                        var feature = allFeatures.FirstOrDefault(
                            f => string.Equals(f.Id, result.FeatureId, StringComparison.OrdinalIgnoreCase));
                        if (feature != null)
                        {
                            scored[result.FeatureId] = (feature,
                                result.Confidence * FeatureConstants.SymbolMatchScoreBoost);
                        }
                    }
                }
            }

            return scored.Values
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Feature.TouchCount)
                .Take(maxResults)
                .Select(s => s.Feature)
                .ToList();
        }

        /// <summary>
        /// Returns the feature and its dependency neighborhood up to <paramref name="maxDepth"/> hops.
        /// Includes both direct dependencies and features that depend on it.
        /// </summary>
        public List<FeatureEntry> GetFeatureWithDependencies(
            string featureId,
            List<FeatureEntry> allFeatures,
            FeatureDependencyGraph graph,
            int maxDepth = 2)
        {
            if (string.IsNullOrWhiteSpace(featureId))
                return new List<FeatureEntry>();

            var featureLookup = allFeatures.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
            var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { featureId };

            // BFS outward from the root feature in both directions
            var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { featureId };
            for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
            {
                var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in frontier)
                {
                    // Dependencies (what this feature depends on)
                    if (graph.DirectDependencies.TryGetValue(id, out var deps))
                    {
                        foreach (var dep in deps)
                        {
                            if (collected.Add(dep))
                                nextFrontier.Add(dep);
                        }
                    }

                    // Dependents (what depends on this feature)
                    if (graph.Dependents.TryGetValue(id, out var dependents))
                    {
                        foreach (var dependent in dependents)
                        {
                            if (collected.Add(dependent))
                                nextFrontier.Add(dependent);
                        }
                    }
                }

                frontier = nextFrontier;
            }

            // Bound the result to prevent unbounded expansion
            const int maxNeighborhood = 20;
            return collected
                .Where(id => featureLookup.ContainsKey(id))
                .Take(maxNeighborhood)
                .Select(id => featureLookup[id])
                .ToList();
        }

        // ── Context Building ─────────────────────────────────────────────

        /// <summary>
        /// Builds the markdown context block injected into prompts for relevant features.
        /// Primary features get full signatures; secondary features (dependencies, siblings)
        /// get compact summaries (name + files only). Respects per-feature and total token budgets.
        /// </summary>
        public string BuildFeatureContextBlock(
            List<FeatureEntry> features,
            List<FeatureEntry>? secondaryFeatures = null,
            string? modulePreamble = null,
            List<MatchedFeature>? matchedFeatures = null)
        {
            if (features.Count == 0)
                return string.Empty;

            var confidenceMap = matchedFeatures?.ToDictionary(
                m => m.FeatureId, m => m.Confidence, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("# FEATURE CONTEXT");
            sb.AppendLine("The following features are relevant to this task. Use this context to understand");
            sb.AppendLine("the architecture before making changes. Read the listed files for full details.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(modulePreamble))
            {
                sb.Append(modulePreamble);
                sb.AppendLine();
            }

            var totalCharsUsed = 0;
            var totalCharBudget = FeatureConstants.MaxTotalFeatureContextTokens * 4;

            // Primary features — full detail
            foreach (var feature in features)
            {
                var confidence = confidenceMap != null && confidenceMap.TryGetValue(feature.Id, out var c) ? c : (double?)null;
                var featureBlock = BuildSingleFeatureBlock(feature, confidence);
                var featureCharBudget = FeatureConstants.MaxTokensPerFeature * 4;

                if (featureBlock.Length > featureCharBudget)
                    featureBlock = TruncateAtMemberBoundary(featureBlock, featureCharBudget);

                if (totalCharsUsed + featureBlock.Length > totalCharBudget)
                    break;

                sb.Append(featureBlock);
                totalCharsUsed += featureBlock.Length;
            }

            // Secondary features — compact summaries (name + files only)
            if (secondaryFeatures is { Count: > 0 })
            {
                var secondaryCharBudget = FeatureConstants.MaxTokensPerSecondaryFeature * 4;
                var maxSecondary = FeatureConstants.MaxSecondaryFeaturesPerTask;
                var count = 0;

                sb.AppendLine("## Related Context (dependencies & siblings)");
                sb.AppendLine();

                foreach (var feature in secondaryFeatures)
                {
                    if (count >= maxSecondary)
                        break;

                    var compactBlock = BuildCompactFeatureBlock(feature);

                    if (compactBlock.Length > secondaryCharBudget)
                        compactBlock = TruncateAtMemberBoundary(compactBlock, secondaryCharBudget);

                    if (totalCharsUsed + compactBlock.Length > totalCharBudget)
                        break;

                    sb.Append(compactBlock);
                    totalCharsUsed += compactBlock.Length;
                    count++;
                }
            }

            return sb.ToString();
        }

        // ── Staleness ────────────────────────────────────────────────────

        /// <summary>
        /// Checks each file in the feature's signatures for hash staleness.
        /// Re-extracts signatures for any files whose content has changed.
        /// Returns true if any signatures were refreshed.
        /// </summary>
        public async Task<bool> RefreshStaleSignaturesAsync(string projectPath, FeatureEntry feature, DateTime? lastIndexedAt = null)
        {
            // Auto-resolve LastIndexedAt from the index if not explicitly provided
            if (!lastIndexedAt.HasValue)
            {
                var index = await LoadIndexAsync(projectPath);
                var indexEntry = index.Features.FirstOrDefault(f => f.Id == feature.Id);
                lastIndexedAt = indexEntry?.LastIndexedAt;
            }

            var anyRefreshed = false;

            var filesToUpdate = new List<(string relativePath, string absolutePath)>();

            foreach (var (relativePath, signature) in feature.Context.Signatures)
            {
                var absolutePath = Path.Combine(projectPath, relativePath);
                if (!File.Exists(absolutePath))
                    continue;

                // Skip files not modified since LastIndexedAt — avoids redundant hash computation
                if (lastIndexedAt.HasValue)
                {
                    var lastWrite = File.GetLastWriteTimeUtc(absolutePath);
                    if (lastWrite <= lastIndexedAt.Value)
                        continue;
                }

                var currentHash = SignatureExtractor.ComputeFileHash(absolutePath);
                if (currentHash != signature.Hash)
                    filesToUpdate.Add((relativePath, absolutePath));
            }

            foreach (var (relativePath, absolutePath) in filesToUpdate)
            {
                try
                {
                    var newHash = SignatureExtractor.ComputeFileHash(absolutePath);
                    var newContent = SignatureExtractor.ExtractSignatures(absolutePath);

                    feature.Context.Signatures[relativePath] = new FileSignature
                    {
                        Hash = newHash,
                        Content = newContent
                    };

                    anyRefreshed = true;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureRegistry", $"Failed to refresh signature for '{relativePath}': {ex.Message}", ex);
                }
            }

            if (anyRefreshed)
                await SaveFeatureAsync(projectPath, feature);

            return anyRefreshed;
        }

        // ── Dependency Graph ─────────────────────────────────────────────

        /// <summary>
        /// Builds the full dependency graph from loaded features.
        /// Computes transitive dependencies, dependents, and detects cycles.
        /// </summary>
        public FeatureDependencyGraph BuildDependencyGraph(List<FeatureEntry> features)
            => FeatureDependencyGraph.Build(features);

        /// <summary>
        /// Validates all DependsOn references point to existing features.
        /// Removes dangling references and logs warnings.
        /// </summary>
        public void ValidateDependencies(List<FeatureEntry> features)
        {
            var knownIds = new HashSet<string>(features.Select(f => f.Id));

            foreach (var feature in features)
            {
                var removed = feature.DependsOn.RemoveAll(depId =>
                {
                    if (knownIds.Contains(depId) && depId != feature.Id)
                        return false;

                    AppLogger.Debug("FeatureRegistry",
                        $"Removed dangling dependency '{depId}' from feature '{feature.Id}'");
                    return true;
                });

                if (removed > 0)
                    AppLogger.Info("FeatureRegistry",
                        $"Cleaned {removed} invalid dependency reference(s) from '{feature.Id}'");
            }
        }

        // ── Private Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the inverted keyword map from all index entries.
        /// Tokenizes each entry's keywords, name, and description, mapping each token to the feature IDs that contain it.
        /// </summary>
        private static void RebuildKeywordMap(FeatureIndex index)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in index.Features)
            {
                // Collect all text sources for this feature
                var textParts = new List<string> { entry.Name };
                if (!string.IsNullOrWhiteSpace(entry.Description))
                    textParts.Add(entry.Description);

                // Tokenize name + description
                var tokens = Tokenize(string.Join(" ", textParts));

                // Add raw keywords (lowered) — these bypass stopword filtering since they're curated
                foreach (var kw in entry.Keywords)
                {
                    var lower = kw.ToLowerInvariant().Trim();
                    if (lower.Length > 0)
                        tokens.Add(lower);
                }

                foreach (var token in tokens)
                {
                    if (!map.TryGetValue(token, out var list))
                    {
                        list = new List<string>();
                        map[token] = list;
                    }
                    if (!list.Contains(entry.Id))
                        list.Add(entry.Id);
                }
            }

            // Sort lists for deterministic serialization
            foreach (var list in map.Values)
                list.Sort(StringComparer.Ordinal);

            index.KeywordMap = new Dictionary<string, List<string>>(
                map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                   .ToDictionary(kv => kv.Key, kv => kv.Value));
        }

        /// <summary>Sorts all list properties on a feature entry for deterministic serialization.</summary>
        private static void SortFeatureLists(FeatureEntry feature)
        {
            feature.Keywords.Sort(StringComparer.Ordinal);
            feature.PrimaryFiles.Sort(StringComparer.Ordinal);
            feature.SecondaryFiles.Sort(StringComparer.Ordinal);
            feature.RelatedFeatureIds.Sort(StringComparer.Ordinal);
            feature.DependsOn.Sort(StringComparer.Ordinal);
            feature.ChildFeatureIds.Sort(StringComparer.Ordinal);
            feature.SymbolNames.Sort(StringComparer.Ordinal);
            feature.Context.KeyTypes.Sort(StringComparer.Ordinal);
            feature.Context.Patterns.Sort(StringComparer.Ordinal);
            feature.Context.Dependencies.Sort(StringComparer.Ordinal);
        }

        /// <summary>
        /// Extracts symbol names from signature text when SymbolNames is empty but signatures exist.
        /// Parses class/interface/enum names from compact signature content lines.
        /// </summary>
        /// <summary>Common short property/field names that produce false-positive matches against task descriptions.</summary>
        private static readonly HashSet<string> NoisySymbolNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Id", "Name", "Type", "Value", "Key", "Data", "Item", "Items", "Count",
            "Index", "Path", "Text", "Title", "Label", "Status", "State", "Error",
            "Result", "Source", "Target", "Content", "Version", "Description", "Category",
            "Options", "Config", "Settings", "Context", "Model", "View", "Action", "Event",
            "Build", "Dispose", "ToString", "GetHashCode", "Equals"
        };

        private static void PopulateSymbolNamesIfEmpty(FeatureEntry feature)
        {
            if (feature.SymbolNames.Count > 0 || feature.Context.Signatures.Count == 0)
                return;

            var symbolRegex = new Regex(
                @"^(?:class|interface|enum|struct|record)\s+(\w+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (_, sig) in feature.Context.Signatures)
            {
                if (string.IsNullOrWhiteSpace(sig.Content))
                    continue;

                foreach (Match match in symbolRegex.Matches(sig.Content))
                {
                    var name = match.Groups[1].Value;
                    if (name.Length > 2 && !NoisySymbolNames.Contains(name))
                        names.Add(name);
                }
            }

            feature.SymbolNames = names.ToList();
        }

        /// <summary>
        /// Filters an existing SymbolNames list to only type-level names,
        /// removing noisy property/method names that cause false matches.
        /// </summary>
        internal static List<string> FilterSymbolNames(List<string> symbolNames, FeatureEntry feature)
        {
            var typeRegex = new Regex(
                @"^(?:class|interface|enum|struct|record)\s+(\w+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, sig) in feature.Context.Signatures)
            {
                if (string.IsNullOrWhiteSpace(sig.Content)) continue;
                foreach (Match match in typeRegex.Matches(sig.Content))
                    typeNames.Add(match.Groups[1].Value);
            }

            return symbolNames
                .Where(n => n.Length > 2 && !NoisySymbolNames.Contains(n) &&
                            (typeNames.Contains(n) || n.Length >= 8))
                .ToList();
        }

        /// <summary>
        /// Tokenizes text by splitting on whitespace and punctuation,
        /// lowercasing, and removing stopwords.
        /// </summary>
        internal static HashSet<string> Tokenize(string text)
        {
            var raw = TokenSplitRegex.Split(text);
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in raw)
            {
                var lower = token.ToLowerInvariant();
                if (lower.Length > 0 && !Stopwords.Contains(lower))
                    tokens.Add(lower);
            }

            return tokens;
        }

        /// <summary>
        /// Scores a feature against a set of task tokens.
        /// <c>keywordOverlap * 0.5 + descriptionWordOverlap * 0.3 + fileNameMention * 0.2</c>
        /// </summary>
        internal static double ScoreFeature(HashSet<string> taskTokens, string taskDescription, FeatureEntry feature)
        {
            // Keyword overlap: fraction of feature keywords that appear in task tokens
            var keywordScore = 0.0;
            if (feature.Keywords.Count > 0)
            {
                var matches = feature.Keywords.Count(k => taskTokens.Contains(k.ToLowerInvariant()));
                keywordScore = (double)matches / feature.Keywords.Count;
            }

            // Description word overlap: fraction of task tokens found in feature description
            var descriptionScore = 0.0;
            if (!string.IsNullOrWhiteSpace(feature.Description) && taskTokens.Count > 0)
            {
                var descTokens = Tokenize(feature.Description);
                var overlap = taskTokens.Count(t => descTokens.Contains(t));
                descriptionScore = (double)overlap / taskTokens.Count;
            }

            // File name mention: any primary/secondary file name mentioned in the task description
            var fileScore = 0.0;
            var descLower = taskDescription.ToLowerInvariant();
            var allFiles = feature.PrimaryFiles.Concat(feature.SecondaryFiles);
            foreach (var file in allFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (fileName.Length > 2 && descLower.Contains(fileName))
                {
                    fileScore = 1.0;
                    break;
                }
            }

            // Symbol name match: boost if task description mentions a class/interface name from this feature
            var symbolScore = 0.0;
            if (feature.SymbolNames.Count > 0)
            {
                foreach (var sym in feature.SymbolNames)
                {
                    if (sym.Length > 2 && descLower.Contains(sym.ToLowerInvariant()))
                    {
                        symbolScore = FeatureConstants.SymbolMatchScoreBoost;
                        break;
                    }
                }
            }

            return keywordScore * 0.5 + descriptionScore * 0.3 + fileScore * 0.2 + symbolScore;
        }

        /// <summary>Builds a compact markdown block for secondary features (name + files only, no signatures).</summary>
        private static string BuildCompactFeatureBlock(FeatureEntry feature)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"- **{feature.Name}**: {string.Join(", ", feature.PrimaryFiles)}");
            if (feature.DependsOn.Count > 0)
                sb.AppendLine($"  Depends on: {string.Join(", ", feature.DependsOn)}");
            return sb.ToString();
        }

        /// <summary>Builds the markdown block for a single feature.</summary>
        private static string BuildSingleFeatureBlock(FeatureEntry feature, double? confidence = null)
        {
            var sb = new StringBuilder();

            var header = confidence.HasValue
                ? $"## {feature.Name} (confidence: {confidence.Value:F2})"
                : $"## {feature.Name}";
            sb.AppendLine(header);
            sb.AppendLine($"**Core files:** {string.Join(", ", feature.PrimaryFiles)}");

            if (feature.DependsOn.Count > 0)
                sb.AppendLine($"**Depends on:** {string.Join(", ", feature.DependsOn)}");

            sb.AppendLine();

            // Signatures
            if (feature.Context.Signatures.Count > 0)
            {
                sb.AppendLine("### Signatures");
                sb.AppendLine("```");
                foreach (var (filePath, sig) in feature.Context.Signatures)
                {
                    if (!string.IsNullOrWhiteSpace(sig.Content))
                    {
                        sb.AppendLine($"// {filePath}");
                        sb.AppendLine(sig.Content);
                    }
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Key Types
            if (feature.Context.KeyTypes.Count > 0)
            {
                sb.AppendLine("### Key Types");
                foreach (var keyType in feature.Context.KeyTypes)
                    sb.AppendLine($"- {keyType}");
                sb.AppendLine();
            }

            // Patterns
            if (feature.Context.Patterns.Count > 0)
            {
                sb.AppendLine("### Patterns");
                foreach (var pattern in feature.Context.Patterns)
                    sb.AppendLine($"- {pattern}");
                sb.AppendLine();
            }

            // Dependencies
            if (feature.Context.Dependencies.Count > 0)
            {
                sb.AppendLine("### Dependencies");
                foreach (var dep in feature.Context.Dependencies)
                    sb.AppendLine($"- {dep}");
                sb.AppendLine();
            }

            // Related features
            if (feature.RelatedFeatureIds.Count > 0)
            {
                sb.AppendLine("### Related");
                sb.AppendLine(string.Join(", ", feature.RelatedFeatureIds));
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// Truncates a feature block at the last complete member boundary (class/method signature line)
        /// before the character budget, appending a trailer indicating how many members were omitted.
        /// </summary>
        private static string TruncateAtMemberBoundary(string block, int charBudget)
        {
            if (block.Length <= charBudget)
                return block;

            // Find all line boundaries up to the budget
            var cutoff = -1;
            var pos = 0;
            while (pos < charBudget)
            {
                var nextNewline = block.IndexOf('\n', pos);
                if (nextNewline < 0 || nextNewline >= charBudget)
                    break;
                cutoff = nextNewline + 1;
                pos = cutoff;
            }

            if (cutoff <= 0)
                cutoff = charBudget;

            var truncated = block[..cutoff];

            // Count remaining signature-like lines (class/method declarations) after cutoff
            var remaining = block[cutoff..];
            var truncatedMemberCount = 0;
            foreach (var line in remaining.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("class ") || trimmed.StartsWith("struct ") ||
                    trimmed.StartsWith("interface ") || trimmed.StartsWith("enum ") ||
                    trimmed.Contains('(') && trimmed.Contains(')') && !trimmed.StartsWith("//") && !trimmed.StartsWith('#'))
                    truncatedMemberCount++;
            }

            if (truncatedMemberCount > 0)
                truncated += $"// ... {truncatedMemberCount} more members truncated\n";

            return truncated;
        }

        /// <summary>
        /// Computes a short hash of the project path for use in the named mutex.
        /// Returns the first 8 hex characters of the SHA-256 of the path.
        /// </summary>
        private static string ComputePathHash(string projectPath)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        /// <summary>
        /// Acquires a named mutex for write operations against a project's feature registry.
        /// Returns a disposable that releases the mutex.
        /// </summary>
        private static MutexGuard AcquireMutex(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            var mutexName = $"Global\\Spritely_FeatureRegistry_{hash}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(FeatureConstants.MutexTimeoutMs))
                {
                    mutex.Dispose();
                    throw new TimeoutException(
                        $"[FeatureRegistry] Timed out acquiring mutex for project: {projectPath}");
                }
            }
            catch (AbandonedMutexException)
            {
                // Previous holder crashed — we now own it, proceed
                AppLogger.Debug("FeatureRegistry", "Acquired abandoned mutex — previous holder likely crashed.");
            }

            return new MutexGuard(mutex);
        }

        /// <summary>Disposable wrapper that releases and disposes a <see cref="Mutex"/>.</summary>
        private sealed class MutexGuard : IDisposable
        {
            private readonly Mutex _mutex;
            private bool _disposed;

            public MutexGuard(Mutex mutex)
            {
                _mutex = mutex;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Mutex was not owned — safe to ignore
                }
                finally
                {
                    _mutex.Dispose();
                }
            }
        }
    }
}
