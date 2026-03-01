using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public class ClaudeService : BaseLlmService
    {
        private const string ApiBaseUrl = "https://api.anthropic.com/v1/messages";

        public static string[] AvailableModels { get; set; } =
        {
            "claude-opus-4-20250514",
            "claude-sonnet-4-20250514",
            "claude-haiku-4-20250414",
            "claude-3-5-sonnet-20241022",
            "claude-3-5-haiku-20241022",
        };

        protected override string ServiceName => "ClaudeService";
        protected override string DefaultModelId => "claude-sonnet-4-20250514";

        public ClaudeService(string appDataDir) : base(appDataDir, "claude_chat_config.json") { }

        public override async Task<string> SendChatMessageStreamingAsync(
            List<ChatMessage> history,
            string userMessage,
            Action<string> onTextChunk,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return GetApiKeyError();

            var apiKey = GetApiKey();

            try
            {
                var messages = new List<object>();
                foreach (var msg in history)
                {
                    messages.Add(new
                    {
                        role = msg.Role == "model" ? "assistant" : "user",
                        content = BuildClaudeContent(msg.Text, msg.ImagePaths)
                    });
                }
                messages.Add(new { role = "user", content = BuildClaudeContent(userMessage, null) });

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
                request.Headers.Add("x-api-key", apiKey);
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
                    catch (Exception ex) { AppLogger.Debug("ClaudeService", $"Malformed SSE chunk skipped: {ex.Message}"); }
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

        private static object BuildClaudeContent(string text, List<string>? imagePaths)
        {
            if (imagePaths == null || imagePaths.Count == 0)
                return text;

            var contentParts = new List<object>();
            foreach (var path in imagePaths)
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var base64 = Convert.ToBase64String(bytes);
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    var mediaType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "image/png"
                    };
                    contentParts.Add(new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = mediaType, data = base64 }
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("ClaudeService", $"Failed to read image {path}: {ex.Message}");
                }
            }
            contentParts.Add(new { type = "text", text });
            return contentParts;
        }
    }
}
