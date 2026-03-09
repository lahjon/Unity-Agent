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
    /// Maintains the full-project symbol index (<c>_codebase_index.json</c>) in
    /// <c>.spritely/features/</c>. Supports full rebuild, incremental updates,
    /// and symbol lookup with fuzzy matching.
    /// </summary>
    public class CodebaseIndexManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>
        /// Full rebuild: scans all source files, extracts symbols, maps to features, writes index.
        /// No file-count ceiling — processes every supported source file in the project.
        /// </summary>
        public async Task BuildIndexAsync(string projectPath, List<FeatureEntry> features, CancellationToken ct, IProgress<string>? progress = null)
        {
            progress?.Report("Scanning source files...");
            var relativeFiles = ScanAllSourceFiles(projectPath);
            ct.ThrowIfCancellationRequested();

            var featureLookup = BuildFeatureFileLookup(features);
            var index = new CodebaseSymbolIndex { Version = 1, Symbols = new Dictionary<string, SymbolIndexEntry>() };

            int processed = 0;
            int total = relativeFiles.Count;
            foreach (var relPath in relativeFiles)
            {
                ct.ThrowIfCancellationRequested();

                var absPath = Path.Combine(projectPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                var symbols = SignatureExtractor.ExtractStructuredSymbols(absPath);
                var featureId = ResolveFeatureForFile(relPath, featureLookup);

                foreach (var symbol in symbols)
                {
                    var key = symbol.Name.ToLowerInvariant();
                    index.Symbols[key] = new SymbolIndexEntry
                    {
                        FeatureId = featureId,
                        FilePath = relPath,
                        Kind = symbol.Kind.ToString(),
                        ShortSignature = symbol.Signature
                    };
                }

                processed++;
                if (processed % 50 == 0 || processed == total)
                    progress?.Report($"Indexing symbols: {processed}/{total} files...");
            }

            progress?.Report("Saving index...");
            await SaveIndexAsync(projectPath, index);
            progress?.Report($"Re-index complete: {index.Symbols.Count} symbols from {total} files");
        }

        /// <summary>
        /// Loads the codebase symbol index from disk. Returns null if missing or unreadable.
        /// </summary>
        public async Task<CodebaseSymbolIndex?> LoadIndexAsync(string projectPath)
        {
            var indexPath = GetIndexPath(projectPath);
            try
            {
                if (!File.Exists(indexPath))
                    return null;

                var json = await File.ReadAllTextAsync(indexPath);
                return JsonSerializer.Deserialize<CodebaseSymbolIndex>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CodebaseIndex", $"Failed to load index: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Persists the codebase symbol index to disk under mutex protection.
        /// </summary>
        public async Task SaveIndexAsync(string projectPath, CodebaseSymbolIndex index)
        {
            using var guard = AcquireMutex(projectPath);
            try
            {
                var featuresPath = GetFeaturesPath(projectPath);
                Directory.CreateDirectory(featuresPath);

                var json = JsonSerializer.Serialize(index, JsonOptions).Replace("\r\n", "\n");
                await File.WriteAllTextAsync(GetIndexPath(projectPath), json);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("CodebaseIndex", $"Failed to save index: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Case-insensitive symbol lookup with fuzzy matching.
        /// Returns matches sorted by confidence: exact (1.0), prefix (0.8), contains (0.5).
        /// </summary>
        public List<SymbolSearchResult> LookupSymbol(CodebaseSymbolIndex index, string symbolName)
        {
            if (index?.Symbols == null || string.IsNullOrWhiteSpace(symbolName))
                return [];

            var query = symbolName.ToLowerInvariant();
            var results = new List<SymbolSearchResult>();

            foreach (var (key, entry) in index.Symbols)
            {
                double confidence;

                if (key == query)
                    confidence = 1.0;
                else if (key.StartsWith(query, StringComparison.Ordinal))
                    confidence = 0.8;
                else if (key.Contains(query, StringComparison.Ordinal))
                    confidence = 0.5;
                else
                    continue;

                // Extract display name from signature (e.g. "class Foo" -> "Foo", "Bar(x) -> int" -> "Bar")
                var displayName = ExtractDisplayName(key, entry.ShortSignature);

                results.Add(new SymbolSearchResult
                {
                    SymbolName = displayName,
                    FeatureId = entry.FeatureId,
                    Confidence = confidence
                });
            }

            return results.OrderByDescending(r => r.Confidence).ToList();
        }

        /// <summary>
        /// Incremental update: re-extracts symbols only for the given files, updates their
        /// entries in the index, and saves. Does not rewrite unrelated entries.
        /// </summary>
        public async Task UpdateSymbolsForFeature(string projectPath, string featureId, List<string> filePaths)
        {
            var index = await LoadIndexAsync(projectPath) ?? new CodebaseSymbolIndex();

            // Remove existing entries for the given files
            var filesToUpdate = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
            var keysToRemove = index.Symbols
                .Where(kvp => filesToUpdate.Contains(kvp.Value.FilePath))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
                index.Symbols.Remove(key);

            // Re-extract and add
            foreach (var relPath in filePaths)
            {
                var absPath = Path.Combine(projectPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absPath))
                    continue;

                var symbols = SignatureExtractor.ExtractStructuredSymbols(absPath);
                foreach (var symbol in symbols)
                {
                    var key = symbol.Name.ToLowerInvariant();
                    index.Symbols[key] = new SymbolIndexEntry
                    {
                        FeatureId = featureId,
                        FilePath = relPath,
                        Kind = symbol.Kind.ToString(),
                        ShortSignature = symbol.Signature
                    };
                }
            }

            await SaveIndexAsync(projectPath, index);
        }

        /// <summary>
        /// Reverse lookup: returns all symbol entries belonging to a specific feature.
        /// </summary>
        public List<SymbolIndexEntry> GetSymbolsInFeature(CodebaseSymbolIndex index, string featureId)
        {
            if (index?.Symbols == null || string.IsNullOrWhiteSpace(featureId))
                return [];

            return index.Symbols.Values
                .Where(e => string.Equals(e.FeatureId, featureId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // ── File Scanning ───────────────────────────────────────────────

        /// <summary>
        /// Scans all source files under the project path with no file-count ceiling.
        /// Same logic as <c>FeatureInitializer.ScanSourceFiles</c> but without caps.
        /// </summary>
        private static List<string> ScanAllSourceFiles(string projectPath)
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

                    var relativePath = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
                    var segments = relativePath.Split('/');

                    var skip = false;
                    for (var i = 0; i < segments.Length - 1; i++)
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
                AppLogger.Debug("CodebaseIndex", $"Error scanning files: {ex.Message}", ex);
            }

            return results;
        }

        // ── Feature Mapping ─────────────────────────────────────────────

        /// <summary>
        /// Builds a lookup from relative file path to feature ID by combining
        /// PrimaryFiles and SecondaryFiles from all features. Primary takes precedence.
        /// </summary>
        private static Dictionary<string, string> BuildFeatureFileLookup(List<FeatureEntry> features)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Secondary first so primary overwrites
            foreach (var feature in features)
            {
                foreach (var file in feature.SecondaryFiles)
                    lookup[file] = feature.Id;
            }

            foreach (var feature in features)
            {
                foreach (var file in feature.PrimaryFiles)
                    lookup[file] = feature.Id;
            }

            return lookup;
        }

        private static string ResolveFeatureForFile(string relPath, Dictionary<string, string> lookup)
        {
            return lookup.TryGetValue(relPath, out var featureId) ? featureId : "";
        }

        /// <summary>
        /// Extracts a human-readable symbol name from the signature or falls back to the key.
        /// </summary>
        private static string ExtractDisplayName(string key, string shortSignature)
        {
            if (string.IsNullOrWhiteSpace(shortSignature))
                return key;

            // Signatures like "class Foo", "enum Bar" — take second word
            var spaceIdx = shortSignature.IndexOf(' ');
            if (spaceIdx >= 0)
            {
                var afterSpace = shortSignature[(spaceIdx + 1)..];
                var endIdx = afterSpace.IndexOfAny(['(', ':', ' ', '<']);
                return endIdx >= 0 ? afterSpace[..endIdx] : afterSpace;
            }

            // Signatures like "Foo(x) -> int" — take before paren
            var parenIdx = shortSignature.IndexOf('(');
            if (parenIdx > 0)
                return shortSignature[..parenIdx];

            return key;
        }

        // ── Paths ───────────────────────────────────────────────────────

        private static string GetFeaturesPath(string projectPath)
            => Path.Combine(projectPath, FeatureConstants.SpritelyDir, FeatureConstants.FeaturesDir);

        private static string GetIndexPath(string projectPath)
            => Path.Combine(GetFeaturesPath(projectPath), FeatureConstants.CodebaseIndexFileName);

        // ── Mutex (same pattern as FeatureRegistryManager) ──────────────

        private static string ComputePathHash(string projectPath)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        private static MutexGuard AcquireMutex(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            var mutexName = $"Global\\Spritely_CodebaseIndex_{hash}";
            var mutex = new Mutex(false, mutexName);

            try
            {
                if (!mutex.WaitOne(FeatureConstants.MutexTimeoutMs))
                {
                    mutex.Dispose();
                    throw new TimeoutException(
                        $"[CodebaseIndex] Timed out acquiring mutex for project: {projectPath}");
                }
            }
            catch (AbandonedMutexException)
            {
                AppLogger.Debug("CodebaseIndex", "Acquired abandoned mutex — previous holder likely crashed.");
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
