using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// Persistent binary cache mapping content hashes to embedding vectors.
    /// Stored at %LOCALAPPDATA%\Spritely\embeddings\{projectHash}.bin to avoid
    /// redundant Voyage API calls across sessions.
    /// </summary>
    public class EmbeddingCache
    {
        private const int CacheVersion = 1;
        private readonly Dictionary<string, float[]> _entries = new(StringComparer.Ordinal);
        private string? _cachePath;
        private bool _dirty;

        public int Count => _entries.Count;

        /// <summary>
        /// Load the cache for a project from %LOCALAPPDATA%\Spritely\embeddings\{projectHash}.bin.
        /// </summary>
        public async Task LoadAsync(string projectPath)
        {
            _cachePath = GetCachePath(projectPath);
            var dir = Path.GetDirectoryName(_cachePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_cachePath))
                return;

            try
            {
                var bytes = await File.ReadAllBytesAsync(_cachePath);
                Deserialize(bytes);
                AppLogger.Info("EmbeddingCache", $"Loaded {_entries.Count} cached embeddings");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EmbeddingCache", $"Failed to load cache, starting fresh: {ex.Message}");
                _entries.Clear();
            }
        }

        public bool TryGet(string contentHash, out float[] embedding)
        {
            if (_entries.TryGetValue(contentHash, out var cached))
            {
                embedding = cached;
                return true;
            }
            embedding = Array.Empty<float>();
            return false;
        }

        public void Put(string contentHash, float[] embedding)
        {
            _entries[contentHash] = embedding;
            _dirty = true;
        }

        /// <summary>
        /// Persist the cache to disk if modified.
        /// </summary>
        public async Task SaveAsync()
        {
            if (!_dirty || _cachePath == null) return;

            try
            {
                var bytes = Serialize();
                await File.WriteAllBytesAsync(_cachePath, bytes);
                _dirty = false;
                AppLogger.Info("EmbeddingCache", $"Saved {_entries.Count} embeddings to cache");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EmbeddingCache", $"Failed to save cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove entries not in the valid set to prevent unbounded growth.
        /// </summary>
        public void Prune(HashSet<string> validHashes)
        {
            var toRemove = new List<string>();
            foreach (var key in _entries.Keys)
            {
                if (!validHashes.Contains(key))
                    toRemove.Add(key);
            }
            foreach (var key in toRemove)
                _entries.Remove(key);
            if (toRemove.Count > 0)
            {
                _dirty = true;
                AppLogger.Info("EmbeddingCache", $"Pruned {toRemove.Count} stale cache entries");
            }
        }

        /// <summary>
        /// Compute a stable hash of text content for use as a cache key.
        /// </summary>
        public static string HashText(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexStringLower(bytes)[..32];
        }

        // Binary format: [version:i32][count:i32] then per entry: [hashLen:i32][hashUtf8][dim:i32][floats]
        private byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(CacheVersion);
            bw.Write(_entries.Count);

            foreach (var (hash, embedding) in _entries)
            {
                var hashBytes = Encoding.UTF8.GetBytes(hash);
                bw.Write(hashBytes.Length);
                bw.Write(hashBytes);
                bw.Write(embedding.Length);
                foreach (var f in embedding)
                    bw.Write(f);
            }

            return ms.ToArray();
        }

        private void Deserialize(byte[] data)
        {
            _entries.Clear();
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            var version = br.ReadInt32();
            if (version != CacheVersion) return;

            var count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var hashLen = br.ReadInt32();
                var hashBytes = br.ReadBytes(hashLen);
                var hash = Encoding.UTF8.GetString(hashBytes);
                var dim = br.ReadInt32();
                var embedding = new float[dim];
                for (int j = 0; j < dim; j++)
                    embedding[j] = br.ReadSingle();
                _entries[hash] = embedding;
            }
        }

        private static string GetCachePath(string projectPath)
        {
            var projectHash = ComputeProjectHash(projectPath);
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spritely", EmbeddingConstants.EmbeddingsDir, $"{projectHash}.bin");
        }

        private static string ComputeProjectHash(string projectPath)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
                projectPath.ToLowerInvariant().TrimEnd('\\', '/')));
            return Convert.ToHexStringLower(bytes)[..16];
        }
    }
}
