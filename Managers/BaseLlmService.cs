using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public abstract class BaseLlmService : IDisposable
    {
        protected readonly string _configFile;
        protected readonly HttpClient _httpClient;
        protected string? _apiKey;
        protected readonly object _apiKeyLock = new();
        protected string _selectedModel;

        protected abstract string ServiceName { get; }
        protected abstract string DefaultModelId { get; }

        public bool ApiKeyDecryptionFailed { get; protected set; }
        public virtual bool IsConfigured { get { lock (_apiKeyLock) return !string.IsNullOrEmpty(_apiKey); } }

        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                SaveConfig();
            }
        }

        protected BaseLlmService(string appDataDir, string configFileName)
        {
            _selectedModel = DefaultModelId;
            _configFile = Path.Combine(appDataDir, configFileName);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            LoadConfig();
        }

        protected virtual void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configFile)) return;
                var json = File.ReadAllText(_configFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("encryptedApiKey", out var encKey))
                {
                    var decrypted = DecryptString(encKey.GetString());
                    lock (_apiKeyLock) _apiKey = decrypted;
                }
                if (doc.RootElement.TryGetProperty("model", out var model))
                    _selectedModel = model.GetString() ?? DefaultModelId;
            }
            catch (Exception ex) { AppLogger.Warn(ServiceName, "Failed to load config", ex); }
        }

        public virtual void SaveApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key cannot be empty.");
            lock (_apiKeyLock) _apiKey = apiKey.Trim();
            ApiKeyDecryptionFailed = false;
            SaveConfig();
        }

        protected void SaveConfig()
        {
            try
            {
                var config = new Dictionary<string, string>();
                string? keyCopy;
                lock (_apiKeyLock) keyCopy = _apiKey;
                if (!string.IsNullOrEmpty(keyCopy))
                    config["encryptedApiKey"] = EncryptString(keyCopy);
                config["model"] = _selectedModel;
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_configFile, json, ServiceName);
            }
            catch (Exception ex) { AppLogger.Warn(ServiceName, "Failed to save config", ex); }
        }

        public string GetMaskedApiKey()
        {
            string? key;
            lock (_apiKeyLock) key = _apiKey;
            if (string.IsNullOrEmpty(key)) return "";
            if (key.Length <= 8) return new string('*', key.Length);
            return key[..4] + new string('*', key.Length - 8) + key[^4..];
        }

        protected string? GetApiKey() { lock (_apiKeyLock) return _apiKey; }

        public string? GetApiKeyForRefresh() { lock (_apiKeyLock) return _apiKey; }

        protected static string TryExtractErrorMessage(string responseBody)
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
            catch (Exception ex) { AppLogger.Debug("LlmService", $"Failed to parse error response JSON: {ex.Message}"); }
            return responseBody.Length > 200 ? responseBody[..200] : responseBody;
        }

        protected static string EncryptString(string plainText)
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        protected string? DecryptString(string? cipherBase64)
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
                AppLogger.Warn(ServiceName,
                    $"Failed to decrypt API key. Please re-enter it in Settings > {ServiceName.Replace("Service", "")}.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
            catch (FormatException ex)
            {
                AppLogger.Warn(ServiceName,
                    $"Stored API key has invalid Base64 encoding. Please re-enter your API key in Settings > {ServiceName.Replace("Service", "")}.", ex);
                ApiKeyDecryptionFailed = true;
                return null;
            }
        }

        protected string GetApiKeyError()
        {
            var label = ServiceName.Replace("Service", "");
            return ApiKeyDecryptionFailed
                ? $"[Error] {label} API key could not be decrypted. Please re-enter it in Settings > {label}."
                : $"[Error] {label} API key not configured. Set it in Settings > {label}.";
        }

        public abstract Task<string> SendChatMessageStreamingAsync(
            List<ChatMessage> history,
            string userMessage,
            Action<string> onTextChunk,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default);

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
