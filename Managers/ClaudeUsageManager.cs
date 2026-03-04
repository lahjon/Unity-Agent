using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace Spritely.Managers
{
    /// <summary>
    /// Tracks cumulative Claude API usage across all API calls.
    /// Persists usage data to disk and provides usage statistics.
    /// </summary>
    public class ClaudeUsageManager
    {
        private readonly string _usageFilePath;
        private readonly object _lock = new object();

        public class UsageData
        {
            public long TotalInputTokens { get; set; }
            public long TotalOutputTokens { get; set; }
            public long TotalCacheReadTokens { get; set; }
            public long TotalCacheCreationTokens { get; set; }
            public DateTime LastUpdated { get; set; }
            public Dictionary<string, ModelUsage> ModelUsage { get; set; } = new();
        }

        public class ModelUsage
        {
            public long InputTokens { get; set; }
            public long OutputTokens { get; set; }
            public long CacheReadTokens { get; set; }
            public long CacheCreationTokens { get; set; }
            public int RequestCount { get; set; }
        }

        private UsageData _usage = new();

        public event EventHandler<UsageData>? UsageUpdated;

        public ClaudeUsageManager(string appDataDir)
        {
            _usageFilePath = Path.Combine(appDataDir, "claude_usage.json");
            LoadUsageData();
        }

        private void LoadUsageData()
        {
            try
            {
                if (File.Exists(_usageFilePath))
                {
                    var json = File.ReadAllText(_usageFilePath);
                    _usage = JsonSerializer.Deserialize<UsageData>(json) ?? new UsageData();
                }
                else
                {
                    _usage = new UsageData();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("ClaudeUsageManager", $"Failed to load Claude usage data: {ex.Message}", ex);
                _usage = new UsageData();
            }
        }

        private void SaveUsageData()
        {
            try
            {
                var json = JsonSerializer.Serialize(_usage, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_usageFilePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Error("ClaudeUsageManager", $"Failed to save Claude usage data: {ex.Message}", ex);
            }
        }

        public void AddUsage(string model, long inputTokens, long outputTokens,
                           long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            lock (_lock)
            {
                _usage.TotalInputTokens += inputTokens;
                _usage.TotalOutputTokens += outputTokens;
                _usage.TotalCacheReadTokens += cacheReadTokens;
                _usage.TotalCacheCreationTokens += cacheCreationTokens;
                _usage.LastUpdated = DateTime.UtcNow;

                if (!_usage.ModelUsage.ContainsKey(model))
                    _usage.ModelUsage[model] = new ModelUsage();

                var modelUsage = _usage.ModelUsage[model];
                modelUsage.InputTokens += inputTokens;
                modelUsage.OutputTokens += outputTokens;
                modelUsage.CacheReadTokens += cacheReadTokens;
                modelUsage.CacheCreationTokens += cacheCreationTokens;
                modelUsage.RequestCount++;

                SaveUsageData();
                UsageUpdated?.Invoke(this, _usage);
            }
        }

        public UsageData GetUsage()
        {
            lock (_lock)
            {
                return _usage;
            }
        }

        public string GetUsageSummary()
        {
            lock (_lock)
            {
                var totalTokens = _usage.TotalInputTokens + _usage.TotalOutputTokens +
                                _usage.TotalCacheReadTokens + _usage.TotalCacheCreationTokens;

                if (totalTokens == 0)
                    return "Claude: No usage yet";

                var cost = Helpers.FormatHelpers.EstimateCost(
                    _usage.TotalInputTokens,
                    _usage.TotalOutputTokens,
                    _usage.TotalCacheReadTokens,
                    _usage.TotalCacheCreationTokens);

                var costStr = Helpers.FormatHelpers.FormatCost(cost);
                var tokenStr = Helpers.FormatHelpers.FormatTokenCount(totalTokens);

                // Get the most used model
                var topModel = _usage.ModelUsage.OrderByDescending(x => x.Value.RequestCount)
                                               .FirstOrDefault().Key;

                if (!string.IsNullOrEmpty(topModel))
                {
                    // Shorten model name for display
                    var modelDisplay = topModel.Contains("opus") ? "Opus" :
                                     topModel.Contains("sonnet") ? "Sonnet" :
                                     topModel.Contains("haiku") ? "Haiku" : topModel;

                    return $"Claude ({modelDisplay}): {tokenStr} tokens (~{costStr})";
                }

                return $"Claude: {tokenStr} tokens (~{costStr})";
            }
        }

        public void ResetUsage()
        {
            lock (_lock)
            {
                _usage = new UsageData();
                SaveUsageData();
                UsageUpdated?.Invoke(this, _usage);
            }
        }
    }
}