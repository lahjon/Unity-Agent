using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers;

/// <summary>
/// Bootstraps a full feature registry for a project by scanning all source files,
/// extracting signatures, and using a Sonnet LLM call to identify logical features.
/// </summary>
public class FeatureInitializer
{
    private readonly FeatureRegistryManager _registryManager;

    /// <summary>Reports progress status messages to the UI.</summary>
    public event Action<string>? ProgressChanged;

    public FeatureInitializer(FeatureRegistryManager registryManager)
    {
        _registryManager = registryManager;
    }

    // ── Schema for the Sonnet feature output ─────────────────────────────

    private const string OutputSchema =
        """{"type":"object","properties":{"features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"keywords":{"type":"array","items":{"type":"string"}},"primary_files":{"type":"array","items":{"type":"string"}},"secondary_files":{"type":"array","items":{"type":"string"}},"related_feature_ids":{"type":"array","items":{"type":"string"}},"key_types":{"type":"array","items":{"type":"string"}},"patterns":{"type":"array","items":{"type":"string"}},"dependencies":{"type":"array","items":{"type":"string"}},"depends_on":{"type":"array","items":{"type":"string"}}},"required":["id","name","description","category","keywords","primary_files"]}}},"required":["features"]}""";

    // ── Schema for the Sonnet module grouping output ─────────────────────

    private const string ModuleOutputSchema =
        """{"type":"object","properties":{"modules":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"feature_ids":{"type":"array","items":{"type":"string"}}},"required":["id","name","description","feature_ids"]}}},"required":["modules"]}""";

    // ── JSON deserialization models for the Sonnet response ──────────────

    private sealed class SonnetResponse
    {
        [JsonPropertyName("features")]
        public List<SonnetFeature> Features { get; set; } = [];
    }

    private sealed class SonnetFeature
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = [];

        [JsonPropertyName("primary_files")]
        public List<string> PrimaryFiles { get; set; } = [];

        [JsonPropertyName("secondary_files")]
        public List<string> SecondaryFiles { get; set; } = [];

        [JsonPropertyName("related_feature_ids")]
        public List<string> RelatedFeatureIds { get; set; } = [];

        [JsonPropertyName("key_types")]
        public List<string> KeyTypes { get; set; } = [];

        [JsonPropertyName("patterns")]
        public List<string> Patterns { get; set; } = [];

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = [];

