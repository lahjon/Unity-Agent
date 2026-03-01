using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public class ModelConfigManager
    {
        private static readonly string[] DefaultClaudeModels =
        {
            "claude-sonnet-4-20250514",
            "claude-haiku-4-20250414",
            "claude-3-5-sonnet-20241022",
            "claude-3-5-haiku-20241022",
        };

        private static readonly string[] DefaultGeminiModels =
        {
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.0-flash-exp",
            "gemini-2.0-flash",
            "gemini-1.5-pro",
            "gemini-1.5-flash",
        };

        private readonly string _configFile;
        private readonly HttpClient _httpClient;

        private string[] _claudeModels;
        private string[] _geminiModels;

        public string[] ClaudeModels => _claudeModels;
        public string[] GeminiModels => _geminiModels;

        public ModelConfigManager(string appDataDir)
        {
            _configFile = Path.Combine(appDataDir, "models.json");
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _claudeModels = DefaultClaudeModels;
            _geminiModels = DefaultGeminiModels;
            LoadFromFile();
        }

        private void LoadFromFile()
        {
            try
            {
                if (!File.Exists(_configFile)) return;
                var json = File.ReadAllText(_configFile);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("claude", out var claudeArr) &&
                    claudeArr.ValueKind == JsonValueKind.Array)
                {
                    var models = claudeArr.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToArray();
                    if (models.Length > 0)
                        _claudeModels = models!;
                }

                if (doc.RootElement.TryGetProperty("gemini", out var geminiArr) &&
                    geminiArr.ValueKind == JsonValueKind.Array)
                {
                    var models = geminiArr.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToArray();
                    if (models.Length > 0)
                        _geminiModels = models!;
                }

                AppLogger.Info("ModelConfig", $"Loaded {_claudeModels.Length} Claude + {_geminiModels.Length} Gemini models from config");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ModelConfig", "Failed to load models.json, using defaults", ex);
            }
        }

        private void SaveToFile()
        {
            try
            {
                var config = new Dictionary<string, string[]>
                {
                    ["claude"] = _claudeModels,
                    ["gemini"] = _geminiModels,
                };
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_configFile, json, "ModelConfig");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ModelConfig", "Failed to save models.json", ex);
            }
        }

        public async Task<(int claudeCount, int geminiCount, string? error)> RefreshFromApisAsync(
            string? claudeApiKey, string? geminiApiKey, CancellationToken ct = default)
        {
            string? error = null;
            int claudeCount = 0;
            int geminiCount = 0;

            if (!string.IsNullOrEmpty(claudeApiKey))
            {
                try
                {
                    var models = await FetchClaudeModelsAsync(claudeApiKey, ct);
                    if (models.Length > 0)
                    {
                        _claudeModels = models;
                        claudeCount = models.Length;
                    }
                }
                catch (Exception ex)
                {
                    error = $"Claude: {ex.Message}";
                    AppLogger.Warn("ModelConfig", "Failed to fetch Claude models", ex);
                }
            }

            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                try
                {
                    var models = await FetchGeminiModelsAsync(geminiApiKey, ct);
                    if (models.Length > 0)
                    {
                        _geminiModels = models;
                        geminiCount = models.Length;
                    }
                }
                catch (Exception ex)
                {
                    var gemErr = $"Gemini: {ex.Message}";
                    error = error != null ? $"{error}; {gemErr}" : gemErr;
                    AppLogger.Warn("ModelConfig", "Failed to fetch Gemini models", ex);
                }
            }

            if (claudeCount > 0 || geminiCount > 0)
                SaveToFile();

            return (claudeCount, geminiCount, error);
        }

        private async Task<string[]> FetchClaudeModelsAsync(string apiKey, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                            models.Add(id);
                    }
                }
            }

            models.Sort();
            return models.ToArray();
        }

        private async Task<string[]> FetchGeminiModelsAsync(string apiKey, CancellationToken ct)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
            using var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArr) &&
                modelsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsArr.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            // API returns "models/gemini-2.5-flash" — strip the prefix
                            if (name.StartsWith("models/", StringComparison.Ordinal))
                                name = name.Substring(7);
                            // Only include generateContent-capable models
                            if (item.TryGetProperty("supportedGenerationMethods", out var methods) &&
                                methods.ValueKind == JsonValueKind.Array)
                            {
                                bool canGenerate = false;
                                foreach (var m in methods.EnumerateArray())
                                {
                                    var method = m.GetString();
                                    if (method == "generateContent" || method == "streamGenerateContent")
                                    {
                                        canGenerate = true;
                                        break;
                                    }
                                }
                                if (!canGenerate) continue;
                            }
                            models.Add(name);
                        }
                    }
                }
            }

            models.Sort();
            return models.ToArray();
        }
    }
}
