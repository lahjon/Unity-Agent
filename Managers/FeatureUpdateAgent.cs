using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// After task completion, updates the feature registry with changes detected during the task.
    /// Fire-and-forget — never blocks task teardown and never propagates exceptions.
    /// Failed updates are queued to <c>_pending_updates.json</c> and drained on the next success.
    /// </summary>
    public class FeatureUpdateAgent
    {
        private const string UpdateJsonSchema =
            """{"type":"object","properties":{"updated_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"add_primary_files":{"type":"array","items":{"type":"string"}},"add_secondary_files":{"type":"array","items":{"type":"string"}},"remove_files":{"type":"array","items":{"type":"string"}},"updated_description":{"type":"string"}},"required":["id"]}},"new_features":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"keywords":{"type":"array","items":{"type":"string"}},"primary_files":{"type":"array","items":{"type":"string"}},"secondary_files":{"type":"array","items":{"type":"string"}},"depends_on":{"type":"array","items":{"type":"string"}},"module_id":{"type":"string"}},"required":["id","name","description","keywords","primary_files"]}}},"required":["updated_features","new_features"]}""";

        private static readonly TimeSpan HaikuTimeout = TimeSpan.FromMinutes(2);
        private static readonly JsonSerializerOptions PendingJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Maximum queued entries to prevent unbounded growth from repeated failures.</summary>
        private const int MaxPendingUpdates = 20;

        private readonly FeatureRegistryManager _registryManager;
        private readonly CodebaseIndexManager? _codebaseIndexManager;
        private readonly ModuleRegistryManager? _moduleRegistryManager;

        public FeatureUpdateAgent(
            FeatureRegistryManager registryManager,
            CodebaseIndexManager? codebaseIndexManager = null,
            ModuleRegistryManager? moduleRegistryManager = null)
        {
            _registryManager = registryManager;
            _codebaseIndexManager = codebaseIndexManager;
            _moduleRegistryManager = moduleRegistryManager;
        }

        /// <summary>
        /// Analyzes task completion data and updates the feature registry accordingly.
        /// On success, drains any previously queued (failed) updates first.
        /// On failure, queues the update request to <c>_pending_updates.json</c> for retry.
        /// This method catches all exceptions internally — it is safe to call fire-and-forget.
        /// </summary>
        public async Task UpdateFeaturesAsync(
            string projectPath,
            string taskId,
            string taskDescription,
            string? completionSummary,
            List<string> changedFiles,
            FeatureContextResult? resolverSuggestion = null,
            CancellationToken ct = default)
        {
            try
            {
                if (!_registryManager.RegistryExists(projectPath))
                    return;

                // Skip if there's nothing meaningful to analyze
                if (changedFiles.Count == 0 && string.IsNullOrWhiteSpace(completionSummary))
                    return;

                var index = await _registryManager.LoadIndexAsync(projectPath);
                if (index.Features.Count == 0 && changedFiles.Count == 0)
                    return;

                // Drain any previously failed updates before processing the current one
                await DrainPendingUpdatesAsync(projectPath, ct);

                // Load all features once upfront — used for both the prompt and post-processing
                var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);

                // Normalize changed file paths to relative
                var relativeFiles = changedFiles
                    .Select(f => ToRelativePath(f, projectPath))
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Build compact feature summary so Haiku can map files to features
                var featureSummaries = allFeatures.Select(f => new
                {
                    id = f.Id,
                    name = f.Name,
                    description = f.Description,
                    primaryFiles = f.PrimaryFiles
                });
                var indexJson = JsonSerializer.Serialize(featureSummaries,
                    new JsonSerializerOptions { WriteIndented = false });

                // Load prompt template and format
                var template = PromptLoader.Load("FeatureUpdatePrompt.md");
                var prompt = string.Format(template,
                    taskDescription,
                    completionSummary ?? "No summary",
                    string.Join("\n", relativeFiles),
                    indexJson);

                // Call Haiku CLI for intelligent feature update analysis
                AppLogger.Info("FeatureUpdateAgent", $"Calling Haiku CLI for feature update. Task: {taskId}, changed files: {relativeFiles.Count}");

                var rootResult = await FeatureSystemCliRunner.RunAsync(
                    prompt, UpdateJsonSchema, "FeatureUpdateAgent", HaikuTimeout, ct);

                if (rootResult is null)
                {
                    // Haiku call failed or returned nothing — queue for retry
                    await EnqueuePendingUpdateAsync(projectPath, taskId, taskDescription, completionSummary, relativeFiles);
                    return;
                }

                var root = rootResult.Value;

                // Process updated features
                if (root.TryGetProperty("updated_features", out var updatedArray))
                {
                    foreach (var item in updatedArray.EnumerateArray())
                    {
                        await ProcessUpdatedFeatureAsync(
                            projectPath, taskId, taskDescription, item, allFeatures);
                    }
                }

                // Process new features
                var createdNewFeatureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("new_features", out var newArray))
                {
                    foreach (var item in newArray.EnumerateArray())
                    {
                        var newId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(newId))
                            createdNewFeatureIds.Add(newId);
                        await ProcessNewFeatureAsync(projectPath, taskId, item, allFeatures);
                    }
                }

                // Deferred new-feature creation: if the resolver suggested a new feature
                // but Haiku's update pass didn't create it, create a placeholder now
                // (only after successful task completion — avoids orphaned placeholders)
                if (resolverSuggestion is { IsNewFeature: true, SuggestedNewFeatureId: not null, SuggestedNewFeatureName: not null }
                    && !createdNewFeatureIds.Contains(resolverSuggestion.SuggestedNewFeatureId))
                {
                    var existingFeature = await _registryManager.LoadFeatureAsync(
                        projectPath, resolverSuggestion.SuggestedNewFeatureId);
                    if (existingFeature is null)
                    {
                        var placeholder = new FeatureEntry
                        {
                            Id = resolverSuggestion.SuggestedNewFeatureId,
                            Name = resolverSuggestion.SuggestedNewFeatureName,
                            Description = $"New feature identified from task: {taskDescription}",
                            Keywords = resolverSuggestion.SuggestedKeywords ?? new List<string>(),
                            PlaceholderCreatedAt = DateTime.UtcNow,
                            TouchCount = 1,
                            LastUpdatedAt = DateTime.UtcNow,
                            LastUpdatedByTaskId = taskId
                        };
                        await _registryManager.SaveFeatureAsync(projectPath, placeholder);
                        AppLogger.Info("FeatureUpdateAgent",
                            $"Created deferred placeholder feature '{placeholder.Id}' from resolver suggestion");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureUpdateAgent", $"Feature update failed (fire-and-forget): {ex.Message}", ex);

                // Queue for retry so registry doesn't drift
                try
                {
                    var relativeFiles = changedFiles
                        .Select(f => ToRelativePath(f, projectPath))
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    await EnqueuePendingUpdateAsync(projectPath, taskId, taskDescription, completionSummary, relativeFiles);
                }
                catch (Exception queueEx)
                {
                    AppLogger.Debug("FeatureUpdateAgent", $"Failed to queue pending update: {queueEx.Message}");
                }
            }
        }

        /// <summary>
        /// Applies incremental updates to an existing feature entry: adds/removes files,
        /// updates description, refreshes signatures for newly added primary files.
        /// </summary>
        private async Task ProcessUpdatedFeatureAsync(
            string projectPath, string taskId, string taskDescription, JsonElement item, List<FeatureEntry> allFeatures)
        {
            var featureId = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(featureId))
                return;

            var feature = await _registryManager.LoadFeatureAsync(projectPath, featureId);
            if (feature is null)
                return;

            var newPrimaryFiles = new List<string>();

            // Add primary files
            if (item.TryGetProperty("add_primary_files", out var addPrimary))
            {
                foreach (var f in addPrimary.EnumerateArray())
                {
                    var path = f.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && !feature.PrimaryFiles.Contains(path))
                    {
                        feature.PrimaryFiles.Add(path);
                        newPrimaryFiles.Add(path);
                    }
                }
            }

            // Add secondary files
            if (item.TryGetProperty("add_secondary_files", out var addSecondary))
            {
                foreach (var f in addSecondary.EnumerateArray())
                {
                    var path = f.GetString();
                    if (!string.IsNullOrWhiteSpace(path) && !feature.SecondaryFiles.Contains(path))
                        feature.SecondaryFiles.Add(path);
                }
            }

            // Remove files from both lists
            if (item.TryGetProperty("remove_files", out var removeFiles))
            {
                foreach (var f in removeFiles.EnumerateArray())
                {
                    var path = f.GetString();
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    feature.PrimaryFiles.Remove(path);
                    feature.SecondaryFiles.Remove(path);
                    feature.Context.Signatures.Remove(path);
                }
            }

            // Update description if provided
            if (item.TryGetProperty("updated_description", out var descEl))
            {
                var desc = descEl.GetString();
                if (!string.IsNullOrWhiteSpace(desc))
                    feature.Description = desc;
            }

            // Re-extract signatures for newly added primary files
            foreach (var relPath in newPrimaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                var signatures = SignatureExtractor.ExtractSignatures(absPath);
                if (!string.IsNullOrEmpty(signatures))
                {
                    feature.Context.Signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = signatures
                    };
                }

                // Extract symbol names for newly added files
                var symbolNames = SignatureExtractor.GetSymbolNames(absPath);
                foreach (var sym in symbolNames)
                {
                    if (!feature.SymbolNames.Contains(sym, StringComparer.OrdinalIgnoreCase))
                        feature.SymbolNames.Add(sym);
                }
            }

            feature.SymbolNames.Sort(StringComparer.OrdinalIgnoreCase);

            // Refresh keywords from newly added symbols
            if (newPrimaryFiles.Count > 0)
                RefreshKeywords(feature, taskDescription);

            // Refresh signatures for existing primary files that the task may have modified
            await _registryManager.RefreshStaleSignaturesAsync(projectPath, feature);

            // Update codebase symbol index incrementally for new files
            if (_codebaseIndexManager != null && newPrimaryFiles.Count > 0)
            {
                try
                {
                    await _codebaseIndexManager.UpdateSymbolsForFeature(projectPath, feature.Id, newPrimaryFiles);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureUpdateAgent", $"Failed to update codebase index: {ex.Message}");
                }
            }

            // Re-run dependency analysis when new files are added
            if (newPrimaryFiles.Count > 0)
            {
                try
                {
                    var newDeps = DependencyAnalyzer.ComputeDependsOnForNewFeature(feature, allFeatures, projectPath);
                    foreach (var dep in newDeps)
                    {
                        if (!feature.DependsOn.Contains(dep, StringComparer.OrdinalIgnoreCase))
                            feature.DependsOn.Add(dep);
                    }
                    feature.DependsOn.Sort(StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureUpdateAgent", $"Failed to re-analyze dependencies: {ex.Message}");
                }
            }

            // Sort file lists for deterministic output
            feature.PrimaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            feature.SecondaryFiles.Sort(StringComparer.OrdinalIgnoreCase);

            feature.TouchCount++;
            feature.LastUpdatedAt = DateTime.UtcNow;
            feature.LastUpdatedByTaskId = taskId;

            await _registryManager.SaveFeatureAsync(projectPath, feature);
        }

        /// <summary>
        /// Creates a brand-new feature entry from Haiku's analysis, extracts signatures
        /// for its primary files, and saves it to the registry.
        /// </summary>
        private async Task ProcessNewFeatureAsync(
            string projectPath, string taskId, JsonElement item, List<FeatureEntry> allFeatures)
        {
            var featureId = item.GetProperty("id").GetString();
            var name = item.GetProperty("name").GetString();
            var description = item.GetProperty("description").GetString();

            if (string.IsNullOrWhiteSpace(featureId) || string.IsNullOrWhiteSpace(name))
                return;

            var keywords = new List<string>();
            if (item.TryGetProperty("keywords", out var kwArray))
            {
                foreach (var kw in kwArray.EnumerateArray())
                {
                    var val = kw.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        keywords.Add(val);
                }
            }

            var primaryFiles = new List<string>();
            if (item.TryGetProperty("primary_files", out var pfArray))
            {
                foreach (var f in pfArray.EnumerateArray())
                {
                    var val = f.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        primaryFiles.Add(val);
                }
            }

            var secondaryFiles = new List<string>();
            if (item.TryGetProperty("secondary_files", out var sfArray))
            {
                foreach (var f in sfArray.EnumerateArray())
                {
                    var val = f.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        secondaryFiles.Add(val);
                }
            }

            var category = item.TryGetProperty("category", out var catEl)
                ? catEl.GetString() ?? ""
                : "";

            // Extract signatures for primary files
            var signatures = new Dictionary<string, FileSignature>();
            foreach (var relPath in primaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                var content = SignatureExtractor.ExtractSignatures(absPath);
                if (!string.IsNullOrEmpty(content))
                {
                    signatures[relPath] = new FileSignature
                    {
                        Hash = SignatureExtractor.ComputeFileHash(absPath),
                        Content = content
                    };
                }
            }

            primaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            secondaryFiles.Sort(StringComparer.OrdinalIgnoreCase);
            keywords.Sort(StringComparer.OrdinalIgnoreCase);

            // Extract symbol names from primary files
            var symbolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relPath in primaryFiles)
            {
                var absPath = Path.Combine(projectPath, relPath);
                foreach (var sym in SignatureExtractor.GetSymbolNames(absPath))
                    symbolNames.Add(sym);
            }
            var sortedSymbolNames = symbolNames.ToList();
            sortedSymbolNames.Sort(StringComparer.OrdinalIgnoreCase);

            // Use Haiku-suggested depends_on/module_id as starting point
            var dependsOn = new List<string>();
            if (item.TryGetProperty("depends_on", out var depsArray))
            {
                foreach (var d in depsArray.EnumerateArray())
                {
                    var val = d.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        dependsOn.Add(val);
                }
            }

            string? parentModuleId = null;
            if (item.TryGetProperty("module_id", out var modEl))
                parentModuleId = modEl.GetString();

            var feature = new FeatureEntry
            {
                Id = featureId,
                Name = name,
                Description = description ?? "",
                Category = category,
                Keywords = keywords,
                PrimaryFiles = primaryFiles,
                SecondaryFiles = secondaryFiles,
                Context = new FeatureContext { Signatures = signatures },
                SymbolNames = sortedSymbolNames,
                DependsOn = dependsOn,
                ParentModuleId = parentModuleId,
                TouchCount = 1,
                LastUpdatedAt = DateTime.UtcNow,
                LastUpdatedByTaskId = taskId
            };

            // Code-computed dependencies take precedence over Haiku suggestions
            try
            {
                var computedDeps = DependencyAnalyzer.ComputeDependsOnForNewFeature(feature, allFeatures, projectPath);
                foreach (var dep in computedDeps)
                {
                    if (!feature.DependsOn.Contains(dep, StringComparer.OrdinalIgnoreCase))
                        feature.DependsOn.Add(dep);
                }
                feature.DependsOn.Sort(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureUpdateAgent", $"Failed to compute dependencies for new feature: {ex.Message}");
            }

            // Infer module membership if not already set
            if (string.IsNullOrEmpty(feature.ParentModuleId) && _moduleRegistryManager != null)
            {
                try
                {
                    var allModules = await _moduleRegistryManager.LoadAllModulesAsync(projectPath);
                    if (allModules.Count > 0)
                    {
                        var membership = DependencyAnalyzer.InferModuleMembership(
                            new List<FeatureEntry> { feature }, allModules);
                        if (membership.TryGetValue(feature.Id, out var inferredModuleId))
                            feature.ParentModuleId = inferredModuleId;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureUpdateAgent", $"Failed to infer module membership: {ex.Message}");
                }
            }

            await _registryManager.SaveFeatureAsync(projectPath, feature);

            // Update codebase symbol index incrementally
            if (_codebaseIndexManager != null && primaryFiles.Count > 0)
            {
                try
                {
                    await _codebaseIndexManager.UpdateSymbolsForFeature(projectPath, feature.Id, primaryFiles);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureUpdateAgent", $"Failed to update codebase index for new feature: {ex.Message}");
                }
            }
        }

        private const int MaxKeywords = 20;
        private static readonly Regex CamelCaseSplitRegex = new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        /// <summary>
        /// Merges novel tokens derived from symbol names into the feature's keywords.
        /// Prefers tokens that appear in both the new symbols and the task description.
        /// Caps keywords at <see cref="MaxKeywords"/>.
        /// </summary>
        private static void RefreshKeywords(FeatureEntry feature, string taskDescription)
        {
            var existingKeywords = new HashSet<string>(feature.Keywords, StringComparer.OrdinalIgnoreCase);

            // Split all symbol names into individual tokens via camelCase/PascalCase boundaries
            var symbolTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in feature.SymbolNames)
            {
                foreach (var part in CamelCaseSplitRegex.Split(sym))
                {
                    var lower = part.ToLowerInvariant();
                    if (lower.Length > 1 && !FeatureRegistryManager.Stopwords.Contains(lower))
                        symbolTokens.Add(lower);
                }
            }

            // Find novel tokens not already in keywords
            var novelTokens = symbolTokens.Where(t => !existingKeywords.Contains(t)).ToList();
            if (novelTokens.Count == 0)
                return;

            // Tokens appearing in both symbols and task description get priority
            var taskTokens = FeatureRegistryManager.Tokenize(taskDescription);
            var prioritized = novelTokens
                .OrderByDescending(t => taskTokens.Contains(t) ? 1 : 0)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var token in prioritized)
            {
                if (feature.Keywords.Count >= MaxKeywords)
                    break;
                feature.Keywords.Add(token);
            }

            // If we exceeded the cap (existing + new > 20), trim least-relevant entries.
            // Keep entries that overlap with task description; drop others from the tail.
            if (feature.Keywords.Count > MaxKeywords)
            {
                feature.Keywords = feature.Keywords
                    .OrderByDescending(k => taskTokens.Contains(k) ? 1 : 0)
                    .ThenByDescending(k => symbolTokens.Contains(k) ? 1 : 0)
                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxKeywords)
                    .ToList();
            }

            feature.Keywords.Sort(StringComparer.OrdinalIgnoreCase);
        }

        // ── Pending Update Queue ─────────────────────────────────────────

        /// <summary>Returns the path to the pending updates file for a project.</summary>
        private static string GetPendingUpdatesPath(string projectPath)
            => Path.Combine(projectPath, FeatureConstants.SpritelyDir, FeatureConstants.PendingUpdatesFileName);

        /// <summary>
        /// Queues a failed update request for later retry.
        /// Caps the queue at <see cref="MaxPendingUpdates"/> entries (oldest dropped).
        /// </summary>
        private static async Task EnqueuePendingUpdateAsync(
            string projectPath,
            string taskId,
            string taskDescription,
            string? completionSummary,
            List<string> changedFiles)
        {
            try
            {
                var filePath = GetPendingUpdatesPath(projectPath);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                var queue = await LoadPendingQueueAsync(filePath);

                queue.Add(new PendingFeatureUpdate
                {
                    TaskId = taskId,
                    TaskDescription = taskDescription,
                    CompletionSummary = completionSummary,
                    ChangedFiles = changedFiles,
                    QueuedAt = DateTime.UtcNow
                });

                // Cap at MaxPendingUpdates — drop oldest entries
                if (queue.Count > MaxPendingUpdates)
                    queue = queue.Skip(queue.Count - MaxPendingUpdates).ToList();

                var json = JsonSerializer.Serialize(queue, PendingJsonOptions);
                await File.WriteAllTextAsync(filePath, json);

                AppLogger.Info("FeatureUpdateAgent",
                    $"Queued pending feature update for task '{taskId}' ({queue.Count} total pending)");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureUpdateAgent", $"Failed to enqueue pending update: {ex.Message}");
            }
        }

        /// <summary>
        /// Drains the pending update queue by re-running each queued update through Haiku.
        /// Entries that fail again are re-queued. Successful entries are removed.
        /// </summary>
        private async Task DrainPendingUpdatesAsync(string projectPath, CancellationToken ct)
        {
            var filePath = GetPendingUpdatesPath(projectPath);
            if (!File.Exists(filePath))
                return;

            List<PendingFeatureUpdate> queue;
            try
            {
                queue = await LoadPendingQueueAsync(filePath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureUpdateAgent", $"Failed to load pending queue: {ex.Message}");
                return;
            }

            if (queue.Count == 0)
                return;

            AppLogger.Info("FeatureUpdateAgent", $"Draining {queue.Count} pending feature update(s)");

            // Clear the file first — successfully processed entries won't be re-added,
            // failures during drain will be re-queued by the individual calls
            try { File.Delete(filePath); }
            catch { /* best-effort */ }

            var allFeatures = await _registryManager.LoadAllFeaturesAsync(projectPath);

            foreach (var pending in queue)
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        // Re-queue remaining items
                        var remaining = queue.SkipWhile(q => q != pending).ToList();
                        await SavePendingQueueAsync(filePath, remaining);
                        return;
                    }

                    var featureSummaries = allFeatures.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        description = f.Description,
                        primaryFiles = f.PrimaryFiles
                    });
                    var indexJson = JsonSerializer.Serialize(featureSummaries,
                        new JsonSerializerOptions { WriteIndented = false });

                    var template = PromptLoader.Load("FeatureUpdatePrompt.md");
                    var prompt = string.Format(template,
                        pending.TaskDescription,
                        pending.CompletionSummary ?? "No summary",
                        string.Join("\n", pending.ChangedFiles),
                        indexJson);

                    var rootResult = await FeatureSystemCliRunner.RunAsync(
                        prompt, UpdateJsonSchema, "FeatureUpdateAgent-drain", HaikuTimeout, ct);

                    if (rootResult is null)
                    {
                        // Still failing — re-queue this entry
                        await EnqueuePendingUpdateAsync(projectPath, pending.TaskId,
                            pending.TaskDescription, pending.CompletionSummary, pending.ChangedFiles);
                        continue;
                    }

                    var root = rootResult.Value;

                    if (root.TryGetProperty("updated_features", out var updatedArray))
                    {
                        foreach (var item in updatedArray.EnumerateArray())
                            await ProcessUpdatedFeatureAsync(
                                projectPath, pending.TaskId, pending.TaskDescription, item, allFeatures);
                    }

                    if (root.TryGetProperty("new_features", out var newArray))
                    {
                        foreach (var item in newArray.EnumerateArray())
                            await ProcessNewFeatureAsync(projectPath, pending.TaskId, item, allFeatures);
                    }

                    AppLogger.Info("FeatureUpdateAgent",
                        $"Successfully drained pending update for task '{pending.TaskId}'");
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("FeatureUpdateAgent",
                        $"Failed to drain pending update for task '{pending.TaskId}': {ex.Message}");
                    // Re-queue on exception
                    await EnqueuePendingUpdateAsync(projectPath, pending.TaskId,
                        pending.TaskDescription, pending.CompletionSummary, pending.ChangedFiles);
                }
            }
        }

        private static async Task<List<PendingFeatureUpdate>> LoadPendingQueueAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<PendingFeatureUpdate>();

            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<List<PendingFeatureUpdate>>(json, PendingJsonOptions)
                   ?? new List<PendingFeatureUpdate>();
        }

        private static async Task SavePendingQueueAsync(string filePath, List<PendingFeatureUpdate> queue)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var json = JsonSerializer.Serialize(queue, PendingJsonOptions);
                await File.WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureUpdateAgent", $"Failed to save pending queue: {ex.Message}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Converts an absolute file path to a path relative to the project root.
        /// Returns the original path if it is already relative or cannot be made relative.
        /// </summary>
        private static string ToRelativePath(string filePath, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            // Normalize separators for consistent comparison
            var normalizedFile = filePath.Replace('\\', '/').TrimEnd('/');
            var normalizedProject = projectPath.Replace('\\', '/').TrimEnd('/');

            if (normalizedFile.StartsWith(normalizedProject + "/", StringComparison.OrdinalIgnoreCase))
                return normalizedFile[(normalizedProject.Length + 1)..];

            // Already relative or from a different root — return as-is
            return filePath.Replace('\\', '/');
        }

        /// <summary>
        /// Lightweight DTO representing a feature update that failed and was queued for retry.
        /// Persisted to <c>.spritely/_pending_updates.json</c>.
        /// </summary>
        internal sealed class PendingFeatureUpdate
        {
            public string TaskId { get; set; } = "";
            public string TaskDescription { get; set; } = "";
            public string? CompletionSummary { get; set; }
            public List<string> ChangedFiles { get; set; } = new();
            public DateTime QueuedAt { get; set; }
        }
    }
}
