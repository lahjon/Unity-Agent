using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers.DataStore
{
    /// <summary>
    /// Single-file JSON data store with versioned envelope and atomic writes.
    /// Supports transparent schema migration on load.
    /// </summary>
    public class JsonDataStore<T> : IDataStore<T> where T : class
    {
        private readonly string _filePath;
        private readonly DataStoreOptions _options;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public JsonDataStore(string filePath, DataStoreOptions? options = null)
        {
            _filePath = filePath;
            _options = options ?? new DataStoreOptions();
        }

        public async Task<T?> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return default;

            try
            {
                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return default;

                return DeserializeWithVersioning(json);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(_options.CallerName, $"Failed to load {Path.GetFileName(_filePath)}", ex);
                return default;
            }
        }

        public async Task SaveAsync(T data)
        {
            var envelope = new VersionedEnvelope<T>
            {
                Version = _options.SchemaVersion,
                Data = data
            };

            var json = JsonSerializer.Serialize(envelope, _options.JsonOptions);

            if (_options.BackgroundWrite)
            {
                SafeFileWriter.WriteInBackground(_filePath, json, _options.CallerName);
                return;
            }

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureDirectory();
                if (_options.AtomicWrite)
                    await AtomicWriteAsync(json).ConfigureAwait(false);
                else
                    await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Task DeleteAsync()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
            return Task.CompletedTask;
        }

        public bool Exists() => File.Exists(_filePath);

        private T? DeserializeWithVersioning(string json)
        {
            // Try versioned envelope first
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("v", out var vProp) &&
                    doc.RootElement.TryGetProperty("data", out _))
                {
                    var storedVersion = vProp.GetInt32();

                    if (storedVersion < _options.SchemaVersion && _options.Migrator != null)
                    {
                        var dataJson = doc.RootElement.GetProperty("data").GetRawText();
                        var migratedJson = _options.Migrator(dataJson, storedVersion);
                        return JsonSerializer.Deserialize<T>(migratedJson, _options.JsonOptions);
                    }

                    var envelope = JsonSerializer.Deserialize<VersionedEnvelope<T>>(json, _options.JsonOptions);
                    return envelope?.Data;
                }
            }
            catch
            {
                // Fall through to legacy deserialization
            }

            // Legacy: raw data without envelope (backwards compatible)
            try
            {
                return JsonSerializer.Deserialize<T>(json, _options.JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(_options.CallerName,
                    $"Failed to deserialize {Path.GetFileName(_filePath)}", ex);
                return default;
            }
        }

        private async Task AtomicWriteAsync(string content)
        {
            var tmpPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, content).ConfigureAwait(false);
            File.Move(tmpPath, _filePath, overwrite: true);
        }

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
