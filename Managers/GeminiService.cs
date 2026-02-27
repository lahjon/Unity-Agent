using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnityAgent.Managers
{
    public class GeminiService
    {
        private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
        private const string DefaultModel = "gemini-2.0-flash-exp";

        public static readonly string[] AvailableModels = new[]
        {
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.0-flash-exp",
            "gemini-2.0-flash",
            "gemini-1.5-pro",
            "gemini-1.5-flash",
        };

        private readonly string _configFile;
        private readonly string _imageDir;
        private readonly HttpClient _httpClient;
        private string? _apiKey;
        private string _selectedModel = DefaultModel;

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                SaveConfig();
            }
        }

        public GeminiService(string appDataDir)
        {
            _configFile = Path.Combine(appDataDir, "gemini_config.json");
            _imageDir = Path.Combine(appDataDir, "gemini_images");
            Directory.CreateDirectory(_imageDir);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configFile)) return;
                var json = File.ReadAllText(_configFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("apiKey", out var key))
                    _apiKey = key.GetString();
                if (doc.RootElement.TryGetProperty("model", out var model))
                    _selectedModel = model.GetString() ?? DefaultModel;
            }
            catch { }
        }

        public void SaveApiKey(string apiKey)
        {
            _apiKey = apiKey;
            SaveConfig();
        }

        private void SaveConfig()
        {
            try
            {
                var config = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(_apiKey))
                    config["apiKey"] = _apiKey;
                config["model"] = _selectedModel;
                File.WriteAllText(_configFile,
                    JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public string GetMaskedApiKey()
        {
            if (string.IsNullOrEmpty(_apiKey)) return "";
            if (_apiKey.Length <= 8) return new string('*', _apiKey.Length);
            return _apiKey[..4] + new string('*', _apiKey.Length - 8) + _apiKey[^4..];
        }

        public async Task<GeminiImageResult> GenerateImageAsync(string prompt, string taskId,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return GeminiImageResult.Failure("Gemini API key not configured. Set it in Settings > Gemini.");

            progress?.Report("[Gemini] Sending image generation request...\n");

            try
            {
                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(requestBody);
                var url = $"{ApiBaseUrl}/{_selectedModel}:generateContent?key={_apiKey}";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                progress?.Report("[Gemini] Waiting for response...\n");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = TryExtractErrorMessage(responseBody);
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return GeminiImageResult.Failure(
                            $"[Gemini] Authentication failed ({(int)response.StatusCode}): {errorMsg}\n" +
                            "Check your API key in Settings > Gemini.");
                    }
                    return GeminiImageResult.Failure(
                        $"[Gemini] API error ({(int)response.StatusCode}): {errorMsg}");
                }

                progress?.Report("[Gemini] Processing response...\n");
                return ParseImageResponse(responseBody, taskId, progress);
            }
            catch (TaskCanceledException)
            {
                return GeminiImageResult.Failure("[Gemini] Request cancelled.");
            }
            catch (HttpRequestException ex)
            {
                return GeminiImageResult.Failure($"[Gemini] Network error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return GeminiImageResult.Failure($"[Gemini] Unexpected error: {ex.Message}");
            }
        }

        private GeminiImageResult ParseImageResponse(string responseBody, string taskId,
            IProgress<string>? progress)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (!root.TryGetProperty("candidates", out var candidates) ||
                    candidates.GetArrayLength() == 0)
                    return GeminiImageResult.Failure("[Gemini] No candidates in response.");

                var content = candidates[0].GetProperty("content");
                var parts = content.GetProperty("parts");

                var imagePaths = new List<string>();
                string? textResponse = null;

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textProp))
                    {
                        textResponse = textProp.GetString();
                    }
                    else if (part.TryGetProperty("inlineData", out var inlineData))
                    {
                        var mimeType = inlineData.GetProperty("mimeType").GetString() ?? "image/png";
                        var base64Data = inlineData.GetProperty("data").GetString();
                        if (string.IsNullOrEmpty(base64Data)) continue;

                        var ext = mimeType switch
                        {
                            "image/jpeg" => ".jpg",
                            "image/webp" => ".webp",
                            "image/gif" => ".gif",
                            _ => ".png"
                        };

                        var fileName = $"{taskId}_{DateTime.Now:yyyyMMdd_HHmmss}_{imagePaths.Count}{ext}";
                        var filePath = Path.Combine(_imageDir, fileName);

                        var imageBytes = Convert.FromBase64String(base64Data);
                        File.WriteAllBytes(filePath, imageBytes);
                        imagePaths.Add(filePath);

                        progress?.Report($"[Gemini] Image saved: {fileName}\n");
                    }
                }

                if (imagePaths.Count == 0)
                    return GeminiImageResult.Failure(
                        $"[Gemini] No images in response.{(textResponse != null ? $"\nModel said: {textResponse}" : "")}");

                return new GeminiImageResult
                {
                    Success = true,
                    ImagePaths = imagePaths,
                    TextResponse = textResponse,
                    ErrorMessage = null
                };
            }
            catch (Exception ex)
            {
                return GeminiImageResult.Failure($"[Gemini] Failed to parse response: {ex.Message}");
            }
        }

        private static string TryExtractErrorMessage(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("message", out var msg))
                        return msg.GetString() ?? "Unknown error";
                    return error.ToString();
                }
            }
            catch { }
            return responseBody.Length > 200 ? responseBody[..200] : responseBody;
        }

        public string GetImageDirectory() => _imageDir;
    }

    public class GeminiImageResult
    {
        public bool Success { get; set; }
        public List<string> ImagePaths { get; set; } = new();
        public string? TextResponse { get; set; }
        public string? ErrorMessage { get; set; }

        public static GeminiImageResult Failure(string message) =>
            new() { Success = false, ErrorMessage = message };
    }
}
