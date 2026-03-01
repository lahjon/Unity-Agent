using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Service for deduplicating common context across multiple agents to reduce token usage.
    /// Extracts and caches reusable prompt components like system prompts, rules, and project descriptions.
    /// </summary>
    public class ContextDeduplicationService
    {
        // Cache for deduplicated context elements
        private readonly ConcurrentDictionary<string, CachedContextElement> _contextCache = new();

        // Patterns for identifying common context blocks
        private static readonly Dictionary<string, Regex> ContextPatterns = new()
        {
            ["SystemPrompt"] = new Regex(@"^# SYSTEM\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["Rules"] = new Regex(@"^# RULES?\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["ProjectContext"] = new Regex(@"^# PROJECT CONTEXT\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["GameProject"] = new Regex(@"^# GAME PROJECT\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["MessageBus"] = new Regex(@"^# MESSAGE BUS\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["Instructions"] = new Regex(@"^# (?:INSTRUCTIONS|GUIDELINES)\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline),
            ["Constants"] = new Regex(@"^# (?:CONSTANTS|CONFIGURATION)\s*\n([\s\S]*?)(?=^#\s|\z)", RegexOptions.Multiline)
        };

        /// <summary>
        /// Extracts and caches common context elements from a full prompt.
        /// Returns the deduplicated prompt with references to cached elements.
        /// </summary>
        public DeduplicatedPrompt DeduplicateContext(string fullPrompt, string taskId)
        {
            var result = new DeduplicatedPrompt
            {
                TaskId = taskId,
                OriginalLength = fullPrompt.Length
            };

            var workingPrompt = fullPrompt;
            var extractedElements = new List<ExtractedElement>();

            // Extract each type of common context
            foreach (var pattern in ContextPatterns)
            {
                var match = pattern.Value.Match(workingPrompt);
                if (match.Success)
                {
                    var elementContent = match.Groups[0].Value;
                    var elementHash = ComputeHash(elementContent);

                    // Check if this element is already cached
                    var cached = _contextCache.GetOrAdd(elementHash, _ => new CachedContextElement
                    {
                        Hash = elementHash,
                        Type = pattern.Key,
                        Content = elementContent,
                        FirstSeen = DateTime.Now,
                        UseCount = 0
                    });

                    cached.UseCount++;
                    cached.LastUsed = DateTime.Now;

                    extractedElements.Add(new ExtractedElement
                    {
                        Type = pattern.Key,
                        Hash = elementHash,
                        StartIndex = match.Index,
                        Length = match.Length
                    });

                    result.CachedElementHashes.Add(elementHash);
                }
            }

            // Build deduplicated prompt
            result.Text = BuildDeduplicatedPrompt(workingPrompt, extractedElements);
            result.DeduplicatedLength = result.Text.Length;
            result.TokenSavingsPercent = (1.0 - (double)result.DeduplicatedLength / result.OriginalLength) * 100;

            AppLogger.Info("ContextDedup",
                $"Task {taskId}: Reduced prompt from {result.OriginalLength} to {result.DeduplicatedLength} chars " +
                $"({result.TokenSavingsPercent:F1}% reduction)");

            return result;
        }

        /// <summary>
        /// Injects cached context elements back into a prompt for a specific task.
        /// Used when building the final prompt for execution.
        /// </summary>
        public string InjectContext(DeduplicatedPrompt deduplicatedPrompt, HashSet<string>? includeOnly = null)
        {
            var sb = new StringBuilder();

            // Add cached elements first
            foreach (var hash in deduplicatedPrompt.CachedElementHashes)
            {
                if (includeOnly != null && !includeOnly.Contains(hash))
                    continue;

                if (_contextCache.TryGetValue(hash, out var element))
                {
                    sb.AppendLine(element.Content);
                    sb.AppendLine();
                }
            }

            // Add the deduplicated prompt content
            sb.Append(deduplicatedPrompt.Text);

            return sb.ToString();
        }

        /// <summary>
        /// Gets a minimal prompt for team members that includes only essential context.
        /// </summary>
        public string GetMinimalTeamMemberPrompt(DeduplicatedPrompt originalPrompt, string memberRole)
        {
            // Define which context types are essential for different roles
            var essentialTypes = memberRole.ToLowerInvariant() switch
            {
                "architect" => new[] { "SystemPrompt", "ProjectContext", "GameProject" },
                "developer" => new[] { "SystemPrompt", "Rules", "ProjectContext" },
                "tester" => new[] { "SystemPrompt", "ProjectContext" },
                "reviewer" => new[] { "SystemPrompt", "Rules", "ProjectContext" },
                _ => new[] { "SystemPrompt", "ProjectContext" }
            };

            var essentialHashes = new HashSet<string>();
            foreach (var hash in originalPrompt.CachedElementHashes)
            {
                if (_contextCache.TryGetValue(hash, out var element) && essentialTypes.Contains(element.Type))
                {
                    essentialHashes.Add(hash);
                }
            }

            return InjectContext(originalPrompt, essentialHashes);
        }

        /// <summary>
        /// Clears old cached elements that haven't been used recently.
        /// </summary>
        public void CleanupOldCache(TimeSpan maxAge)
        {
            var cutoffTime = DateTime.Now - maxAge;
            var keysToRemove = _contextCache
                .Where(kvp => kvp.Value.LastUsed < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _contextCache.TryRemove(key, out _);
            }

            if (keysToRemove.Count > 0)
            {
                AppLogger.Debug("ContextDedup", $"Cleaned up {keysToRemove.Count} old cache entries");
            }
        }

        /// <summary>
        /// Gets statistics about the current cache usage.
        /// </summary>
        public CacheStatistics GetCacheStatistics()
        {
            var stats = new CacheStatistics
            {
                TotalElements = _contextCache.Count,
                TotalSizeBytes = _contextCache.Values.Sum(e => e.Content.Length),
                ElementsByType = _contextCache.Values
                    .GroupBy(e => e.Type)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AverageUseCount = _contextCache.Values.Any()
                    ? _contextCache.Values.Average(e => e.UseCount)
                    : 0
            };

            return stats;
        }

        private string BuildDeduplicatedPrompt(string originalPrompt, List<ExtractedElement> extractedElements)
        {
            if (extractedElements.Count == 0)
                return originalPrompt;

            // Sort by start index in reverse order to replace from end to beginning
            extractedElements.Sort((a, b) => b.StartIndex.CompareTo(a.StartIndex));

            var result = new StringBuilder(originalPrompt);
            foreach (var element in extractedElements)
            {
                var reference = $"[CACHED_CONTEXT: {element.Type}:{element.Hash[..8]}]\n";
                result.Remove(element.StartIndex, element.Length);
                result.Insert(element.StartIndex, reference);
            }

            return result.ToString();
        }

        private static string ComputeHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return Convert.ToBase64String(bytes);
        }

        // Data structures
        private class CachedContextElement
        {
            public string Hash { get; set; } = "";
            public string Type { get; set; } = "";
            public string Content { get; set; } = "";
            public DateTime FirstSeen { get; set; }
            public DateTime LastUsed { get; set; }
            public int UseCount { get; set; }
        }

        private class ExtractedElement
        {
            public string Type { get; set; } = "";
            public string Hash { get; set; } = "";
            public int StartIndex { get; set; }
            public int Length { get; set; }
        }

        public class DeduplicatedPrompt
        {
            public string TaskId { get; set; } = "";
            public string Text { get; set; } = "";
            public List<string> CachedElementHashes { get; set; } = new();
            public int OriginalLength { get; set; }
            public int DeduplicatedLength { get; set; }
            public double TokenSavingsPercent { get; set; }
        }

        public class CacheStatistics
        {
            public int TotalElements { get; set; }
            public long TotalSizeBytes { get; set; }
            public Dictionary<string, int> ElementsByType { get; set; } = new();
            public double AverageUseCount { get; set; }
        }
    }
}