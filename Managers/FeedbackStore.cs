using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Persists feedback entries and insights per-project in %LOCALAPPDATA%\Spritely\feedback\.
    /// Thread-safe via locking on read/write operations.
    /// </summary>
    public class FeedbackStore
    {
        private readonly string _feedbackDir;
        private readonly object _lock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public FeedbackStore(string appDataDir)
        {
            _feedbackDir = Path.Combine(appDataDir, "feedback");
            Directory.CreateDirectory(_feedbackDir);
        }

        /// <summary>Appends a feedback entry for the given project.</summary>
        public void SaveEntry(FeedbackEntry entry)
        {
            try
            {
                var file = GetProjectFeedbackFile(entry.ProjectPath);
                List<FeedbackEntry> entries;
                lock (_lock)
                {
                    entries = LoadEntriesFromFile(file);
                    entries.Add(entry);

                    // Keep last 200 entries per project to bound storage
                    if (entries.Count > 200)
                        entries = entries.Skip(entries.Count - 200).ToList();

                    var json = JsonSerializer.Serialize(entries, JsonOptions);
                    SafeFileWriter.WriteInBackground(file, json, "FeedbackStore");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackStore", "Failed to save feedback entry", ex);
            }
        }

        /// <summary>Loads all feedback entries for a project.</summary>
        public List<FeedbackEntry> LoadEntries(string projectPath)
        {
            try
            {
                var file = GetProjectFeedbackFile(projectPath);
                lock (_lock)
                {
                    return LoadEntriesFromFile(file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackStore", "Failed to load feedback entries", ex);
                return new List<FeedbackEntry>();
            }
        }

        /// <summary>Loads all feedback entries across all projects.</summary>
        public List<FeedbackEntry> LoadAllEntries()
        {
            var all = new List<FeedbackEntry>();
            try
            {
                if (!Directory.Exists(_feedbackDir)) return all;
                foreach (var file in Directory.GetFiles(_feedbackDir, "feedback_*.json"))
                {
                    lock (_lock)
                    {
                        all.AddRange(LoadEntriesFromFile(file));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackStore", "Failed to load all feedback entries", ex);
            }
            return all;
        }

        /// <summary>Saves an aggregated insight for a project.</summary>
        public void SaveInsight(FeedbackInsight insight)
        {
            try
            {
                var file = GetInsightFile(insight.ProjectPath);
                List<FeedbackInsight> insights;
                lock (_lock)
                {
                    insights = LoadInsightsFromFile(file);
                    insights.Add(insight);

                    // Keep last 50 insights per project
                    if (insights.Count > 50)
                        insights = insights.Skip(insights.Count - 50).ToList();

                    var json = JsonSerializer.Serialize(insights, JsonOptions);
                    SafeFileWriter.WriteInBackground(file, json, "FeedbackStore");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackStore", "Failed to save feedback insight", ex);
            }
        }

        /// <summary>Loads insights for a project.</summary>
        public List<FeedbackInsight> LoadInsights(string projectPath)
        {
            try
            {
                var file = GetInsightFile(projectPath);
                lock (_lock)
                {
                    return LoadInsightsFromFile(file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FeedbackStore", "Failed to load feedback insights", ex);
                return new List<FeedbackInsight>();
            }
        }

        /// <summary>Returns the number of feedback entries since the last insight was generated.</summary>
        public int GetEntriesSinceLastInsight(string projectPath)
        {
            var entries = LoadEntries(projectPath);
            var insights = LoadInsights(projectPath);
            var lastInsightTime = insights.Count > 0
                ? insights.Max(i => i.GeneratedAt)
                : DateTime.MinValue;
            return entries.Count(e => e.Timestamp > lastInsightTime);
        }

        private string GetProjectFeedbackFile(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            return Path.Combine(_feedbackDir, $"feedback_{hash}.json");
        }

        private string GetInsightFile(string projectPath)
        {
            var hash = ComputePathHash(projectPath);
            return Path.Combine(_feedbackDir, $"insights_{hash}.json");
        }

        private static string ComputePathHash(string path)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant().TrimEnd('\\', '/')));
            return Convert.ToHexStringLower(bytes)[..16];
        }

        private static List<FeedbackEntry> LoadEntriesFromFile(string file)
        {
            if (!File.Exists(file)) return new List<FeedbackEntry>();
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<List<FeedbackEntry>>(json, JsonOptions) ?? new List<FeedbackEntry>();
        }

        private static List<FeedbackInsight> LoadInsightsFromFile(string file)
        {
            if (!File.Exists(file)) return new List<FeedbackInsight>();
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<List<FeedbackInsight>>(json, JsonOptions) ?? new List<FeedbackInsight>();
        }
    }
}
