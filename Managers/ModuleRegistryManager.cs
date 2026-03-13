using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages the module layer of the feature hierarchy.
    /// Modules are stored in the JSONL database (<c>modules.jsonl</c>).
    /// </summary>
    public class ModuleRegistryManager
    {
        // ── Persistence ──────────────────────────────────────────────────

        /// <summary>Builds a <see cref="ModuleIndex"/> in memory from the JSONL database.</summary>
        public async Task<ModuleIndex> LoadModuleIndexAsync(string projectPath)
        {
            try
            {
                var modules = await FeatureDatabase.LoadModulesAsync(projectPath);
                return RebuildModuleIndex(modules);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to load module index: {ex.Message}", ex);
                return new ModuleIndex();
            }
        }

        /// <summary>Saves all modules from the index back to the JSONL database.</summary>
        public async Task SaveModuleIndexAsync(string projectPath, ModuleIndex index)
        {
            using var guard = AcquireMutex(projectPath);
            var modules = await FeatureDatabase.LoadModulesAsync(projectPath);

            // Sync index entries back to module objects
            foreach (var entry in index.Modules)
            {
                var module = modules.FirstOrDefault(m => m.Id == entry.Id);
                if (module != null)
                {
                    module.Name = entry.Name;
                    module.FeatureIds = new List<string>(entry.FeatureIds);
                }
            }

            await FeatureDatabase.SaveModulesAsync(projectPath, modules);
        }

        /// <summary>Loads a single module entry by id from the JSONL database.</summary>
        public async Task<ModuleEntry?> LoadModuleAsync(string projectPath, string moduleId)
        {
            try
            {
                return await FeatureDatabase.LoadModuleByIdAsync(projectPath, moduleId);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to load module '{moduleId}': {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>Loads all modules from the JSONL database.</summary>
        public async Task<List<ModuleEntry>> LoadAllModulesAsync(string projectPath)
        {
            try
            {
                return await FeatureDatabase.LoadModulesAsync(projectPath);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to load all modules: {ex.Message}", ex);
                return new List<ModuleEntry>();
            }
        }

        /// <summary>Saves a module to the JSONL database (upsert).</summary>
        public async Task SaveModuleAsync(string projectPath, ModuleEntry module)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                SortModuleLists(module);
                await FeatureDatabase.UpsertModuleAsync(projectPath, module);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ModuleRegistry", $"Failed to save module '{module.Id}': {ex.Message}", ex);
            }
        }

        /// <summary>Removes a module from the JSONL database.</summary>
        public async Task RemoveModuleAsync(string projectPath, string moduleId)
        {
            using var guard = AcquireMutex(projectPath);

            try
            {
                await FeatureDatabase.RemoveModuleAsync(projectPath, moduleId);
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
