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
    public class ClaudeService
    {
        private const string ApiBaseUrl = "https://api.anthropic.com/v1/messages";
        private const string DefaultModel = "claude-sonnet-4-20250514";

        public static readonly string[] AvailableModels = new[]
        {
            "claude-sonnet-4-20250514",
            "claude-haiku-4-20250414",
            "claude-3-5-sonnet-20241022",
            "claude-3-5-haiku-20241022",
        };

        private readonly string _configFile;
        private readonly HttpClient _httpClient;
        private string? _apiKey;
        private string _selectedModel = DefaultModel;

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
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

        public ClaudeService(string appDataDir)
        {
            _configFile = Path.Combine(appDataDir, "claude_chat_config.json");
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
                if (doc.RootElement.TryGetProperty("encryptedApiKey", out var encKey))
                    _apiKey = DecryptString(encKey.GetString());
                if (doc.RootElement.TryGetProperty("model", out var model))
                    _selectedModel = model.GetString() ?? DefaultModel;
            }
            catch (Exception ex) { AppLogger.Warn("ClaudeService", "Failed to load config", ex); }
        }

        public void SaveApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key cannot be empty.");
            _apiKey = apiKey.Trim();
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
                SafeFileWriter.WriteInBackground(_configFile, json, "ClaudeService");
            }
            catch (Exception ex) { AppLogger.Warn("ClaudeService", "Failed to save config", ex); }
        }

        public string GetMaskedApiKey()
        {
            if (string.IsNullOrEmpty(_apiKey)) return "";
            if (_apiKey.Length <= 8) return new string('*', _apiKey.Length);
            return _apiKey[..4] + new string('*', _apiKey.Length - 8) + _apiKey[^4..];
        }

        public async Task<string> SendChatMessageStreamingAsync(
            List<ChatMessage> history,
            string userMessage,
            Action<string> onTextChunk,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_apiKey))
                return ApiKeyDecryptionFailed
                    ? "[Error] Claude API key could not be decrypted. Please re-enter it in Settings > Claude."
                    : "[Error] Claude API key not configured. Set it in Settings > Claude.";

            try
            {
                var messages = new List<object>();
                foreach (var msg in history)
                {
                    messages.Add(new
                    {
                        role = msg.Role == "model" ? "assistant" : "user",
                        content = msg.Text
                    });
                }
                messages.Add(new { role = "user", content = userMessage });

                var requestObj = new Dictionary<string, object>
                {
                    ["model"] = _selectedModel,
                    ["max_tokens"] = 4096,
                    ["stream"] = true,
                    ["messages"] = messages,
                };
                if (!string.IsNullOrEmpty(systemInstruction))
                    requestObj["system"] = systemInstruction;

                var jsonContent = JsonSerializer.Serialize(requestObj);

                using var request = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var errorMsg = TryExtractErrorMessage(errorBody);
                    return $"[Error] Claude API ({(int)response.StatusCode}): {errorMsg}";
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
                    if (string.IsNullOrWhiteSpace(json) || json == "[DONE]") continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var typeProp)) continue;
                        var eventType = typeProp.GetString();

                        if (eventType == "content_block_delta" &&
                            root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var textProp))
                        {
                            var chunk = textProp.GetString();
                            if (chunk != null)
                            {
                                fullText.Append(chunk);
                                onTextChunk(chunk);
                            }
                        }
                        else if (eventType == "error" &&
                                 root.TryGetProperty("error", out var errObj) &&
                                 errObj.TryGetProperty("message", out var errMsg))
                        {
                            return $"[Error] {errMsg.GetString()}";
                        }
                    }
                    catch { /* malformed chunk, skip */ }
                }

                return fullText.Length > 0 ? fullText.ToString() : "[Error] No response from Claude.";
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
                AppLogger.Warn("ClaudeService",
                    "Failed to decrypt API key. Please re-enter your API key in Settings > Claude.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
            catch (FormatException ex)
            {
                AppLogger.Warn("ClaudeService",
                    "Stored API key has invalid Base64 encoding. Please re-enter your API key in Settings > Claude.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
        }
    }
}
