using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers.DataStore
{
    /// <summary>
    /// Stores each item in a separate file within a directory, keyed by a string identifier.
    /// Useful for per-task or per-entity storage (e.g. iteration_1.json, iteration_2.json).
    /// </summary>
    public class PartitionedDataStore<T> : IPartitionedDataStore<T> where T : class
    {
        private readonly string _directory;
        private readonly string _filePrefix;
        private readonly string _fileExtension;
        private readonly DataStoreOptions _options;
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public PartitionedDataStore(string directory, DataStoreOptions? options = null,
            string filePrefix = "", string fileExtension = ".json")
        {
            _directory = directory;
            _filePrefix = filePrefix;
            _fileExtension = fileExtension;
            _options = options ?? new DataStoreOptions();
        }

        public async Task<T?> LoadAsync(string key)
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
                return default;

            try
            {
                var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    return default;

                return JsonSerializer.Deserialize<T>(json, _options.JsonOptions);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(_options.CallerName, $"Failed to load partition '{key}'", ex);
                return default;
            }
        }

        public async Task<List<T>> LoadAllAsync()
        {
            if (!Directory.Exists(_directory))
                return new List<T>();

            var results = new List<T>();
            var pattern = $"{_filePrefix}*{_fileExtension}";

            foreach (var file in Directory.GetFiles(_directory, pattern).OrderBy(f => f))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var item = JsonSerializer.Deserialize<T>(json, _options.JsonOptions);
                    if (item != null)
                        results.Add(item);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(_options.CallerName, $"Failed to load partition file {Path.GetFileName(file)}", ex);
                }
            }

            return results;
        }

        public async Task SaveAsync(string key, T data)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureDirectory();
                var filePath = GetFilePath(key);
                var json = JsonSerializer.Serialize(data, _options.JsonOptions);

                if (_options.AtomicWrite)
                {
                    var tmpPath = filePath + ".tmp";
                    await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);
                    File.Move(tmpPath, filePath, overwrite: true);
                }
                else
                {
                    await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Task DeleteAsync(string key)
        {
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
                File.Delete(filePath);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
            return Task.CompletedTask;
        }

        public bool Exists(string key) => File.Exists(GetFilePath(key));

        public List<string> ListKeys()
        {
            if (!Directory.Exists(_directory))
                return new List<string>();

            var pattern = $"{_filePrefix}*{_fileExtension}";
            return Directory.GetFiles(_directory, pattern)
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Select(name => _filePrefix.Length > 0 && name.StartsWith(_filePrefix)
                    ? name[_filePrefix.Length..]
                    : name)
                .OrderBy(k => k)
                .ToList();
        }

        private string GetFilePath(string key) =>
            Path.Combine(_directory, $"{_filePrefix}{key}{_fileExtension}");

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_directory))
                Directory.CreateDirectory(_directory);
        }
    }
}
