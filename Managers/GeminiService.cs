using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
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

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey) && IsValidApiKeyFormat(_apiKey);
        public bool ApiKeyDecryptionFailed { get; private set; }
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
                bool needsResave = false;
                if (doc.RootElement.TryGetProperty("encryptedApiKey", out var encKey))
                {
                    _apiKey = DecryptString(encKey.GetString());
                }
                else if (doc.RootElement.TryGetProperty("apiKey", out var key))
                {
                    // Migrate plaintext key to encrypted format
                    _apiKey = key.GetString();
                    needsResave = true;
                }
                if (doc.RootElement.TryGetProperty("model", out var model))
                    _selectedModel = model.GetString() ?? DefaultModel;
                if (needsResave && !string.IsNullOrEmpty(_apiKey))
                    SaveConfig();
            }
            catch (Exception ex) { AppLogger.Warn("GeminiService", "Failed to load config", ex); }
        }

        public void SaveApiKey(string apiKey)
        {
            if (!IsValidApiKeyFormat(apiKey))
                throw new ArgumentException(
                    "Invalid API key format. Google API keys should be 39 characters long and start with 'AIza'.");
            _apiKey = apiKey;
            ApiKeyDecryptionFailed = false;
            SaveConfig();
        }

        private void SaveConfig()
        {
            try
            {
                var config = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(_apiKey))
                    config["encryptedApiKey"] = EncryptString(_apiKey);
                config["model"] = _selectedModel;
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_configFile, json, "GeminiService");
            }
            catch (Exception ex) { AppLogger.Warn("GeminiService", "Failed to save config", ex); }
        }

        public string GetMaskedApiKey()
        {
            if (string.IsNullOrEmpty(_apiKey)) return "";
            if (_apiKey.Length <= 8) return new string('*', _apiKey.Length);
            return _apiKey[..4] + new string('*', _apiKey.Length - 8) + _apiKey[^4..];
        }

        /// <summary>
        /// Validates that an API key matches the expected Google API key format.
        /// Google API keys start with "AIza" and are 39 characters long.
        /// </summary>
        public static bool IsValidApiKeyFormat(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            return key.Length == 39 && key.StartsWith("AIza", StringComparison.Ordinal);
        }

        private static string EncryptString(string plainText)
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private string? DecryptString(string? cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64)) return null;
            try
            {
                var encrypted = Convert.FromBase64String(cipherBase64);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException ex)
            {
                AppLogger.Warn("GeminiService",
                    "Failed to decrypt API key (DPAPI scope mismatch or corrupted data). " +
                    "Please re-enter your API key in Settings > Gemini.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
            catch (FormatException ex)
            {
                AppLogger.Warn("GeminiService",
                    "Stored API key has invalid Base64 encoding. " +
                    "Please re-enter your API key in Settings > Gemini.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
        }

        public async Task<GeminiImageResult> GenerateImageAsync(string prompt, string taskId,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return GeminiImageResult.Failure(ApiKeyDecryptionFailed
                    ? "Gemini API key could not be decrypted. Please re-enter it in Settings > Gemini."
                    : "Gemini API key not configured. Set it in Settings > Gemini.");
            if (!IsValidApiKeyFormat(_apiKey))
                return GeminiImageResult.Failure(
                    "Gemini API key has an invalid format (expected 39-char key starting with 'AIza'). " +
                    "Please re-enter it in Settings > Gemini.");

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
                return await ParseImageResponse(responseBody, taskId, progress);
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

        private async Task<GeminiImageResult> ParseImageResponse(string responseBody, string taskId,
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
                        await Task.Run(() => File.WriteAllBytes(filePath, imageBytes));
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
            catch (Exception ex) { AppLogger.Debug("GeminiService", $"Failed to parse error response JSON: {ex.Message}"); }
            return responseBody.Length > 200 ? responseBody[..200] : responseBody;
        }

        public string GetImageDirectory() => _imageDir;

        /// <summary>
        /// Sends a text chat message to Gemini with conversation history (non-streaming).
        /// </summary>
        public async Task<string> SendChatMessageAsync(
            List<ChatMessage> history,
            string userMessage,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return ApiKeyDecryptionFailed
                    ? "[Error] Gemini API key could not be decrypted. Please re-enter it in Settings > Gemini."
                    : "[Error] Gemini API key not configured. Set it in Settings > Gemini.";
            if (!IsValidApiKeyFormat(_apiKey))
                return "[Error] Gemini API key has an invalid format. Please re-enter it in Settings > Gemini.";

            try
            {
                var jsonContent = JsonSerializer.Serialize(BuildChatRequestBody(history, userMessage, systemInstruction));
                var url = $"{ApiBaseUrl}/{_selectedModel}:generateContent?key={_apiKey}";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = TryExtractErrorMessage(responseBody);
                    return $"[Error] Gemini API ({(int)response.StatusCode}): {errorMsg}";
                }

                return ExtractTextFromResponse(responseBody) ?? "[Error] No response from Gemini.";
            }
            catch (TaskCanceledException)
            {
                return "[Cancelled]";
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }
        }

        /// <summary>
        /// Streams a chat response from Gemini, calling onTextChunk for each piece of text as it arrives.
        /// Returns the full accumulated response text, or an error string starting with "[Error]" or "[Cancelled]".
        /// </summary>
        public async Task<string> SendChatMessageStreamingAsync(
            List<ChatMessage> history,
            string userMessage,
            Action<string> onTextChunk,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return ApiKeyDecryptionFailed
                    ? "[Error] Gemini API key could not be decrypted. Please re-enter it in Settings > Gemini."
                    : "[Error] Gemini API key not configured. Set it in Settings > Gemini.";
            if (!IsValidApiKeyFormat(_apiKey))
                return "[Error] Gemini API key has an invalid format. Please re-enter it in Settings > Gemini.";

            try
            {
                var jsonContent = JsonSerializer.Serialize(BuildChatRequestBody(history, userMessage, systemInstruction));
                var url = $"{ApiBaseUrl}/{_selectedModel}:streamGenerateContent?alt=sse&key={_apiKey}";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var errorMsg = TryExtractErrorMessage(errorBody);
                    return $"[Error] Gemini API ({(int)response.StatusCode}): {errorMsg}";
                }

                var fullText = new StringBuilder();
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;

                    if (!line.StartsWith("data: ")) continue;
                    var json = line.Substring(6);
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var chunk = ExtractTextFromResponse(json);
                    if (chunk != null)
                    {
                        fullText.Append(chunk);
                        onTextChunk(chunk);
                    }
                }

                return fullText.Length > 0 ? fullText.ToString() : "[Error] No response from Gemini.";
            }
            catch (TaskCanceledException)
            {
                return "[Cancelled]";
            }
            catch (Exception ex)
            {
                return $"[Error] {ex.Message}";
            }
        }

        private object BuildChatRequestBody(List<ChatMessage> history, string userMessage, string? systemInstruction)
        {
            var contents = new List<object>();
            foreach (var msg in history)
            {
                contents.Add(new
                {
                    role = msg.Role,
                    parts = new[] { new { text = msg.Text } }
                });
            }
            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = userMessage } }
            });

            if (!string.IsNullOrEmpty(systemInstruction))
            {
                return new
                {
                    system_instruction = new
                    {
                        parts = new[] { new { text = systemInstruction } }
                    },
                    contents
                };
            }
            return new { contents };
        }

        private static string? ExtractTextFromResponse(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var textProp))
                                return textProp.GetString() ?? "";
                        }
                    }
                }
            }
            catch { /* malformed chunk, skip */ }
            return null;
        }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = "";
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
