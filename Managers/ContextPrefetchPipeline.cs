using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Speculatively resolves feature context while the user is still typing,
    /// using an 800ms debounce. Caches results keyed by description hash so
    /// <see cref="TaskExecutionManager"/> can skip the resolution round-trip
    /// when a fresh result (under 30 seconds old) is available.
    /// </summary>
    public class ContextPrefetchPipeline
    {
        private readonly FeatureContextResolver _featureContextResolver;
        private readonly FeatureRegistryManager _featureRegistryManager;
        private readonly HybridSearchManager? _hybridSearchManager;
        private readonly Func<string> _getProjectPath;

        private readonly ConcurrentDictionary<string, CachedContextResult> _cache = new();
        private CancellationTokenSource? _debounceCts;
        private readonly object _debounceLock = new();

        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan CacheFreshnessLimit = TimeSpan.FromSeconds(30);

        public ContextPrefetchPipeline(
            FeatureContextResolver featureContextResolver,
            FeatureRegistryManager featureRegistryManager,
            HybridSearchManager? hybridSearchManager,
            Func<string> getProjectPath)
        {
            _featureContextResolver = featureContextResolver;
            _featureRegistryManager = featureRegistryManager;
            _hybridSearchManager = hybridSearchManager;
            _getProjectPath = getProjectPath;
        }

        /// <summary>
        /// Called on every text change from the UI. Debounces and then
        /// fires a speculative resolve in the background.
        /// </summary>
        public void OnDescriptionChanged(string description)
        {
            var text = description?.Trim() ?? "";
            if (text.Length < 15) return; // too short to resolve meaningfully

            lock (_debounceLock)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
            }

            var cts = _debounceCts;
            _ = DebounceAndResolveAsync(text, cts.Token);
        }

        /// <summary>
        /// Tries to retrieve a cached result for the given description.
        /// Returns null if no fresh cache entry exists.
        /// </summary>
        public FeatureContextResult? TryGetCached(string description)
        {
            var hash = ComputeHash(description?.Trim() ?? "");
            if (_cache.TryGetValue(hash, out var cached) &&
                DateTime.UtcNow - cached.ResolvedAt < CacheFreshnessLimit)
            {
                AppLogger.Debug("ContextPrefetch", "Cache hit — skipping feature resolution");
                return cached.Result;
            }
            return null;
        }

        /// <summary>Clears all cached results.</summary>
        public void ClearCache() => _cache.Clear();

        private async Task DebounceAndResolveAsync(string description, CancellationToken ct)
        {
            try
            {
                await Task.Delay(DebounceDelay, ct);
            }
            catch (OperationCanceledException) { return; }

            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath) ||
                !_featureRegistryManager.RegistryExists(projectPath))
                return;

            var hash = ComputeHash(description);

            // Already cached and fresh — skip
            if (_cache.TryGetValue(hash, out var existing) &&
                DateTime.UtcNow - existing.ResolvedAt < CacheFreshnessLimit)
                return;

            try
            {
                FeatureContextResult? result;
                if (_hybridSearchManager != null && _hybridSearchManager.IsAvailable)
                {
                    var request = new HybridSearchRequest { Query = description };
                    result = await _hybridSearchManager.SearchAsync(request, projectPath, ct);
                }
                else
                {
                    result = await _featureContextResolver.ResolveAsync(projectPath, description, ct: ct);
                }

                if (result != null)
                {
                    _cache[hash] = new CachedContextResult(result, DateTime.UtcNow);
                    AppLogger.Debug("ContextPrefetch",
                        $"Pre-fetched context: {result.RelevantFeatures.Count} features for hash {hash[..8]}");
                }
            }
            catch (OperationCanceledException) { /* debounce superseded */ }
            catch (Exception ex)
            {
                AppLogger.Debug("ContextPrefetch", $"Speculative resolve failed: {ex.Message}");
            }
        }

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private sealed record CachedContextResult(FeatureContextResult Result, DateTime ResolvedAt);
    }
}