        [JsonPropertyName("depends_on")]
        public List<string> DependsOnFeatures { get; set; } = [];
    }

    private sealed class SonnetModuleResponse
    {
        [JsonPropertyName("modules")]
        public List<SonnetModule> Modules { get; set; } = [];
    }

    private sealed class SonnetModule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("feature_ids")]
        public List<string> FeatureIds { get; set; } = [];
    }

    // ── Main entry point ────────────────────────────────────────────────

    /// <summary>
    /// Scans the project, calls Sonnet to identify features, builds and persists
    /// the feature registry, and sets up git integration files.
    /// Returns the final <see cref="FeatureIndex"/>, or <c>null</c> on failure.
    /// </summary>
    public async Task<FeatureIndex?> InitializeAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            // ── Phase 1: File Discovery ─────────────────────────────────
            ReportProgress("Scanning project files...");

            var projectType = DetectProjectType(projectPath);
            var relativeFiles = ScanSourceFiles(projectPath);

            ct.ThrowIfCancellationRequested();
            ReportProgress($"Found {relativeFiles.Count} source files");

            if (relativeFiles.Count == 0)
            {
                ReportProgress("No supported source files found — nothing to initialize");
                return null;
            }

            // ── Phase 2: Build Structural Map (chunked) ─────────────────
            ReportProgress("Extracting code signatures...");

            var directoryTree = BuildDirectoryTree(projectPath, relativeFiles);

            // Split files into chunks of MaxFilesPerSonnetChunk
            var chunks = ChunkFiles(relativeFiles, FeatureConstants.MaxFilesPerSonnetChunk);
            var totalChunks = chunks.Count;

            var allSonnetFeatures = new Dictionary<string, SonnetFeature>(StringComparer.OrdinalIgnoreCase);

            for (var chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
            {
                ct.ThrowIfCancellationRequested();

                var chunk = chunks[chunkIdx];
                if (totalChunks > 1)
                    ReportProgress($"Analyzing chunk {chunkIdx + 1}/{totalChunks} ({chunk.Count} files)...");

                // Extract signatures in parallel across all files in the chunk
                var sigResults = new ConcurrentBag<(string RelativePath, string Signatures)>();
                await Parallel.ForEachAsync(chunk,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = FeatureConstants.MaxSignatureParallelism,
                        CancellationToken = ct
                    },
                    (relativePath, innerCt) =>
                    {
                        var absolutePath = Path.Combine(projectPath, relativePath);
                        var signatures = SignatureExtractor.ExtractSignatures(absolutePath);
                        if (!string.IsNullOrEmpty(signatures))
                            sigResults.Add((relativePath, signatures));
                        return ValueTask.CompletedTask;
                    });

                // Reassemble in deterministic order
                var signaturesBuilder = new StringBuilder();
                foreach (var (relPath, sigs) in sigResults.OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase))
                {
                    signaturesBuilder.AppendLine($"## {relPath}");
                    signaturesBuilder.AppendLine(sigs);
                    signaturesBuilder.AppendLine();
                }

                var signaturesText = signaturesBuilder.ToString();

                // ── Phase 3: LLM Analysis ───────────────────────────────
                if (chunkIdx == 0)
                    ReportProgress("Analyzing project structure...");

                var template = PromptLoader.Load("FeatureInitializationPrompt.md");
                var prompt = string.Format(template, projectType, directoryTree, signaturesText);

                ct.ThrowIfCancellationRequested();

                var sonnetResponse = await CallSonnetWithRetryAsync(prompt, OutputSchema, ct);

                if (sonnetResponse is null)
                    continue;

                var parsed = DeserializeResponse<SonnetResponse>(sonnetResponse);
                if (parsed?.Features == null || parsed.Features.Count == 0)
                    continue;

                // Merge features across chunks by unioning file lists and keywords
                foreach (var sf in parsed.Features)
                {
                    if (string.IsNullOrWhiteSpace(sf.Id))
                        continue;

                    if (allSonnetFeatures.TryGetValue(sf.Id, out var existing))
                    {
                        // Union PrimaryFiles, SecondaryFiles, and Keywords from the later chunk
                        if (sf.PrimaryFiles != null)
                        {
                            existing.PrimaryFiles ??= new List<string>();
                            foreach (var f in sf.PrimaryFiles)
                                if (!existing.PrimaryFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                                    existing.PrimaryFiles.Add(f);
                        }
                        if (sf.SecondaryFiles != null)
                        {
                            existing.SecondaryFiles ??= new List<string>();
                            foreach (var f in sf.SecondaryFiles)
                                if (!existing.SecondaryFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                                    existing.SecondaryFiles.Add(f);
                        }
                        if (sf.Keywords != null)
                        {
                            existing.Keywords ??= new List<string>();
                            foreach (var kw in sf.Keywords)
                                if (!existing.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                                    existing.Keywords.Add(kw);
                        }
                    }
                    else
                    {
                        allSonnetFeatures[sf.Id] = sf;
                    }
                }

                if (totalChunks > 1)
                    ReportProgress($"Chunk {chunkIdx + 1}/{totalChunks}: identified {parsed.Features.Count} features");
            }

            if (allSonnetFeatures.Count == 0)
            {
                ReportProgress("AI analysis returned no features after all chunks — initialization aborted");
                return null;
            }

            ReportProgress($"AI identified {allSonnetFeatures.Count} features across {totalChunks} chunk(s)");

            // ── Phase 4: Registry Creation (merge-aware) ─────────────────
            ReportProgress("Building feature registry...");

            // Load existing features so we can merge task-discovered data
            var existingFeatures = new Dictionary<string, FeatureEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ef in await _registryManager.LoadAllFeaturesAsync(projectPath))
                    existingFeatures[ef.Id] = ef;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureInitializer", $"Could not load existing features for merge: {ex.Message}");
            }

            var sonnetFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdCount = 0;
            foreach (var sf in allSonnetFeatures.Values)
            {
                ct.ThrowIfCancellationRequested();
                sonnetFeatureIds.Add(sf.Id);

                var feature = new FeatureEntry
                {
                    Id = sf.Id,
                    Name = sf.Name,
                    Description = sf.Description,
                    Category = sf.Category,
                    Keywords = sf.Keywords.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
                    PrimaryFiles = sf.PrimaryFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    SecondaryFiles = (sf.SecondaryFiles ?? []).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    RelatedFeatureIds = (sf.RelatedFeatureIds ?? []).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                    DependsOn = (sf.DependsOnFeatures ?? []).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(),
                    Context = new FeatureContext
                    {
                        KeyTypes = (sf.KeyTypes ?? []).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                        Patterns = (sf.Patterns ?? []).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                        Dependencies = (sf.Dependencies ?? []).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                    },
                    LastUpdatedAt = DateTime.UtcNow,
                    TouchCount = 0
                };

                // Merge with existing feature if it was previously task-discovered
                if (existingFeatures.TryGetValue(sf.Id, out var existing))
                {
                    // Preserve task-tracking metadata
                    feature.TouchCount = existing.TouchCount;
                    feature.LastUpdatedByTaskId = existing.LastUpdatedByTaskId;
                    feature.ParentModuleId = existing.ParentModuleId;
                    feature.HierarchyLevel = existing.HierarchyLevel;
                    feature.ChildFeatureIds = existing.ChildFeatureIds;

                    // Merge task-added files that Sonnet didn't include
                    foreach (var pf in existing.PrimaryFiles)
                    {
                        if (!feature.PrimaryFiles.Contains(pf, StringComparer.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(projectPath, pf)))
                            feature.PrimaryFiles.Add(pf);
                    }
                    foreach (var sf2 in existing.SecondaryFiles)
                    {
                        if (!feature.SecondaryFiles.Contains(sf2, StringComparer.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(projectPath, sf2)))
                            feature.SecondaryFiles.Add(sf2);
                    }

                    feature.PrimaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
                    feature.SecondaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
                }

                // Populate signatures, SymbolNames, and derived keywords for primary files (parallel)
                var fileSigResults = new ConcurrentBag<(string File, string Hash, string Sigs, HashSet<string> Keywords)>();
                Parallel.ForEach(feature.PrimaryFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = FeatureConstants.MaxSignatureParallelism },
                    primaryFile =>
                    {
                        var absolutePath = Path.Combine(projectPath, primaryFile);
                        var signatures = SignatureExtractor.ExtractSignatures(absolutePath);
                        var hash = SignatureExtractor.ComputeFileHash(absolutePath);
                        var fileKeywords = SignatureExtractor.ExtractKeywords(absolutePath);
                        fileSigResults.Add((primaryFile, hash, signatures, fileKeywords));
                    });

                var symbolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var derivedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, hash, sigs, keywords) in fileSigResults)
                {
                    if (!string.IsNullOrEmpty(sigs))
                    {
                        feature.Context.Signatures[file] = new FileSignature
                        {
                            Hash = hash,
                            Content = sigs
                        };
                    }
                    foreach (var kw in keywords)
                    {
                        derivedKeywords.Add(kw);
                        if (kw.Length > 1 && char.IsUpper(kw[0]))
                            symbolNames.Add(kw);
                    }
                }

                feature.SymbolNames = symbolNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

                // Merge symbol-derived keywords into the feature's keyword list
                foreach (var kw in derivedKeywords.Where(k => k.Length > 2 && char.IsLower(k[0])))
                {
                    if (!feature.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                        feature.Keywords.Add(kw);
                }

                await _registryManager.SaveFeatureAsync(projectPath, feature);
                createdCount++;
            }

            // ── Phase 4b: Validate task-discovered features not in Sonnet output ──
            // Features added by FeatureUpdateAgent during tasks but not re-identified
            // by Sonnet: keep if their primary files still exist, remove if all gone.
            // Also clean up empty placeholder features created by FeatureContextResolver
            // that were never populated by FeatureUpdateAgent (empty PrimaryFiles, older than 7 days).
            var orphanedCount = 0;
            var refreshedCount = 0;
            var placeholderCount = 0;
            foreach (var (existingId, existingFeature) in existingFeatures)
            {
                if (sonnetFeatureIds.Contains(existingId))
                    continue; // Already handled above

                ct.ThrowIfCancellationRequested();

                // Remove empty placeholder features older than 7 days
                if (existingFeature.PrimaryFiles.Count == 0
                    && existingFeature.LastUpdatedAt < DateTime.UtcNow.AddDays(-7))
                {
                    await _registryManager.RemoveFeatureAsync(projectPath, existingId);
                    placeholderCount++;
                    AppLogger.Info("FeatureInitializer",
                        $"Removed stale placeholder feature '{existingId}' (empty PrimaryFiles, last updated {existingFeature.LastUpdatedAt:u})");
                    continue;
                }

                // Check if any primary files still exist on disk
                var validPrimaryFiles = existingFeature.PrimaryFiles
                    .Where(f => File.Exists(Path.Combine(projectPath, f)))
                    .ToList();

                if (validPrimaryFiles.Count == 0)
                {
                    // All primary files are gone — remove the orphaned feature
                    await _registryManager.RemoveFeatureAsync(projectPath, existingId);
                    orphanedCount++;
                    AppLogger.Info("FeatureInitializer",
                        $"Removed orphaned feature '{existingId}' (all primary files deleted)");
                    continue;
                }

                // Feature still has valid files — refresh its signatures and prune dead files
                existingFeature.PrimaryFiles = validPrimaryFiles;
                existingFeature.SecondaryFiles = existingFeature.SecondaryFiles
                    .Where(f => File.Exists(Path.Combine(projectPath, f)))
                    .ToList();

                // Remove signatures for deleted files
                var deadSigKeys = existingFeature.Context.Signatures.Keys
                    .Where(k => !existingFeature.PrimaryFiles.Contains(k, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var key in deadSigKeys)
                    existingFeature.Context.Signatures.Remove(key);

                // Refresh stale signatures
                await _registryManager.RefreshStaleSignaturesAsync(projectPath, existingFeature);

                // Rebuild symbol names and keywords from current files via unified extraction
                var symNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var refreshKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pf in existingFeature.PrimaryFiles)
                {
                    foreach (var kw in SignatureExtractor.ExtractKeywords(Path.Combine(projectPath, pf)))
                    {
                        refreshKeywords.Add(kw);
                        if (kw.Length > 1 && char.IsUpper(kw[0]))
                            symNames.Add(kw);
                    }
                }
                existingFeature.SymbolNames = symNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var kw in refreshKeywords.Where(k => k.Length > 2 && char.IsLower(k[0])))
                {
                    if (!existingFeature.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                        existingFeature.Keywords.Add(kw);
                }

                existingFeature.LastUpdatedAt = DateTime.UtcNow;
                await _registryManager.SaveFeatureAsync(projectPath, existingFeature);
                refreshedCount++;
            }

            var statusParts = new List<string> { $"Created {createdCount} features" };
            if (refreshedCount > 0)
                statusParts.Add($"refreshed {refreshedCount} task-discovered");
            if (orphanedCount > 0)
                statusParts.Add($"removed {orphanedCount} orphaned");
            if (placeholderCount > 0)
                statusParts.Add($"removed {placeholderCount} stale placeholders");
            ReportProgress(string.Join(", ", statusParts));

            // ── Phase 4.5: Dependency Analysis ──────────────────────────
            ReportProgress("Analyzing cross-feature dependencies...");

            ct.ThrowIfCancellationRequested();
            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);

            DependencyAnalyzer.AnalyzeDependencies(allFeatures, projectPath);
            DependencyAnalyzer.AnalyzeImportDependencies(allFeatures, projectPath);
            _registryManager.ValidateDependencies(allFeatures);

            // Prune inflated dependsOn arrays to top-N by keyword relevance
            PruneDependencies(allFeatures);

            // Re-save features with updated DependsOn
            foreach (var feature in allFeatures)
                await _registryManager.SaveFeatureAsync(projectPath, feature);

            var graph = _registryManager.BuildDependencyGraph(allFeatures);
            if (graph.Cycles.Count > 0)
            {
                AppLogger.Warn("FeatureInitializer",
                    $"Detected {graph.Cycles.Count} dependency cycle(s) in feature graph");
                ReportProgress($"Warning: {graph.Cycles.Count} circular dependency cycle(s) detected");
            }

            ReportProgress($"Dependency analysis complete — {allFeatures.Count(f => f.DependsOn.Count > 0)} features have dependencies");

            // ── Phase 5: Codebase Symbol Index ──────────────────────────
            ReportProgress("Building codebase symbol index...");

            ct.ThrowIfCancellationRequested();
            var codebaseIndexManager = new CodebaseIndexManager();
            await codebaseIndexManager.BuildIndexAsync(projectPath, allFeatures, ct);

            // Persist symbol index version hash for staleness detection on next startup
            var symbolVersion = FeatureRegistryManager.ComputeSymbolIndexVersion(allFeatures);
            var currentIndex = await _registryManager.LoadIndexAsync(projectPath);
            currentIndex.SymbolIndexVersion = symbolVersion;
            await _registryManager.SaveIndexAsync(projectPath, currentIndex);

            ReportProgress("Codebase symbol index written");

            // ── Phase 6: Module Creation ────────────────────────────────
            ReportProgress("Grouping features into modules...");

            ct.ThrowIfCancellationRequested();
            await CreateModulesAsync(projectPath, allFeatures, ct);

            // ── Phase 7: Git Integration ────────────────────────────────
            EnsureGitIntegration(projectPath);

            ReportProgress($"Feature registry initialized with {createdCount} features");

            return await _registryManager.LoadIndexAsync(projectPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("FeatureInitializer", $"Initialization failed: {ex.Message}", ex);
            ReportProgress($"Initialization failed: {ex.Message}");
            return null;
        }
    }

    // ── Incremental Update ───────────────────────────────────────────────

    /// <summary>
    /// Incrementally updates the feature registry by only re-scanning files that have
    /// changed since the last scan. Compares file hashes stored in feature signatures
    /// against current disk state. Much faster than a full <see cref="InitializeAsync"/>.
    /// Returns the number of features updated, or -1 if a full re-init is needed.
    /// </summary>
    public async Task<int> IncrementalUpdateAsync(string projectPath, CancellationToken ct = default)
    {
        try
        {
            if (!_registryManager.RegistryExists(projectPath))
                return -1; // No registry — need full init

            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
            if (allFeatures.Count == 0)
                return -1;

            ReportProgress("Scanning for changed files...");

            // Scan current source files on disk
            var currentFiles = new HashSet<string>(
                ScanSourceFiles(projectPath), StringComparer.OrdinalIgnoreCase);

            // Build a map of all files tracked by features and their stored hashes
            var trackedFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in allFeatures)
            {
                foreach (var (relPath, sig) in feature.Context.Signatures)
                    trackedFileHashes.TryAdd(relPath, sig.Hash);
            }

            // Detect changed, new, and deleted files (parallel hash computation)
            var changedFiles = new ConcurrentBag<string>();
            var newFiles = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(currentFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = FeatureConstants.MaxSignatureParallelism,
                    CancellationToken = ct
                },
                (relPath, innerCt) =>
                {
                    var absPath = Path.Combine(projectPath, relPath);
                    var currentHash = SignatureExtractor.ComputeFileHash(absPath);

                    if (trackedFileHashes.TryGetValue(relPath, out var storedHash))
                    {
                        if (currentHash != storedHash)
                            changedFiles.Add(relPath);
                    }
                    else
                    {
                        newFiles.Add(relPath);
                    }
                    return ValueTask.CompletedTask;
                });

            var deletedFiles = trackedFileHashes.Keys
                .Where(f => !currentFiles.Contains(f))
                .ToList();

            var totalChanges = changedFiles.Count + newFiles.Count + deletedFiles.Count;
            if (totalChanges == 0)
            {
                ReportProgress("No file changes detected — registry is up to date");
                return 0;
            }

            // If too many new files, suggest full re-init
            if (newFiles.Count > allFeatures.Count * 2)
            {
                ReportProgress($"Too many new files ({newFiles.Count}) — full re-initialization recommended");
                return -1;
            }

            ReportProgress($"Detected {changedFiles.Count} changed, {newFiles.Count} new, {deletedFiles.Count} deleted files");

            // Update signatures for changed files in their owning features
            var updatedFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changedFileSet = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);
            var deletedFileSet = new HashSet<string>(deletedFiles, StringComparer.OrdinalIgnoreCase);

            foreach (var feature in allFeatures)
            {
                ct.ThrowIfCancellationRequested();
                var needsSave = false;

                // Remove deleted files from signatures and file lists
                foreach (var deleted in deletedFileSet)
                {
                    if (feature.Context.Signatures.Remove(deleted))
                        needsSave = true;
                    if (feature.PrimaryFiles.Remove(deleted))
                        needsSave = true;
                    if (feature.SecondaryFiles.Remove(deleted))
                        needsSave = true;
                }

                // Refresh changed file signatures
                foreach (var changed in changedFileSet)
                {
                    if (!feature.Context.Signatures.ContainsKey(changed) &&
                        !feature.PrimaryFiles.Contains(changed, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var absPath = Path.Combine(projectPath, changed);
                    var newSigs = SignatureExtractor.ExtractSignatures(absPath);
                    var newHash = SignatureExtractor.ComputeFileHash(absPath);

                    feature.Context.Signatures[changed] = new FileSignature
                    {
                        Hash = newHash,
                        Content = newSigs
                    };
                    needsSave = true;
                }

                if (needsSave)
                {
                    // Rebuild symbol names and keywords via unified extraction
                    var symNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var incKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pf in feature.PrimaryFiles)
                    {
                        foreach (var kw in SignatureExtractor.ExtractKeywords(Path.Combine(projectPath, pf)))
                        {
                            incKeywords.Add(kw);
                            if (kw.Length > 1 && char.IsUpper(kw[0]))
                                symNames.Add(kw);
                        }
                    }
                    feature.SymbolNames = symNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var kw in incKeywords.Where(k => k.Length > 2 && char.IsLower(k[0])))
                    {
                        if (!feature.Keywords.Contains(kw, StringComparer.OrdinalIgnoreCase))
                            feature.Keywords.Add(kw);
                    }

                    feature.LastUpdatedAt = DateTime.UtcNow;
                    await _registryManager.SaveFeatureAsync(projectPath, feature);
                    updatedFeatureIds.Add(feature.Id);
                }
            }

            // Remove features with no remaining primary files
            foreach (var feature in allFeatures.Where(f => f.PrimaryFiles.Count == 0).ToList())
            {
                await _registryManager.RemoveFeatureAsync(projectPath, feature.Id);
                ReportProgress($"Removed feature '{feature.Id}' (all primary files deleted)");
            }

            // Update symbol index version hash after incremental changes
            if (updatedFeatureIds.Count > 0)
            {
                var refreshedFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);
                var symbolVersion = FeatureRegistryManager.ComputeSymbolIndexVersion(refreshedFeatures);
                var featureIndex = await _registryManager.LoadIndexAsync(projectPath);
                featureIndex.SymbolIndexVersion = symbolVersion;
                await _registryManager.SaveIndexAsync(projectPath, featureIndex);
            }

            ReportProgress($"Incremental update complete: {updatedFeatureIds.Count} features updated");
            return updatedFeatureIds.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("FeatureInitializer", $"Incremental update failed: {ex.Message}", ex);
            ReportProgress($"Incremental update failed: {ex.Message}");
            return -1;
        }
    }

    // ── Helper: Chunked Sonnet call with retry ──────────────────────────

    private async Task<string?> CallSonnetWithRetryAsync(string prompt, string schema, CancellationToken ct)
    {
        // Cycle status messages while waiting
        var cycleMessages = new[]
        {
            "Analyzing project structure...",
            "Scanning code patterns...",
            "Identifying features...",
            "Processing signatures...",
            "Mapping dependencies...",
            "Classifying components..."
        };
        var cycleIndex = 0;
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cycleTask = Task.Run(async () =>
        {
            try
            {
                while (!cycleCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(3000, cycleCts.Token);
                    cycleIndex = (cycleIndex + 1) % cycleMessages.Length;
                    ReportProgress(cycleMessages[cycleIndex]);
                }
            }
            catch (OperationCanceledException) { }
        }, cycleCts.Token);

        var result = await CallSonnetAsync(prompt, schema, ct);

        if (string.IsNullOrWhiteSpace(result))
        {
            ct.ThrowIfCancellationRequested();
            AppLogger.Info("FeatureInitializer", "First Sonnet call failed, retrying in 3 seconds...");
            ReportProgress("Retrying AI analysis...");
            await Task.Delay(3000, ct);
            result = await CallSonnetAsync(prompt, schema, ct);
        }

        await cycleCts.CancelAsync();
        try { await cycleTask; } catch (OperationCanceledException) { }

        return result;
    }

    // ── Helper: Module creation via Sonnet ───────────────────────────────

    private async Task CreateModulesAsync(string projectPath, List<FeatureEntry> allFeatures, CancellationToken ct)
    {
        try
        {
            // Build feature summary for the module grouping prompt
            var featureSummary = new StringBuilder();
            foreach (var f in allFeatures.OrderBy(f => f.Category).ThenBy(f => f.Id))
            {
                featureSummary.AppendLine($"- **{f.Id}**: {f.Name} [{f.Category}] — {f.Description}");
            }

            var template = PromptLoader.Load("FeatureModuleGroupingPrompt.md");
            var prompt = string.Format(template, featureSummary.ToString());

            ct.ThrowIfCancellationRequested();

            var result = await CallSonnetWithRetryAsync(prompt, ModuleOutputSchema, ct);
            if (string.IsNullOrWhiteSpace(result))
            {
                ReportProgress("Module grouping returned no results — skipping module creation");
                return;
            }

            var moduleResponse = DeserializeResponse<SonnetModuleResponse>(result);
            if (moduleResponse?.Modules == null || moduleResponse.Modules.Count == 0)
            {
                ReportProgress("Module grouping returned no modules — skipping");
                return;
            }

            var moduleRegistry = new ModuleRegistryManager();
            var featureIdSet = new HashSet<string>(allFeatures.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            var createdModuleCount = 0;

            foreach (var sm in moduleResponse.Modules)
            {
                ct.ThrowIfCancellationRequested();

                // Filter to only valid feature IDs
                var validFeatureIds = sm.FeatureIds
                    .Where(id => featureIdSet.Contains(id))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();

                var module = new ModuleEntry
                {
                    Id = sm.Id,
                    Name = sm.Name,
                    Description = sm.Description,
                    Category = "",
                    FeatureIds = validFeatureIds,
                    Keywords = [],
                    LastUpdatedAt = DateTime.UtcNow
                };

                await moduleRegistry.SaveModuleAsync(projectPath, module);
                createdModuleCount++;

                // Update features with ParentModuleId
                foreach (var fid in validFeatureIds)
                {
                    var feature = allFeatures.FirstOrDefault(f =>
                        string.Equals(f.Id, fid, StringComparison.OrdinalIgnoreCase));
                    if (feature != null)
                    {
                        feature.ParentModuleId = sm.Id;
                        feature.HierarchyLevel = 0;
                        await _registryManager.SaveFeatureAsync(projectPath, feature);
                    }
                }
            }

            // Use DependencyAnalyzer to infer membership for any features Sonnet missed
            var modules = await moduleRegistry.LoadAllModulesAsync(projectPath);
            var inferredMembership = DependencyAnalyzer.InferModuleMembership(allFeatures, modules);
            foreach (var (featureId, moduleId) in inferredMembership)
            {
                var feature = allFeatures.FirstOrDefault(f =>
                    string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
                if (feature != null)
                {
                    feature.ParentModuleId = moduleId;
                    feature.HierarchyLevel = 0;
                    await _registryManager.SaveFeatureAsync(projectPath, feature);

                    // Also add to the module's FeatureIds
                    var module = modules.FirstOrDefault(m =>
                        string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                    if (module != null && !module.FeatureIds.Contains(featureId, StringComparer.OrdinalIgnoreCase))
                    {
                        module.FeatureIds.Add(featureId);
                        await moduleRegistry.SaveModuleAsync(projectPath, module);
                    }
                }
            }

            ReportProgress($"Created {createdModuleCount} modules");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("FeatureInitializer", $"Module creation failed: {ex.Message}", ex);
            ReportProgress($"Module creation failed (non-fatal): {ex.Message}");
        }
    }

    // ── Helper: Chunk file list ─────────────────────────────────────────

    private static List<List<string>> ChunkFiles(List<string> files, int chunkSize)
    {
        var chunks = new List<List<string>>();
        for (var i = 0; i < files.Count; i += chunkSize)
        {
            chunks.Add(files.GetRange(i, Math.Min(chunkSize, files.Count - i)));
        }
        return chunks;
    }

    // ── Helper: Deserialize Sonnet JSON ─────────────────────────────────

    private static T? DeserializeResponse<T>(string json) where T : class
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException ex)
        {
            AppLogger.Warn("FeatureInitializer", $"Failed to deserialize response: {ex.Message}");
            return null;
        }
    }

    // ── Helper: Scan Source Files ────────────────────────────────────────

    /// <summary>
    /// Returns relative paths of all source files under <paramref name="projectPath"/>,
    /// skipping directories listed in <see cref="FeatureConstants.IgnoredDirectories"/>.
    /// </summary>
    public List<string> ScanSourceFiles(string projectPath)
    {
        var supportedExtensions = SignatureExtractor.GetSupportedExtensions();
        var ignoredDirs = new HashSet<string>(FeatureConstants.IgnoredDirectories, StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (FeatureConstants.IgnoredFileExtensions.Contains(ext))
                    continue;
                if (!supportedExtensions.Contains(ext.ToLowerInvariant()))
                    continue;

                // Check if any path segment is in the ignored set
                var relativePath = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
                var segments = relativePath.Split('/');

                var skip = false;
                for (var i = 0; i < segments.Length - 1; i++) // skip the filename itself
                {
                    if (ignoredDirs.Contains(segments[i]))
                    {
                        skip = true;
                        break;
                    }
                }

                if (!skip)
                    results.Add(relativePath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Error scanning files: {ex.Message}", ex);
        }

        return results;
    }

    // ── Helper: Build Directory Tree ────────────────────────────────────

    /// <summary>
    /// Groups files by their first directory component and returns a compact
    /// tree summary (e.g. "  Managers/ (25 files)").
    /// </summary>
    public string BuildDirectoryTree(string projectPath, List<string> relativeFiles)
    {
        var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in relativeFiles)
        {
            var firstSlash = file.IndexOf('/');
            var dir = firstSlash >= 0 ? file[..firstSlash] : "(root)";

            if (!groups.TryAdd(dir, 1))
                groups[dir]++;
        }

        var sb = new StringBuilder();
        foreach (var (dir, count) in groups)
        {
            if (dir == "(root)")
                sb.AppendLine($"  (root files) ({count} files)");
            else
                sb.AppendLine($"  {dir}/ ({count} files)");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Helper: Detect Project Type ─────────────────────────────────────

    /// <summary>
    /// Checks for well-known project indicator files and returns a string
    /// describing the project type.
    /// </summary>
    public string DetectProjectType(string projectPath)
    {
        var hasCsproj = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories).Any();
        var hasAssetsFolder = Directory.Exists(Path.Combine(projectPath, "Assets"));

        if (hasCsproj && hasAssetsFolder)
            return "Unity C#";

        if (hasCsproj)
            return "C# .NET";

        if (File.Exists(Path.Combine(projectPath, "project.godot")))
            return "GDScript/Godot";

        if (File.Exists(Path.Combine(projectPath, "package.json")))
            return "TypeScript/JavaScript";

        if (File.Exists(Path.Combine(projectPath, "requirements.txt")) ||
            File.Exists(Path.Combine(projectPath, "setup.py")) ||
            File.Exists(Path.Combine(projectPath, "pyproject.toml")))
            return "Python";

        return "Mixed";
    }

    // ── Helper: Call Sonnet via Claude Code CLI ─────────────────────────

    /// <summary>Maximum time to wait for the Sonnet CLI call before giving up.</summary>
    private static readonly TimeSpan SonnetTimeout = TimeSpan.FromMinutes(5);

    private async Task<string?> CallSonnetAsync(string prompt, string schema, CancellationToken ct)
    {
        try
        {
            var escapedSchema = schema.Replace("\"", "\\\"");
            var arguments = $"-p --output-format json --model {AppConstants.ClaudeSonnet} --max-turns 3 --json-schema \"{escapedSchema}\"";
            AppLogger.Info("FeatureInitializer", $"Calling Sonnet CLI. Prompt length: {prompt.Length} chars, model: {AppConstants.ClaudeSonnet}");

            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

            using var process = new Process { StartInfo = psi };

            process.Start();
            JobObjectManager.Instance.AssignProcess(process);
            AppLogger.Info("FeatureInitializer", $"Sonnet CLI process started (PID: {process.Id})");

            // Write the prompt to stdin and close it
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();
            }
            catch (IOException ioEx)
            {
                var earlyStderr = "";
                try { earlyStderr = await process.StandardError.ReadToEndAsync(ct); } catch { }
                AppLogger.Error("FeatureInitializer",
                    $"Failed to write prompt to CLI stdin (pipe closed). stderr: {earlyStderr}", ioEx);
                ReportProgress($"CLI process rejected input: {(string.IsNullOrWhiteSpace(earlyStderr) ? ioEx.Message : earlyStderr.Trim())}");
                return null;
            }

            // Read stdout and stderr concurrently to avoid deadlocks
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            // Apply timeout so we don't hang forever
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SonnetTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                AppLogger.Error("FeatureInitializer",
                    $"Sonnet CLI timed out after {SonnetTimeout.TotalMinutes:F0} minutes (PID: {process.Id}). Killing process.");
                ReportProgress($"AI analysis timed out after {SonnetTimeout.TotalMinutes:F0} minutes");
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stderr))
                AppLogger.Warn("FeatureInitializer", $"Sonnet CLI stderr: {stderr[..Math.Min(500, stderr.Length)]}");

            if (process.ExitCode != 0)
            {
                AppLogger.Error("FeatureInitializer",
                    $"Claude CLI exited with code {process.ExitCode}. stderr: {stderr[..Math.Min(1000, stderr.Length)]}");
                ReportProgress($"AI analysis failed (exit code {process.ExitCode})");
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                AppLogger.Warn("FeatureInitializer", $"Claude CLI returned empty output. stderr: {stderr}");
                ReportProgress("AI analysis returned empty output");
                return null;
            }

            AppLogger.Info("FeatureInitializer", $"Sonnet CLI completed. stdout length: {stdout.Length}");

            var text = Helpers.FormatHelpers.StripAnsiCodes(stdout).Trim();

            // The CLI with --output-format json wraps the result; extract the "result" field
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            string resultJson;
            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                resultJson = structured.GetRawText();
            }
            else if (root.TryGetProperty("result", out var resultElement))
            {
                resultJson = resultElement.ValueKind == JsonValueKind.String
                    ? resultElement.GetString()!
                    : resultElement.GetRawText();
            }
            else
            {
                resultJson = text;
            }

            AppLogger.Info("FeatureInitializer", $"Sonnet result JSON length: {resultJson.Length}");
            return resultJson;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException jsonEx)
        {
            AppLogger.Error("FeatureInitializer", $"Failed to parse Sonnet CLI response as JSON: {jsonEx.Message}", jsonEx);
            ReportProgress("AI response was not valid JSON");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("FeatureInitializer", $"Error calling Claude CLI: {ex.Message}", ex);
            ReportProgress($"AI analysis error: {ex.Message}");
            return null;
        }
    }

    // ── Helper: Git Integration ─────────────────────────────────────────

    private void EnsureGitIntegration(string projectPath)
    {
        try
        {
            // Ensure .spritely/.gitignore
            var spritelyDir = Path.Combine(projectPath, FeatureConstants.SpritelyDir);
            Directory.CreateDirectory(spritelyDir);

            var gitignorePath = Path.Combine(spritelyDir, ".gitignore");
            const string gitignoreContent = """
                # Track features/ (shared context), ignore local caches
                cache/
                *.local.json
                """;

            // Normalize indentation from raw string literal
            var normalizedContent = string.Join('\n',
                gitignoreContent.Split('\n').Select(l => l.TrimStart())) + "\n";

            File.WriteAllText(gitignorePath, normalizedContent, Encoding.UTF8);

            // Check .gitattributes in project root
            var gitattributesPath = Path.Combine(projectPath, ".gitattributes");
            const string spritelyAttributes =
                ".spritely/features/features.jsonl merge=union text eol=lf\n" +
                ".spritely/features/modules.jsonl merge=union text eol=lf\n" +
                ".spritely/features/_metadata.json text eol=lf\n";

            if (File.Exists(gitattributesPath))
            {
                var existing = File.ReadAllText(gitattributesPath, Encoding.UTF8);
                if (!existing.Contains(".spritely/features/features.jsonl"))
                {
                    // Remove legacy patterns if present
                    var cleaned = existing
                        .Replace(".spritely/features/_index.json merge=union text eol=lf\n", "")
                        .Replace(".spritely/features/*.json text eol=lf\n", "");
                    var suffix = cleaned.EndsWith('\n') ? "" : "\n";
                    File.WriteAllText(gitattributesPath,
                        cleaned + suffix + spritelyAttributes, Encoding.UTF8);
                }
            }
            // If .gitattributes doesn't exist, don't create it — only append if it's already there
        }
        catch (Exception ex)
        {
            AppLogger.Debug("FeatureInitializer", $"Error setting up git integration: {ex.Message}", ex);
        }
    }

    // ── Helper: Report Progress ─────────────────────────────────────────

    /// <summary>
    /// Caps each feature's DependsOn to <see cref="FeatureConstants.MaxDependenciesPerFeature"/>
    /// by ranking candidates using keyword overlap via <see cref="FeatureRegistryManager.ScoreFeature"/>,
    /// keeping the top-N most relevant, and discarding the rest.
    /// </summary>
    private static void PruneDependencies(List<FeatureEntry> allFeatures)
    {
        var cap = FeatureConstants.MaxDependenciesPerFeature;
        var featureLookup = allFeatures.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var feature in allFeatures)
        {
            if (feature.DependsOn.Count <= cap)
                continue;

            // Build tokens from the feature's own keywords + description for scoring
            var featureText = string.Join(" ", feature.Keywords);
            if (!string.IsNullOrWhiteSpace(feature.Description))
                featureText += " " + feature.Description;

            var tokens = FeatureRegistryManager.Tokenize(featureText);

            // Score each dependency by relevance to this feature
            var scored = new List<(string DepId, double Score)>();
            foreach (var depId in feature.DependsOn)
            {
                if (featureLookup.TryGetValue(depId, out var depFeature))
                {
                    var score = FeatureRegistryManager.ScoreFeature(tokens, featureText, depFeature);
                    scored.Add((depId, score));
                }
                else
                {
                    scored.Add((depId, 0.0));
                }
            }

            var kept = scored
                .OrderByDescending(s => s.Score)
                .Take(cap)
                .Select(s => s.DepId)
                .ToList();

            var removed = feature.DependsOn.Except(kept, StringComparer.OrdinalIgnoreCase).ToList();

            feature.DependsOn = kept;
            feature.DependsOn.Sort(StringComparer.Ordinal);

            AppLogger.Info("FeatureInitializer",
                $"Pruned {removed.Count} low-relevance dependencies from '{feature.Id}' " +
                $"(kept {kept.Count}/{kept.Count + removed.Count}): removed [{string.Join(", ", removed)}]");
        }
    }

    private void ReportProgress(string message)
    {
        AppLogger.Debug("FeatureInitializer", message);
        ProgressChanged?.Invoke(message);
    }
}
