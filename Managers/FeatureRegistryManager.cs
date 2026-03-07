using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

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

        /// <summary>Checks if the feature index exists for the given project.</summary>
        public bool RegistryExists(string projectPath)
        {
            var indexPath = Path.Combine(GetFeaturesPath(projectPath), FeatureConstants.IndexFileName);
            return File.Exists(indexPath);
        }

        /// <summary>Returns the absolute path to the features directory.</summary>
        public string GetFeaturesPath(string projectPath)
        {
            return Path.Combine(projectPath, FeatureConstants.SpritelyDir, FeatureConstants.FeaturesDir);
        }

        /// <summary>Loads and deserializes <c>_index.json</c>.</summary>
        public async Task<FeatureIndex> LoadIndexAsync(string projectPath)
        {
            var indexPath = Path.Combine(GetFeaturesPath(projectPath), FeatureConstants.IndexFileName);
            try
            {
                if (!File.Exists(indexPath))
                    return new FeatureIndex();

                var json = await File.ReadAllTextAsync(indexPath);
                return JsonSerializer.Deserialize<FeatureIndex>(json, JsonOptions) ?? new FeatureIndex();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to load index: {ex.Message}", ex);
                return new FeatureIndex();
            }
        }

        /// <summary>Loads a single feature entry by id.</summary>
        public async Task<FeatureEntry?> LoadFeatureAsync(string projectPath, string featureId)
        {
            var filePath = Path.Combine(GetFeaturesPath(projectPath), $"{featureId}.json");
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<FeatureEntry>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to load feature '{featureId}': {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>Loads all features referenced in the index.</summary>
        public async Task<List<FeatureEntry>> LoadAllFeaturesAsync(string projectPath)
        {
            var index = await LoadIndexAsync(projectPath);

            var tasks = index.Features.Select(entry => LoadFeatureAsync(projectPath, entry.Id));
            var loaded = await Task.WhenAll(tasks);

            return loaded.Where(f => f != null).ToList()!;
        }

        /// <summary>
        /// Saves a feature file and updates the index if the feature is new.
        /// All <see cref="List{T}"/> properties are sorted alphabetically before serialization.
        /// </summary>
        public async Task SaveFeatureAsync(string projectPath, FeatureEntry feature)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                var featuresPath = GetFeaturesPath(projectPath);
                Directory.CreateDirectory(featuresPath);

                PopulateSymbolNamesIfEmpty(feature);
                SortFeatureLists(feature);

                var json = SerializeDeterministic(feature);
                var filePath = Path.Combine(featuresPath, $"{feature.Id}.json");
                await File.WriteAllTextAsync(filePath, json);

                // Update index: add if missing, sync fields if changed
                var index = await LoadIndexAsync(projectPath);
                var indexEntry = index.Features.FirstOrDefault(f => f.Id == feature.Id);
                if (indexEntry == null)
                {
                    index.Features.Add(new FeatureIndexEntry
                    {
                        Id = feature.Id,
                        Name = feature.Name,
                        Category = feature.Category,
                        TouchCount = feature.TouchCount,
                        PrimaryFileCount = feature.PrimaryFiles.Count
                    });
                    await SaveIndexInternalAsync(projectPath, index);
                }
                else
                {
                    var changed = indexEntry.Name != feature.Name
                                  || indexEntry.Category != feature.Category
                                  || indexEntry.TouchCount != feature.TouchCount
                                  || indexEntry.PrimaryFileCount != feature.PrimaryFiles.Count;

                    if (changed)
                    {
                        indexEntry.Name = feature.Name;
                        indexEntry.Category = feature.Category;
                        indexEntry.TouchCount = feature.TouchCount;
                        indexEntry.PrimaryFileCount = feature.PrimaryFiles.Count;
                        await SaveIndexInternalAsync(projectPath, index);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to save feature '{feature.Id}': {ex.Message}", ex);
            }
        }

        /// <summary>Removes a feature file and its index entry.</summary>
        public async Task RemoveFeatureAsync(string projectPath, string featureId)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                var filePath = Path.Combine(GetFeaturesPath(projectPath), $"{featureId}.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);

                var index = await LoadIndexAsync(projectPath);
                index.Features.RemoveAll(f => f.Id == featureId);
                await SaveIndexInternalAsync(projectPath, index);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to remove feature '{featureId}': {ex.Message}", ex);
            }
        }

        /// <summary>Saves the feature index, sorted by Id.</summary>
        public async Task SaveIndexAsync(string projectPath, FeatureIndex index)
        {
            using var guard = AcquireMutex(projectPath);
            await SaveIndexInternalAsync(projectPath, index);
        }

        // ── Search ───────────────────────────────────────────────────────

        /// <summary>
        /// Finds features matching a task description using local keyword matching.
        /// No LLM involved — pure tokenization and scoring.
        /// </summary>
        public List<FeatureEntry> FindMatchingFeatures(
            string taskDescription,
            List<FeatureEntry> allFeatures,
            int maxResults = 5)
        {
            var tokens = Tokenize(taskDescription);
            if (tokens.Count == 0)
                return new List<FeatureEntry>();

            var scored = new List<(FeatureEntry Feature, double Score)>();

            foreach (var feature in allFeatures)
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
            CodebaseIndexManager? codebaseIndexManager = null)
        {
            if (symbolIndex == null)
                return FindMatchingFeatures(taskDescription, allFeatures, maxResults);

            var tokens = Tokenize(taskDescription);
            if (tokens.Count == 0)
                return new List<FeatureEntry>();

            // Score all features with keyword + symbol scoring
            var scored = new Dictionary<string, (FeatureEntry Feature, double Score)>();

            foreach (var feature in allFeatures)
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
            List<FeatureEntry>? secondaryFeatures = null)
        {
            if (features.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("# FEATURE CONTEXT");
            sb.AppendLine("The following features are relevant to this task. Use this context to understand");
            sb.AppendLine("the architecture before making changes. Read the listed files for full details.");
            sb.AppendLine();

            var totalCharsUsed = 0;
            var totalCharBudget = FeatureConstants.MaxTotalFeatureContextTokens * 4;

            // Primary features — full detail
            foreach (var feature in features)
            {
                var featureBlock = BuildSingleFeatureBlock(feature);
                var featureCharBudget = FeatureConstants.MaxTokensPerFeature * 4;

                if (featureBlock.Length > featureCharBudget)
                    featureBlock = featureBlock[..featureCharBudget];

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
                        compactBlock = compactBlock[..secondaryCharBudget];

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
        public async Task<bool> RefreshStaleSignaturesAsync(string projectPath, FeatureEntry feature)
        {
            var anyRefreshed = false;

            var filesToUpdate = new List<(string relativePath, string absolutePath)>();

            foreach (var (relativePath, signature) in feature.Context.Signatures)
            {
                var absolutePath = Path.Combine(projectPath, relativePath);
                if (!File.Exists(absolutePath))
                    continue;

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

        /// <summary>Saves the index without acquiring the mutex (caller must hold it).</summary>
        private async Task SaveIndexInternalAsync(string projectPath, FeatureIndex index)
        {
            try
            {
                var featuresPath = GetFeaturesPath(projectPath);
                Directory.CreateDirectory(featuresPath);

                index.Features = index.Features.OrderBy(f => f.Id).ToList();

                var json = SerializeDeterministic(index);
                var indexPath = Path.Combine(featuresPath, FeatureConstants.IndexFileName);
                await File.WriteAllTextAsync(indexPath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureRegistry", $"Failed to save index: {ex.Message}", ex);
            }
        }

        /// <summary>Serializes an object to JSON with deterministic formatting and LF line endings.</summary>
        private static string SerializeDeterministic<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return json.Replace("\r\n", "\n");
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
        private static void PopulateSymbolNamesIfEmpty(FeatureEntry feature)
        {
            if (feature.SymbolNames.Count > 0 || feature.Context.Signatures.Count == 0)
                return;

            var symbolRegex = new Regex(
                @"\b(?:class|interface|enum|struct|record)\s+(\w+)",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (_, sig) in feature.Context.Signatures)
            {
                if (string.IsNullOrWhiteSpace(sig.Content))
                    continue;

                foreach (Match match in symbolRegex.Matches(sig.Content))
                {
                    if (match.Groups[1].Value.Length > 1)
                        names.Add(match.Groups[1].Value);
                }
            }

            feature.SymbolNames = names.ToList();
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
        private static string BuildSingleFeatureBlock(FeatureEntry feature)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"## {feature.Name}");
            sb.AppendLine($"**Core files:** {string.Join(", ", feature.PrimaryFiles)}");

            if (feature.DependsOn.Count > 0)
                sb.AppendLine($"**Depends on:** {string.Join(", ", feature.DependsOn)}");

            sb.AppendLine();

            // Signatures
            if (feature.Context.Signatures.Count > 0)
            {
                sb.AppendLine("### Signatures");
                sb.AppendLine("```");
                foreach (var (_, sig) in feature.Context.Signatures)
                {
                    if (!string.IsNullOrWhiteSpace(sig.Content))
                        sb.AppendLine(sig.Content);
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
