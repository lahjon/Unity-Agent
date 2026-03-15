using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Managers.DataStore;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Low-level JSONL persistence for the feature database.
    /// Each record is stored as a single compact JSON line, sorted by ID.
    /// This format gives line-level git diffs and minimizes merge conflicts.
    /// </summary>
    public static class FeatureDatabase
    {
        private static readonly JsonSerializerOptions CompactOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        private static readonly JsonSerializerOptions PrettyOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // ── Features JSONL ──────────────────────────────────────────────

        /// <summary>
        /// Reads all features from the JSONL database file.
        /// Returns empty list if the file does not exist or is empty.
        /// </summary>
        public static async Task<List<FeatureEntry>> LoadFeaturesAsync(string projectPath)
        {
            return await GetFeatureStore(projectPath).LoadAsync();
        }

        /// <summary>
        /// Writes all features to the JSONL database file, one per line, sorted by ID.
        /// </summary>
        public static async Task SaveFeaturesAsync(string projectPath, List<FeatureEntry> features)
        {
            var sorted = features.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
            await GetFeatureStore(projectPath).SaveAsync(sorted);
        }

        /// <summary>
        /// Loads a single feature by ID without reading all features.
        /// Scans line-by-line for the matching ID. Returns null if not found.
        /// </summary>
        public static async Task<FeatureEntry?> LoadFeatureByIdAsync(string projectPath, string featureId)
        {
            var filePath = GetFeaturesPath(projectPath);
            if (!File.Exists(filePath))
                return null;

            // Since lines are sorted by ID, we could binary search, but linear is fine for ~100 features
            await foreach (var line in ReadLinesAsync(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Quick check: does this line contain the feature ID before parsing?
                if (!line.Contains($"\"id\":\"{featureId}\"", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains($"\"id\": \"{featureId}\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var feature = JsonSerializer.Deserialize<FeatureEntry>(line, CompactOptions);
                    if (feature != null && string.Equals(feature.Id, featureId, StringComparison.OrdinalIgnoreCase))
                        return feature;
                }
                catch { /* skip malformed line */ }
            }

            return null;
        }

        /// <summary>
        /// Updates or inserts a single feature in the JSONL file.
        /// Reads all features, replaces or adds the entry, and writes back.
        /// </summary>
        public static async Task UpsertFeatureAsync(string projectPath, FeatureEntry feature)
        {
            var features = await LoadFeaturesAsync(projectPath);
            var existingIndex = features.FindIndex(f =>
                string.Equals(f.Id, feature.Id, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
                features[existingIndex] = feature;
            else
                features.Add(feature);

            await SaveFeaturesAsync(projectPath, features);
        }

        /// <summary>
        /// Removes a feature by ID from the JSONL file.
        /// </summary>
        public static async Task RemoveFeatureAsync(string projectPath, string featureId)
        {
            var features = await LoadFeaturesAsync(projectPath);
            features.RemoveAll(f => string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
            await SaveFeaturesAsync(projectPath, features);
        }

        // ── Modules JSONL ───────────────────────────────────────────────

        /// <summary>Reads all modules from the JSONL database file.</summary>
        public static async Task<List<ModuleEntry>> LoadModulesAsync(string projectPath)
        {
            return await GetModuleStore(projectPath).LoadAsync();
        }

        /// <summary>Writes all modules to the JSONL database file, sorted by ID.</summary>
        public static async Task SaveModulesAsync(string projectPath, List<ModuleEntry> modules)
        {
            var sorted = modules.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            await GetModuleStore(projectPath).SaveAsync(sorted);
        }

        /// <summary>Loads a single module by ID.</summary>
        public static async Task<ModuleEntry?> LoadModuleByIdAsync(string projectPath, string moduleId)
        {
            var filePath = GetModulesPath(projectPath);
            if (!File.Exists(filePath))
                return null;

            await foreach (var line in ReadLinesAsync(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!line.Contains($"\"id\":\"{moduleId}\"", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains($"\"id\": \"{moduleId}\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var module = JsonSerializer.Deserialize<ModuleEntry>(line, CompactOptions);
                    if (module != null && string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                        return module;
                }
                catch { /* skip malformed line */ }
            }

            return null;
        }

        /// <summary>Updates or inserts a single module.</summary>
        public static async Task UpsertModuleAsync(string projectPath, ModuleEntry module)
        {
            var modules = await LoadModulesAsync(projectPath);
            var existingIndex = modules.FindIndex(m =>
                string.Equals(m.Id, module.Id, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
                modules[existingIndex] = module;
            else
                modules.Add(module);

            await SaveModulesAsync(projectPath, modules);
        }

        /// <summary>Removes a module by ID.</summary>
        public static async Task RemoveModuleAsync(string projectPath, string moduleId)
        {
            var modules = await LoadModulesAsync(projectPath);
            modules.RemoveAll(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            await SaveModulesAsync(projectPath, modules);
        }

        // ── Metadata ────────────────────────────────────────────────────

        /// <summary>Loads the metadata file. Returns defaults if missing.</summary>
        public static async Task<FeatureMetadata> LoadMetadataAsync(string projectPath)
        {
            try
            {
                var store = GetMetadataStore(projectPath);
                return await store.LoadAsync().ConfigureAwait(false) ?? new FeatureMetadata();
            }
            catch
            {
                return new FeatureMetadata();
            }
        }

        /// <summary>Saves the metadata file.</summary>
        public static async Task SaveMetadataAsync(string projectPath, FeatureMetadata metadata)
        {
            var store = GetMetadataStore(projectPath);
            metadata.LastUpdatedAt = DateTime.UtcNow;
            await store.SaveAsync(metadata).ConfigureAwait(false);
        }

        // ── Migration ───────────────────────────────────────────────────

        /// <summary>
        /// Checks if the project uses the legacy per-file JSON format and migrates to JSONL.
        /// Returns true if migration was performed.
        /// </summary>
        public static async Task<bool> MigrateIfNeededAsync(string projectPath)
        {
            var featuresDir = GetFeaturesDir(projectPath);
            var jsonlPath = GetFeaturesPath(projectPath);
            var legacyIndexPath = Path.Combine(featuresDir, FeatureConstants.IndexFileName);

            // Already migrated or fresh project
            if (File.Exists(jsonlPath) || !File.Exists(legacyIndexPath))
                return false;

            try
            {
                AppLogger.Info("FeatureDatabase", $"Migrating legacy JSON files to JSONL for {Path.GetFileName(projectPath)}");

                // Load legacy index
                var indexJson = await File.ReadAllTextAsync(legacyIndexPath);
                var legacyIndex = JsonSerializer.Deserialize<FeatureIndex>(indexJson, PrettyOptions);
                if (legacyIndex == null)
                    return false;

                // Load all individual feature files
                var features = new List<FeatureEntry>();
                foreach (var entry in legacyIndex.Features)
                {
                    var featureFilePath = Path.Combine(featuresDir, $"{entry.Id}.json");
                    if (!File.Exists(featureFilePath))
                        continue;

                    try
                    {
                        var featureJson = await File.ReadAllTextAsync(featureFilePath);
                        var feature = JsonSerializer.Deserialize<FeatureEntry>(featureJson, PrettyOptions);
                        if (feature != null)
                            features.Add(feature);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("FeatureDatabase", $"Skipping malformed feature file '{entry.Id}': {ex.Message}");
                    }
                }

                // Load legacy modules
                var modules = new List<ModuleEntry>();
                var moduleIndexPath = Path.Combine(featuresDir, FeatureConstants.ModuleIndexFileName);
                if (File.Exists(moduleIndexPath))
                {
                    try
                    {
                        var moduleIndexJson = await File.ReadAllTextAsync(moduleIndexPath);
                        var moduleIndex = JsonSerializer.Deserialize<ModuleIndex>(moduleIndexJson, PrettyOptions);
                        if (moduleIndex != null)
                        {
                            foreach (var moduleEntry in moduleIndex.Modules)
                            {
                                var moduleFilePath = Path.Combine(featuresDir, $"{moduleEntry.Id}{FeatureConstants.ModuleFileExtension}");
                                if (!File.Exists(moduleFilePath))
                                    continue;

                                try
                                {
                                    var moduleJson = await File.ReadAllTextAsync(moduleFilePath);
                                    var module = JsonSerializer.Deserialize<ModuleEntry>(moduleJson, PrettyOptions);
                                    if (module != null)
                                        modules.Add(module);
                                }
                                catch { /* skip malformed */ }
                            }
                        }
                    }
                    catch { /* skip if module index is malformed */ }
                }

                // Write new JSONL files
                Directory.CreateDirectory(featuresDir);
                await SaveFeaturesAsync(projectPath, features);
                await SaveModulesAsync(projectPath, modules);

                // Write metadata
                var metadata = new FeatureMetadata
                {
                    Version = 2,
                    SymbolIndexVersion = legacyIndex.SymbolIndexVersion
                };
                await SaveMetadataAsync(projectPath, metadata);

                // Remove legacy files (keep _codebase_index.json and _pending_updates.json)
                CleanupLegacyFiles(featuresDir, legacyIndex, modules);

                AppLogger.Info("FeatureDatabase",
                    $"Migration complete: {features.Count} features, {modules.Count} modules → JSONL");

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeatureDatabase", $"Migration failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Removes legacy individual JSON files after successful migration.
        /// Preserves _codebase_index.json and _pending_updates.json.
        /// </summary>
        private static void CleanupLegacyFiles(string featuresDir, FeatureIndex legacyIndex, List<ModuleEntry> modules)
        {
            try
            {
                // Remove individual feature files
                foreach (var entry in legacyIndex.Features)
                {
                    var filePath = Path.Combine(featuresDir, $"{entry.Id}.json");
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }

                // Remove individual module files
                foreach (var module in modules)
                {
                    var filePath = Path.Combine(featuresDir, $"{module.Id}{FeatureConstants.ModuleFileExtension}");
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }

                // Remove legacy index files
                var indexPath = Path.Combine(featuresDir, FeatureConstants.IndexFileName);
                if (File.Exists(indexPath))
                    File.Delete(indexPath);

                var moduleIndexPath = Path.Combine(featuresDir, FeatureConstants.ModuleIndexFileName);
                if (File.Exists(moduleIndexPath))
                    File.Delete(moduleIndexPath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("FeatureDatabase", $"Error cleaning up legacy files: {ex.Message}");
            }
        }

        // ── Path Helpers ────────────────────────────────────────────────

        /// <summary>Returns the features directory path.</summary>
        public static string GetFeaturesDir(string projectPath)
            => Path.Combine(projectPath, FeatureConstants.SpritelyDir, FeatureConstants.FeaturesDir);

        /// <summary>Returns the path to the features JSONL file.</summary>
        public static string GetFeaturesPath(string projectPath)
            => Path.Combine(GetFeaturesDir(projectPath), FeatureConstants.FeaturesDatabaseFileName);

        /// <summary>Returns the path to the modules JSONL file.</summary>
        public static string GetModulesPath(string projectPath)
            => Path.Combine(GetFeaturesDir(projectPath), FeatureConstants.ModulesDatabaseFileName);

        /// <summary>Returns the path to the metadata JSON file.</summary>
        public static string GetMetadataPath(string projectPath)
            => Path.Combine(GetFeaturesDir(projectPath), FeatureConstants.MetadataFileName);

        // ── DataStore Integration ──────────────────────────────────────

        private static readonly ConcurrentDictionary<string, IJsonlDataStore<FeatureEntry>> _featureStores = new();
        private static readonly ConcurrentDictionary<string, IJsonlDataStore<ModuleEntry>> _moduleStores = new();
        private static readonly ConcurrentDictionary<string, IDataStore<FeatureMetadata>> _metadataStores = new();

        private static readonly DataStoreOptions _jsonlStoreOptions = new()
        {
            SchemaVersion = 1,
            AtomicWrite = false, // Feature files are in-repo, direct write is fine
            CallerName = "FeatureDatabase",
            JsonOptions = CompactOptions
        };

        private static readonly DataStoreOptions _metadataStoreOptions = new()
        {
            SchemaVersion = 1,
            AtomicWrite = false,
            CallerName = "FeatureDatabase",
            JsonOptions = PrettyOptions
        };

        internal static IJsonlDataStore<FeatureEntry> GetFeatureStore(string projectPath)
        {
            return _featureStores.GetOrAdd(GetFeaturesPath(projectPath),
                path => new JsonlDataStore<FeatureEntry>(path, _jsonlStoreOptions));
        }

        internal static IJsonlDataStore<ModuleEntry> GetModuleStore(string projectPath)
        {
            return _moduleStores.GetOrAdd(GetModulesPath(projectPath),
                path => new JsonlDataStore<ModuleEntry>(path, _jsonlStoreOptions));
        }

        internal static IDataStore<FeatureMetadata> GetMetadataStore(string projectPath)
        {
            return _metadataStores.GetOrAdd(GetMetadataPath(projectPath),
                path => new JsonDataStore<FeatureMetadata>(path, _metadataStoreOptions));
        }

        // ── JSONL Primitives (kept for migration code) ──────────────────

        private static async Task<List<T>> LoadJsonlAsync<T>(string filePath) where T : class
        {
            var store = new JsonlDataStore<T>(filePath, _jsonlStoreOptions);
            return await store.LoadAsync();
        }

        private static async Task SaveJsonlAsync<T>(string filePath, List<T> items) where T : class
        {
            var store = new JsonlDataStore<T>(filePath, _jsonlStoreOptions);
            await store.SaveAsync(items);
        }

        /// <summary>Async line reader for JSONL files (used by LoadFeatureByIdAsync for streaming).</summary>
        private static async IAsyncEnumerable<string> ReadLinesAsync(string filePath)
        {
            using var reader = new StreamReader(filePath, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
                yield return line;
        }

        /// <summary>
        /// Checks if the JSONL database exists for a project.
        /// Falls back to checking legacy _index.json for migration scenarios.
        /// </summary>
        public static bool DatabaseExists(string projectPath)
        {
            var jsonlPath = GetFeaturesPath(projectPath);
            if (File.Exists(jsonlPath))
                return true;

            // Check legacy format
            var legacyPath = Path.Combine(GetFeaturesDir(projectPath), FeatureConstants.IndexFileName);
            return File.Exists(legacyPath);
        }
    }
}
