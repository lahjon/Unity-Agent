using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages the module layer of the feature hierarchy.
    /// Modules are the top-level grouping above features, stored as individual
    /// <c>{module-id}.module.json</c> files with a <c>_module_index.json</c> manifest.
    /// </summary>
    public class ModuleRegistryManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // ── Persistence ──────────────────────────────────────────────────

        /// <summary>Returns the absolute path to the features directory (shared with feature files).</summary>
        private static string GetFeaturesPath(string projectPath)
            => Path.Combine(projectPath, FeatureConstants.SpritelyDir, FeatureConstants.FeaturesDir);

        /// <summary>Returns the file path for a module's JSON file.</summary>
        private static string GetModuleFilePath(string projectPath, string moduleId)
            => Path.Combine(GetFeaturesPath(projectPath), $"{moduleId}{FeatureConstants.ModuleFileExtension}");

        /// <summary>Loads and deserializes <c>_module_index.json</c>. Returns empty index if missing.</summary>
        public async Task<ModuleIndex> LoadModuleIndexAsync(string projectPath)
        {
            var indexPath = Path.Combine(GetFeaturesPath(projectPath), FeatureConstants.ModuleIndexFileName);
            try
            {
                if (!File.Exists(indexPath))
                    return new ModuleIndex();

                var json = await File.ReadAllTextAsync(indexPath);
                return JsonSerializer.Deserialize<ModuleIndex>(json, JsonOptions) ?? new ModuleIndex();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to load module index: {ex.Message}", ex);
                return new ModuleIndex();
            }
        }

        /// <summary>Saves the module index, sorted by Id.</summary>
        public async Task SaveModuleIndexAsync(string projectPath, ModuleIndex index)
        {
            using var guard = AcquireMutex(projectPath);
            await SaveModuleIndexInternalAsync(projectPath, index);
        }

        /// <summary>Loads a single module entry by id. Returns null if not found.</summary>
        public async Task<ModuleEntry?> LoadModuleAsync(string projectPath, string moduleId)
        {
            var filePath = GetModuleFilePath(projectPath, moduleId);
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<ModuleEntry>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to load module '{moduleId}': {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>Loads all modules referenced in the index.</summary>
        public async Task<List<ModuleEntry>> LoadAllModulesAsync(string projectPath)
        {
            var index = await LoadModuleIndexAsync(projectPath);
            var results = new List<ModuleEntry>();

            foreach (var entry in index.Modules)
            {
                var module = await LoadModuleAsync(projectPath, entry.Id);
                if (module != null)
                    results.Add(module);
            }

            return results;
        }

        /// <summary>
        /// Saves a module file and updates the index if the module is new.
        /// All list properties are sorted before serialization.
        /// </summary>
        public async Task SaveModuleAsync(string projectPath, ModuleEntry module)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                var featuresPath = GetFeaturesPath(projectPath);
                Directory.CreateDirectory(featuresPath);

                SortModuleLists(module);

                var json = SerializeDeterministic(module);
                var filePath = GetModuleFilePath(projectPath, module.Id);
                await File.WriteAllTextAsync(filePath, json);

                var index = await LoadModuleIndexAsync(projectPath);
                if (!index.Modules.Any(m => m.Id == module.Id))
                {
                    index.Modules.Add(new ModuleIndexEntry
                    {
                        Id = module.Id,
                        Name = module.Name,
                        FeatureIds = new List<string>(module.FeatureIds)
                    });
                    await SaveModuleIndexInternalAsync(projectPath, index);
                }
                else
                {
                    // Update existing entry's name and feature list
                    var existing = index.Modules.First(m => m.Id == module.Id);
                    existing.Name = module.Name;
                    existing.FeatureIds = new List<string>(module.FeatureIds);
                    await SaveModuleIndexInternalAsync(projectPath, index);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to save module '{module.Id}': {ex.Message}", ex);
            }
        }

        /// <summary>Removes a module file and its index entry.</summary>
        public async Task RemoveModuleAsync(string projectPath, string moduleId)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                var filePath = GetModuleFilePath(projectPath, moduleId);
                if (File.Exists(filePath))
                    File.Delete(filePath);

                var index = await LoadModuleIndexAsync(projectPath);
                index.Modules.RemoveAll(m => m.Id == moduleId);
                await SaveModuleIndexInternalAsync(projectPath, index);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to remove module '{moduleId}': {ex.Message}", ex);
            }
        }

        // ── Queries ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns features whose <see cref="FeatureEntry.ParentModuleId"/> matches the module's Id.
        /// </summary>
        public static List<FeatureEntry> GetFeaturesInModule(ModuleEntry module, List<FeatureEntry> allFeatures)
            => allFeatures.Where(f => f.ParentModuleId == module.Id).ToList();

        /// <summary>
        /// Given a feature, finds all other features in the same module (module siblings),
        /// excluding the queried feature itself.
        /// </summary>
        public static List<FeatureEntry> GetRelatedFeaturesViaModule(
            string featureId,
            List<FeatureEntry> allFeatures,
            List<ModuleEntry> allModules)
        {
            var feature = allFeatures.FirstOrDefault(f => f.Id == featureId);
            if (feature?.ParentModuleId == null)
                return new List<FeatureEntry>();

            var parentModule = allModules.FirstOrDefault(m => m.Id == feature.ParentModuleId);
            if (parentModule == null)
                return new List<FeatureEntry>();

            return allFeatures
                .Where(f => f.ParentModuleId == parentModule.Id && f.Id != featureId)
                .ToList();
        }

        /// <summary>
        /// Deterministically rebuilds the module index from a list of modules.
        /// Modules are sorted by Id.
        /// </summary>
        public static ModuleIndex RebuildModuleIndex(List<ModuleEntry> modules)
        {
            var index = new ModuleIndex
            {
                Version = 1,
                Modules = modules
                    .OrderBy(m => m.Id, StringComparer.Ordinal)
                    .Select(m => new ModuleIndexEntry
                    {
                        Id = m.Id,
                        Name = m.Name,
                        FeatureIds = m.FeatureIds.OrderBy(id => id, StringComparer.Ordinal).ToList()
                    })
                    .ToList()
            };

            return index;
        }

        // ── Private Helpers ──────────────────────────────────────────────

        /// <summary>Saves the module index without acquiring the mutex (caller must hold it).</summary>
        private async Task SaveModuleIndexInternalAsync(string projectPath, ModuleIndex index)
        {
            try
            {
                var featuresPath = GetFeaturesPath(projectPath);
                Directory.CreateDirectory(featuresPath);

                index.Modules = index.Modules.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

                var json = SerializeDeterministic(index);
                var indexPath = Path.Combine(featuresPath, FeatureConstants.ModuleIndexFileName);
                await File.WriteAllTextAsync(indexPath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to save module index: {ex.Message}", ex);
            }
        }

        private static string SerializeDeterministic<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return json.Replace("\r\n", "\n");
        }

        private static void SortModuleLists(ModuleEntry module)
        {
            module.FeatureIds.Sort(StringComparer.Ordinal);
            module.Keywords.Sort(StringComparer.Ordinal);
        }

        private static string ComputePathHash(string projectPath)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        /// <summary>
        /// Acquires a named mutex for write operations. Uses a separate mutex name
        /// from FeatureRegistryManager to avoid cross-blocking.
        /// </summary>
        private static MutexGuard AcquireMutex(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            var mutexName = $"Global\\Spritely_ModuleRegistry_{hash}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(FeatureConstants.MutexTimeoutMs))
                {
                    mutex.Dispose();
                    throw new TimeoutException(
                        $"[ModuleRegistry] Timed out acquiring mutex for project: {projectPath}");
                }
            }
            catch (AbandonedMutexException)
            {
                AppLogger.Debug("ModuleRegistry", "Acquired abandoned mutex — previous holder likely crashed.");
            }

            return new MutexGuard(mutex);
        }

        private sealed class MutexGuard : IDisposable
        {
            private readonly Mutex _mutex;
            private bool _disposed;

            public MutexGuard(Mutex mutex) => _mutex = mutex;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
                finally { _mutex.Dispose(); }
            }
        }
    }
}
