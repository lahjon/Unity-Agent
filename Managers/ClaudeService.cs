using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
{
    public class ClaudeService : BaseLlmService
    {
        private const string ApiBaseUrl = "https://api.anthropic.com/v1/messages";
        private ClaudeUsageManager? _usageManager;

        public static string[] AvailableModels { get; set; } =
            (string[])AppConstants.ClaudeAvailableModels.Clone();

        protected override string ServiceName => "ClaudeService";
        protected override string DefaultModelId => AppConstants.ClaudeDefaultChatModel;

        public ClaudeService(string appDataDir) : base(appDataDir, "claude_chat_config.json") { }

        public void SetUsageManager(ClaudeUsageManager usageManager)
        {
            _usageManager = usageManager;
        }

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
                {
                    // Use cache_control to enable prompt caching on the static system instruction.
                    // This is GA — no beta header required. Cached tokens are ~90% cheaper on
                    // subsequent calls within the 5-minute TTL window.
                    requestObj["system"] = new[]
                    {
                        new
                        {
                            type = "text",
                            text = systemInstruction,
                            cache_control = new { type = "ephemeral" }
                        }
                    };
                }

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
                        else if (eventType == "message_stop" &&
                                 root.TryGetProperty("message", out var message) &&
                                 message.TryGetProperty("usage", out var usage))
                        {
                            // Extract usage information
                            long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheCreationTokens = 0;

                            if (usage.TryGetProperty("input_tokens", out var inputProp))
                                inputTokens = inputProp.GetInt64();
                            if (usage.TryGetProperty("output_tokens", out var outputProp))
                                outputTokens = outputProp.GetInt64();
                            if (usage.TryGetProperty("cache_read_input_tokens", out var cacheReadProp))
                                cacheReadTokens = cacheReadProp.GetInt64();
                            if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheCreationProp))
                                cacheCreationTokens = cacheCreationProp.GetInt64();

                            // Report usage to the manager
                            _usageManager?.AddUsage(_selectedModel, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
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

        /// <summary>
        /// Sends a single-turn prompt to the Haiku model via direct API call and returns
        /// structured JSON output. Uses tool_use with forced tool_choice to guarantee
        /// schema-conformant JSON. Returns null on any failure.
        /// </summary>
        public async Task<JsonElement?> SendStructuredHaikuAsync(
            string prompt,
            string jsonSchema,
            CancellationToken ct = default)
        {
            if (!IsConfigured)
            {
                AppLogger.Warn("ClaudeService", "SendStructuredHaikuAsync: API key not configured");
                return null;
            }

            var apiKey = GetApiKey();

            try
            {
                // Parse the JSON schema to use as tool input_schema
                using var schemaDoc = JsonDocument.Parse(jsonSchema);
                var inputSchema = schemaDoc.RootElement.Clone();

                var requestObj = new Dictionary<string, object>
                {
                    ["model"] = AppConstants.ClaudeHaiku,
                    ["max_tokens"] = 4096,
                    ["stream"] = false,
                    // Use cache_control on the system instruction so the static schema
                    // description is cached across repeated calls (90% cheaper within 5-min TTL).
                    ["system"] = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "You are a structured data extraction assistant. Return results using the provided tool.",
                            cache_control = new { type = "ephemeral" }
                        }
                    },
                    ["messages"] = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    // cache_control on the tool definition caches the schema itself,
                    // which is typically identical across calls with the same schema.
                    ["tools"] = new[]
                    {
                        new
                        {
                            name = "structured_result",
                            description = "Return the structured result",
                            input_schema = inputSchema,
                            cache_control = new { type = "ephemeral" }
                        }
                    },
                    ["tool_choice"] = new { type = "tool", name = "structured_result" }
                };

                var jsonContent = JsonSerializer.Serialize(requestObj);

                using var request = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");

                using var response = await _httpClient.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    var errorMsg = TryExtractErrorMessage(errorBody);
                    AppLogger.Warn("ClaudeService",
                        $"SendStructuredHaikuAsync API error ({(int)response.StatusCode}): {errorMsg}");
                    return null;
                }

                var responseText = await response.Content.ReadAsStringAsync(ct);
                using var responseDoc = JsonDocument.Parse(responseText);
                var root = responseDoc.RootElement;

                // Track usage
                if (root.TryGetProperty("usage", out var usage))
                {
                    long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheCreationTokens = 0;
                    if (usage.TryGetProperty("input_tokens", out var inp)) inputTokens = inp.GetInt64();
                    if (usage.TryGetProperty("output_tokens", out var outp)) outputTokens = outp.GetInt64();
                    if (usage.TryGetProperty("cache_read_input_tokens", out var cr)) cacheReadTokens = cr.GetInt64();
                    if (usage.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreationTokens = cc.GetInt64();
                    _usageManager?.AddUsage(AppConstants.ClaudeHaiku, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
                }

                // Extract tool_use input from content blocks
                if (root.TryGetProperty("content", out var content))
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "tool_use" &&
                            block.TryGetProperty("input", out var input))
                        {
                            return input.Clone();
                        }
                    }
                }

                AppLogger.Warn("ClaudeService", "SendStructuredHaikuAsync: no tool_use block in response");
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("ClaudeService", $"SendStructuredHaikuAsync failed: {ex.Message}");
                return null;
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
