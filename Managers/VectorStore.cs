using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spritely.Constants;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// SQLite-backed local vector store for the Hybrid Semantic Index.
    /// Supports upsert, k-NN search with optional binary pre-filtering,
    /// and category/feature filtering. Zero external infrastructure required.
    /// </summary>
    public class VectorStore : IDisposable
    {
        private SqliteConnection? _connection;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        /// <summary>Whether the store has been initialized and is ready for queries.</summary>
        public bool IsInitialized => _connection != null;

        /// <summary>
        /// Initialize the vector store for a project. Creates the SQLite database
        /// and schema if they don't exist.
        /// </summary>
        public async Task InitializeAsync(string projectPath)
        {
            var dbPath = GetDbPath(projectPath);
            var dir = Path.GetDirectoryName(dbPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Ensure .gitignore excludes the vector DB
            EnsureGitIgnore(dir);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            await _connection.OpenAsync();

            await CreateSchemaAsync();
        }

        /// <summary>
        /// Insert or update a single vector. Updates existing entry if the ID matches.
        /// </summary>
        public async Task UpsertAsync(EmbeddingVector vector)
        {
            EnsureInitialized();
            await _writeLock.WaitAsync();
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO vectors
                        (id, category, feature_id, file_path, chunk_index, line_start, line_end,
                         content_preview, embedding, binary_embedding, content_hash, model_id, created_at, updated_at)
                    VALUES
                        ($id, $category, $featureId, $filePath, $chunkIndex, $lineStart, $lineEnd,
                         $preview, $embedding, $binaryEmbedding, $hash, $modelId, $createdAt, $updatedAt)";

                BindVectorParams(cmd, vector);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>
        /// Batch upsert multiple vectors in a single transaction.
        /// </summary>
        public async Task UpsertBatchAsync(List<EmbeddingVector> vectors)
        {
            if (vectors.Count == 0) return;
            EnsureInitialized();

            await _writeLock.WaitAsync();
            try
            {
                using var transaction = _connection!.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO vectors
                        (id, category, feature_id, file_path, chunk_index, line_start, line_end,
                         content_preview, embedding, binary_embedding, content_hash, model_id, created_at, updated_at)
                    VALUES
                        ($id, $category, $featureId, $filePath, $chunkIndex, $lineStart, $lineEnd,
                         $preview, $embedding, $binaryEmbedding, $hash, $modelId, $createdAt, $updatedAt)";

                foreach (var vector in vectors)
                {
                    cmd.Parameters.Clear();
                    BindVectorParams(cmd, vector);
                    await cmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>
        /// Brute-force k-NN search using cosine similarity. For typical project sizes
        /// (under 10K vectors), this completes in under 10ms.
        /// </summary>
        public async Task<List<SemanticSearchResult>> SearchAsync(
            float[] queryEmbedding, int topK = 10, string[]? categories = null, float minScore = 0.25f)
        {
            EnsureInitialized();

            var results = new List<(string id, string category, string? featureId, string? filePath, string preview, float score)>();

            using var cmd = _connection!.CreateCommand();
            var whereClauses = new List<string>();
            if (categories != null && categories.Length > 0)
            {
                var placeholders = string.Join(",", categories.Select((_, i) => $"$cat{i}"));
                whereClauses.Add($"category IN ({placeholders})");
                for (int i = 0; i < categories.Length; i++)
                    cmd.Parameters.AddWithValue($"$cat{i}", categories[i]);
            }

            var where = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";
            cmd.CommandText = $"SELECT id, category, feature_id, file_path, content_preview, embedding FROM vectors {where}";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var embeddingBlob = (byte[])reader["embedding"];
                var storedEmbedding = BytesToFloats(embeddingBlob);
                var score = EmbeddingService.CosineSimilarity(queryEmbedding, storedEmbedding);

                if (score >= minScore)
                {
                    results.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4),
                        score
                    ));
                }
            }

            return results
                .OrderByDescending(r => r.score)
                .Take(topK)
                .Select(r => new SemanticSearchResult
                {
                    VectorId = r.id,
                    Category = r.category,
                    FeatureId = r.featureId,
                    FilePath = r.filePath,
                    ContentPreview = r.preview,
                    VectorScore = r.score
                })
                .ToList();
        }

        /// <summary>
        /// Two-phase search: binary pre-filter with hamming distance, then full cosine rerank.
        /// Faster than brute-force for large vector counts (50K+).
        /// </summary>
        public async Task<List<SemanticSearchResult>> SearchWithPreFilterAsync(
            float[] queryEmbedding, byte[] binaryQuery,
            int preFilterK = 100, int topK = 10, string[]? categories = null, float minScore = 0.25f)
        {
            EnsureInitialized();

            // Phase 1: binary pre-filter
            var candidates = new List<(string id, int distance, byte[] fullEmbedding)>();

            using var preCmd = _connection!.CreateCommand();
            var whereClauses = new List<string> { "binary_embedding IS NOT NULL" };
            if (categories != null && categories.Length > 0)
            {
                var placeholders = string.Join(",", categories.Select((_, i) => $"$cat{i}"));
                whereClauses.Add($"category IN ({placeholders})");
                for (int i = 0; i < categories.Length; i++)
                    preCmd.Parameters.AddWithValue($"$cat{i}", categories[i]);
            }

            preCmd.CommandText = $@"
                SELECT id, binary_embedding, embedding
                FROM vectors
                WHERE {string.Join(" AND ", whereClauses)}";

            using var reader = await preCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var binEmb = (byte[])reader["binary_embedding"];
                var dist = EmbeddingService.HammingDistance(binaryQuery, binEmb);
                candidates.Add((reader.GetString(0), dist, (byte[])reader["embedding"]));
            }

            // Take top preFilterK by hamming distance
            var filtered = candidates.OrderBy(c => c.distance).Take(preFilterK).ToList();

            // Phase 2: full cosine rerank
            var results = new List<SemanticSearchResult>();
            var filteredIds = new HashSet<string>(filtered.Select(f => f.id));

            // Need metadata for filtered IDs
            if (filtered.Count == 0) return results;

            var idPlaceholders = string.Join(",", filtered.Select((_, i) => $"$id{i}"));
            using var metaCmd = _connection.CreateCommand();
            metaCmd.CommandText = $"SELECT id, category, feature_id, file_path, content_preview FROM vectors WHERE id IN ({idPlaceholders})";
            for (int i = 0; i < filtered.Count; i++)
                metaCmd.Parameters.AddWithValue($"$id{i}", filtered[i].id);

            var metadata = new Dictionary<string, (string category, string? featureId, string? filePath, string preview)>();
            using var metaReader = await metaCmd.ExecuteReaderAsync();
            while (await metaReader.ReadAsync())
            {
                metadata[metaReader.GetString(0)] = (
                    metaReader.GetString(1),
                    metaReader.IsDBNull(2) ? null : metaReader.GetString(2),
                    metaReader.IsDBNull(3) ? null : metaReader.GetString(3),
                    metaReader.GetString(4)
                );
            }

            foreach (var candidate in filtered)
            {
                var embedding = BytesToFloats(candidate.fullEmbedding);
                var score = EmbeddingService.CosineSimilarity(queryEmbedding, embedding);
                if (score >= minScore && metadata.TryGetValue(candidate.id, out var meta))
                {
                    results.Add(new SemanticSearchResult
                    {
                        VectorId = candidate.id,
                        Category = meta.category,
                        FeatureId = meta.featureId,
                        FilePath = meta.filePath,
                        ContentPreview = meta.preview,
                        VectorScore = score
                    });
                }
            }

            return results.OrderByDescending(r => r.VectorScore).Take(topK).ToList();
        }

        /// <summary>Delete all vectors associated with a specific feature.</summary>
        public async Task DeleteByFeatureAsync(string featureId)
        {
            EnsureInitialized();
            await _writeLock.WaitAsync();
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM vectors WHERE feature_id = $featureId";
                cmd.Parameters.AddWithValue("$featureId", featureId);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>Delete all vectors associated with a specific file path.</summary>
        public async Task DeleteByFileAsync(string filePath)
        {
            EnsureInitialized();
            await _writeLock.WaitAsync();
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM vectors WHERE file_path = $filePath";
                cmd.Parameters.AddWithValue("$filePath", filePath);
                await cmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>Check if a stored vector is stale by comparing content hashes.</summary>
        public async Task<bool> IsStaleAsync(string id, string currentHash)
        {
            EnsureInitialized();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT content_hash FROM vectors WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value) return true;
            return (string)result != currentHash;
        }

        /// <summary>Remove vectors whose feature IDs or file paths no longer exist.</summary>
        public async Task PruneOrphansAsync(HashSet<string> validFeatureIds, HashSet<string> validFilePaths)
        {
            EnsureInitialized();
            await _writeLock.WaitAsync();
            try
            {
                // Collect IDs to delete
                var toDelete = new List<string>();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT id, feature_id, file_path, category FROM vectors";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var category = reader.GetString(3);
                    if (category == EmbeddingConstants.CategoryFeatureDesc || category == EmbeddingConstants.CategoryFeatureSigs)
                    {
                        var featureId = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (featureId != null && !validFeatureIds.Contains(featureId))
                            toDelete.Add(reader.GetString(0));
                    }
                    else if (category == EmbeddingConstants.CategoryFileChunk)
                    {
                        var filePath = reader.IsDBNull(2) ? null : reader.GetString(2);
                        if (filePath != null && !validFilePaths.Contains(filePath))
                            toDelete.Add(reader.GetString(0));
                    }
                }

                if (toDelete.Count > 0)
                {
                    using var delCmd = _connection.CreateCommand();
                    using var transaction = _connection.BeginTransaction();
                    delCmd.Transaction = transaction;

                    foreach (var batch in toDelete.Chunk(100))
                    {
                        var placeholders = string.Join(",", batch.Select((_, i) => $"$d{i}"));
                        delCmd.CommandText = $"DELETE FROM vectors WHERE id IN ({placeholders})";
                        delCmd.Parameters.Clear();
                        for (int i = 0; i < batch.Length; i++)
                            delCmd.Parameters.AddWithValue($"$d{i}", batch[i]);
                        await delCmd.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                    AppLogger.Info("VectorStore", $"Pruned {toDelete.Count} orphaned vectors");
                }
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>Get total vector count, optionally filtered by category.</summary>
        public async Task<int> GetVectorCountAsync(string? category = null)
        {
            EnsureInitialized();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = category != null
                ? "SELECT COUNT(*) FROM vectors WHERE category = $cat"
                : "SELECT COUNT(*) FROM vectors";
            if (category != null)
                cmd.Parameters.AddWithValue("$cat", category);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Store a task completion embedding for historical learning.
        /// </summary>
        public async Task UpsertTaskEmbeddingAsync(
            string taskId, float[] descriptionEmbedding, float[]? summaryEmbedding,
            List<string> matchedFeatureIds, string outcome)
        {
            EnsureInitialized();
            await _writeLock.WaitAsync();
            try
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO task_embeddings
                        (task_id, description_embedding, summary_embedding, matched_feature_ids, outcome, created_at)
                    VALUES
                        ($taskId, $descEmb, $sumEmb, $featureIds, $outcome, $createdAt)";

                cmd.Parameters.AddWithValue("$taskId", taskId);
                cmd.Parameters.AddWithValue("$descEmb", FloatsToBytes(descriptionEmbedding));
                cmd.Parameters.AddWithValue("$sumEmb", summaryEmbedding != null ? (object)FloatsToBytes(summaryEmbedding) : DBNull.Value);
                cmd.Parameters.AddWithValue("$featureIds", System.Text.Json.JsonSerializer.Serialize(matchedFeatureIds));
                cmd.Parameters.AddWithValue("$outcome", outcome);
                cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();

                // Prune old task embeddings beyond the limit
                using var pruneCmd = _connection.CreateCommand();
                pruneCmd.CommandText = @"
                    DELETE FROM task_embeddings WHERE task_id IN (
                        SELECT task_id FROM task_embeddings
                        ORDER BY created_at DESC
                        LIMIT -1 OFFSET $limit
                    )";
                pruneCmd.Parameters.AddWithValue("$limit", EmbeddingConstants.MaxTaskHistoryEmbeddings);
                await pruneCmd.ExecuteNonQueryAsync();
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>
        /// Get per-feature task history embeddings for historical affinity scoring.
        /// Returns a dictionary mapping each feature ID to the description embeddings
        /// of past successful tasks that matched that specific feature.
        /// </summary>
        public async Task<Dictionary<string, List<float[]>>> GetPerFeatureHistoryAsync(
            IEnumerable<string> featureIds, int maxTasksPerFeature = 10)
        {
            EnsureInitialized();
            var results = new Dictionary<string, List<float[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in featureIds)
                results[id] = new List<float[]>();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT description_embedding, matched_feature_ids
                FROM task_embeddings
                WHERE outcome = 'success'
                ORDER BY created_at DESC
                LIMIT 200";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var featureIdsJson = reader.GetString(1);
                var taskFeatureIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(featureIdsJson);
                if (taskFeatureIds == null) continue;

                var embBlob = (byte[])reader["description_embedding"];
                float[]? embedding = null; // lazy deserialize

                foreach (var taskFeatId in taskFeatureIds)
                {
                    if (results.TryGetValue(taskFeatId, out var list) && list.Count < maxTasksPerFeature)
                    {
                        embedding ??= BytesToFloats(embBlob);
                        list.Add(embedding);
                    }
                }
            }

            return results;
        }

        // ── Schema ──────────────────────────────────────────────────────

        private async Task CreateSchemaAsync()
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS vectors (
                    id TEXT PRIMARY KEY,
                    category TEXT NOT NULL,
                    feature_id TEXT,
                    file_path TEXT,
                    chunk_index INTEGER DEFAULT 0,
                    line_start INTEGER,
                    line_end INTEGER,
                    content_preview TEXT NOT NULL DEFAULT '',
                    embedding BLOB NOT NULL,
                    binary_embedding BLOB,
                    content_hash TEXT NOT NULL DEFAULT '',
                    model_id TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_vectors_category ON vectors(category);
                CREATE INDEX IF NOT EXISTS idx_vectors_feature ON vectors(feature_id);
                CREATE INDEX IF NOT EXISTS idx_vectors_file ON vectors(file_path);
                CREATE INDEX IF NOT EXISTS idx_vectors_hash ON vectors(content_hash);

                CREATE TABLE IF NOT EXISTS task_embeddings (
                    task_id TEXT PRIMARY KEY,
                    description_embedding BLOB NOT NULL,
                    summary_embedding BLOB,
                    matched_feature_ids TEXT NOT NULL DEFAULT '[]',
                    outcome TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_task_feature ON task_embeddings(matched_feature_ids);

                CREATE TABLE IF NOT EXISTS metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                INSERT OR IGNORE INTO metadata (key, value) VALUES ('schema_version', $version);
            ";
            cmd.Parameters.AddWithValue("$version", EmbeddingConstants.SchemaVersion.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static string GetDbPath(string projectPath) =>
            Path.Combine(projectPath, FeatureConstants.SpritelyDir,
                EmbeddingConstants.EmbeddingsDir, EmbeddingConstants.VectorDbFileName);

        private static void EnsureGitIgnore(string embeddingsDir)
        {
            var gitignorePath = Path.Combine(embeddingsDir, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                try { File.WriteAllText(gitignorePath, "# Vector database is machine-local — rebuilt from source\n*\n!.gitignore\n"); }
                catch { /* non-critical */ }
            }
        }

        private void EnsureInitialized()
        {
            if (_connection == null)
                throw new InvalidOperationException("VectorStore not initialized. Call InitializeAsync first.");
        }

        private static void BindVectorParams(SqliteCommand cmd, EmbeddingVector v)
        {
            cmd.Parameters.AddWithValue("$id", v.Id);
            cmd.Parameters.AddWithValue("$category", v.Category);
            cmd.Parameters.AddWithValue("$featureId", (object?)v.FeatureId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", (object?)v.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$chunkIndex", v.ChunkIndex);
            cmd.Parameters.AddWithValue("$lineStart", (object?)v.LineStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lineEnd", (object?)v.LineEnd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$preview", v.ContentPreview);
            cmd.Parameters.AddWithValue("$embedding", FloatsToBytes(v.Embedding));
            cmd.Parameters.AddWithValue("$binaryEmbedding", (object?)v.BinaryEmbedding ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", v.ContentHash);
            cmd.Parameters.AddWithValue("$modelId", v.ModelId);
            cmd.Parameters.AddWithValue("$createdAt", v.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$updatedAt", v.UpdatedAt.ToString("o"));
        }

        internal static byte[] FloatsToBytes(float[] floats)
        {
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        internal static float[] BytesToFloats(byte[] bytes)
        {
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _writeLock.Dispose();
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}
