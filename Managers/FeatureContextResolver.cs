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

        private readonly FeatureRegistryManager _registryManager;
        private readonly CodebaseIndexManager? _codebaseIndexManager;
        private readonly ModuleRegistryManager? _moduleRegistryManager;

        public FeatureContextResolver(
            FeatureRegistryManager registryManager,
            CodebaseIndexManager? codebaseIndexManager = null,
            ModuleRegistryManager? moduleRegistryManager = null)
        {
            _registryManager = registryManager;
            _codebaseIndexManager = codebaseIndexManager;
            _moduleRegistryManager = moduleRegistryManager;
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

                // Enhanced pre-filter: keyword + symbol matching
                var candidates = _registryManager.FindMatchingFeaturesEnhanced(
                    taskDescription, allFeatures, symbolIndex, FeatureConstants.MaxFeaturesPerTask,
                    _codebaseIndexManager);

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
                            candidates, fpSecondary.Count > 0 ? fpSecondary : null, fpModulePreamble);

                        return new FeatureContextResult
                        {
                            RelevantFeatures = fastPathFeatures,
                            IsNewFeature = false,
                            ContextBlock = fastContextBlock
                        };
                    }
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
                    primaryFeatures, secondaryFeatures.Count > 0 ? secondaryFeatures : null, modulePreamble);

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
    }
}
