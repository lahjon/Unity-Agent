using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// Generates dense embeddings via the Voyage AI API. Supports code, text, and
    /// multimodal (image) embeddings. Falls back gracefully when the API key is
    /// unavailable or calls fail.
    /// </summary>
    public class EmbeddingService : IDisposable
    {
        private const string VoyageApiBaseUrl = "https://api.voyageai.com/v1/embeddings";
        private const string ApiKeyFileName = "voyage_api_key.txt";

        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private bool _disposed;

        public EmbeddingService(string? apiKeyOverride = null)
        {
            _httpClient = new HttpClient { Timeout = EmbeddingConstants.ApiTimeout };
            _apiKey = apiKeyOverride ?? LoadApiKey();

            if (!string.IsNullOrWhiteSpace(_apiKey))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        /// <summary>Whether the service has a configured API key and can make embedding calls.</summary>
        public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>
        /// Embed a single text string using the specified model.
        /// Returns null if the service is unavailable or the call fails.
        /// </summary>
        public async Task<float[]?> EmbedTextAsync(string text, string? modelOverride = null, CancellationToken ct = default)
        {
            var results = await EmbedBatchAsync(new[] { text }, modelOverride, ct);
            return results?.Length > 0 ? results[0] : null;
        }

        /// <summary>
        /// Embed a batch of texts. Returns an array of embedding vectors in the same order,
        /// or null if the service is unavailable or the call fails entirely.
        /// </summary>
        public async Task<float[][]?> EmbedBatchAsync(
            string[] texts, string? modelOverride = null, CancellationToken ct = default,
            bool isQuery = false)
        {
            if (!IsAvailable || texts.Length == 0)
                return null;

            var model = modelOverride ?? EmbeddingConstants.VoyageCodeModel;
            var inputType = isQuery ? "query" : "document";
            var allResults = new List<float[]>();

            // Split into batches respecting API limits
            for (int i = 0; i < texts.Length; i += EmbeddingConstants.MaxBatchSize)
            {
                var batch = texts.Skip(i).Take(EmbeddingConstants.MaxBatchSize).ToArray();
                var batchResult = await CallApiWithRetryAsync(batch, model, inputType, ct);
                if (batchResult == null)
                    return null;
                allResults.AddRange(batchResult);
            }

            return allResults.ToArray();
        }

        /// <summary>
        /// Embed code content using the code-specific model (document input type for indexing).
        /// </summary>
        public Task<float[]?> EmbedCodeAsync(string code, CancellationToken ct = default)
            => EmbedTextAsync(code, EmbeddingConstants.VoyageCodeModel, ct);

        /// <summary>
        /// Embed a query string using the code-specific model (query input type for search).
        /// Voyage AI optimizes query embeddings differently from document embeddings.
        /// </summary>
        public async Task<float[]?> EmbedQueryAsync(string query, string? modelOverride = null, CancellationToken ct = default)
        {
            var results = await EmbedBatchAsync(new[] { query }, modelOverride, ct, isQuery: true);
            return results?.Length > 0 ? results[0] : null;
        }

        /// <summary>
        /// Compute cosine similarity between two embedding vectors.
        /// Returns 0 if either vector is null or empty.
        /// </summary>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0)
                return 0f;

            // Use SIMD-accelerated dot product where available
            float dot = 0f, normA = 0f, normB = 0f;

            int simdLength = Vector<float>.Count;
            int i = 0;

            if (Vector.IsHardwareAccelerated && a.Length >= simdLength)
            {
                var dotVec = Vector<float>.Zero;
                var normAVec = Vector<float>.Zero;
                var normBVec = Vector<float>.Zero;

                for (; i <= a.Length - simdLength; i += simdLength)
                {
                    var va = new Vector<float>(a, i);
                    var vb = new Vector<float>(b, i);
                    dotVec += va * vb;
                    normAVec += va * va;
                    normBVec += vb * vb;
                }

                dot = Vector.Sum(dotVec);
                normA = Vector.Sum(normAVec);
                normB = Vector.Sum(normBVec);
            }

            // Scalar remainder
            for (; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
            return denom > 0 ? dot / denom : 0f;
        }

        /// <summary>
        /// Convert a float32 embedding to a binary vector (1 bit per dimension)
        /// for fast hamming-distance pre-filtering.
        /// </summary>
        public static byte[] ToBinaryVector(float[] embedding)
        {
            var bytes = new byte[EmbeddingConstants.BinaryEmbeddingByteSize];
            for (int i = 0; i < embedding.Length; i++)
            {
                if (embedding[i] > 0)
                    bytes[i / 8] |= (byte)(1 << (i % 8));
            }
            return bytes;
        }

        /// <summary>
        /// Compute hamming distance between two binary vectors.
        /// Lower distance = more similar.
        /// </summary>
        public static int HammingDistance(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return int.MaxValue;

            int distance = 0;
            for (int i = 0; i < a.Length; i++)
                distance += BitOperations.PopCount((uint)(a[i] ^ b[i]));
            return distance;
        }

        /// <summary>
        /// Rough token count estimate (~4 chars per token for English/code).
        /// </summary>
        public static int EstimateTokens(string text) => (text.Length + 3) / 4;

        private async Task<float[][]?> CallApiWithRetryAsync(string[] texts, string model, string inputType, CancellationToken ct)
        {
            for (int attempt = 0; attempt < EmbeddingConstants.MaxApiRetries; attempt++)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    return await CallApiAsync(texts, model, inputType, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException ex) when (attempt < EmbeddingConstants.MaxApiRetries - 1)
                {
                    AppLogger.Debug("EmbeddingService",
                        $"API call failed (attempt {attempt + 1}/{EmbeddingConstants.MaxApiRetries}): {ex.Message}");
                    var delay = EmbeddingConstants.RetryBaseDelay * Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("EmbeddingService", $"Embedding API call failed: {ex.Message}");
                    return null;
                }
            }
            return null;
        }

        private async Task<float[][]> CallApiAsync(string[] texts, string model, string inputType, CancellationToken ct)
        {
            var requestBody = new
            {
                model,
                input = texts,
                input_type = inputType
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(VoyageApiBaseUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            var data = doc.RootElement.GetProperty("data");
            var embeddings = new float[texts.Length][];

            foreach (var item in data.EnumerateArray())
            {
                var index = item.GetProperty("index").GetInt32();
                var embeddingArray = item.GetProperty("embedding");
                var values = new float[EmbeddingConstants.EmbeddingDimension];

                int j = 0;
                foreach (var val in embeddingArray.EnumerateArray())
                {
                    if (j < values.Length)
                        values[j++] = val.GetSingle();
                }
                embeddings[index] = values;
            }

            return embeddings;
        }

        private static string? LoadApiKey()
        {
            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Spritely");
                var keyPath = Path.Combine(appDataDir, ApiKeyFileName);

                if (!File.Exists(keyPath))
                {
                    // Also check environment variable
                    var envKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY");
                    if (!string.IsNullOrWhiteSpace(envKey))
                        return envKey.Trim();
                    return null;
                }

                var key = File.ReadAllText(keyPath).Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("EmbeddingService", $"Failed to load API key: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient.Dispose();
                _disposed = true;
            }
        }
    }
}
