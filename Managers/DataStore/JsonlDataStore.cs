using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers.DataStore
{
    /// <summary>
    /// JSONL (one JSON object per line) data store. Ideal for collections where
    /// line-level git diffs and append-friendly writes are desirable.
    /// </summary>
    public class JsonlDataStore<T> : IJsonlDataStore<T> where T : class
    {
        private readonly string _filePath;
        private readonly DataStoreOptions _options;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public JsonlDataStore(string filePath, DataStoreOptions? options = null)
        {
            _filePath = filePath;
            _options = options ?? new DataStoreOptions
            {
                JsonOptions = DataStoreOptions.CompactJsonOptions
            };
        }

        public async Task<List<T>> LoadAsync()
        {
            if (!File.Exists(_filePath))
                return new List<T>();

            var results = new List<T>();
            try
            {
                await foreach (var line in ReadLinesAsync())
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var item = JsonSerializer.Deserialize<T>(line, _options.JsonOptions);
                        if (item != null)
                            results.Add(item);
                    }
                    catch
                    {
                        // Skip malformed lines — graceful degradation
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn(_options.CallerName, $"Failed to load {Path.GetFileName(_filePath)}", ex);
            }

            return results;
        }

        public async Task SaveAsync(List<T> items)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureDirectory();

                var sb = new StringBuilder();
                foreach (var item in items)
                {
                    var line = JsonSerializer.Serialize(item, _options.JsonOptions);
                    sb.Append(line);
                    sb.Append('\n');
                }

                var content = sb.ToString();

                if (_options.AtomicWrite)
                {
                    var tmpPath = _filePath + ".tmp";
                    await File.WriteAllTextAsync(tmpPath, content).ConfigureAwait(false);
                    File.Move(tmpPath, _filePath, overwrite: true);
                }
                else
                {
                    await File.WriteAllTextAsync(_filePath, content).ConfigureAwait(false);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task AppendAsync(T item)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureDirectory();
                var line = JsonSerializer.Serialize(item, _options.JsonOptions) + "\n";
                await File.AppendAllTextAsync(_filePath, line).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task UpsertAsync(T item, Func<T, T, bool> matchPredicate)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var items = await LoadInternalAsync();
                var existingIndex = items.FindIndex(existing => matchPredicate(existing, item));

                if (existingIndex >= 0)
                    items[existingIndex] = item;
                else
                    items.Add(item);

                await SaveInternalAsync(items);
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

        // Internal methods that skip the semaphore (caller already holds it)
        private async Task<List<T>> LoadInternalAsync()
        {
            if (!File.Exists(_filePath))
                return new List<T>();

            var results = new List<T>();
            await foreach (var line in ReadLinesAsync())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(line, _options.JsonOptions);
                    if (item != null) results.Add(item);
                }
                catch { /* skip malformed */ }
            }
            return results;
        }

        private async Task SaveInternalAsync(List<T> items)
        {
            EnsureDirectory();
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.Append(JsonSerializer.Serialize(item, _options.JsonOptions));
                sb.Append('\n');
            }

            if (_options.AtomicWrite)
            {
                var tmpPath = _filePath + ".tmp";
                await File.WriteAllTextAsync(tmpPath, sb.ToString()).ConfigureAwait(false);
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(_filePath, sb.ToString()).ConfigureAwait(false);
            }
        }

        private async IAsyncEnumerable<string> ReadLinesAsync()
        {
            using var reader = new StreamReader(_filePath, Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
                yield return line;
        }

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
