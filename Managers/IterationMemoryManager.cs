using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages structured memory across teams mode iterations to prevent repeated failures
    /// and provide context about previous discoveries.
    /// </summary>
    public class IterationMemoryManager
    {
        private readonly string _memoryBasePath;
        private readonly object _lock = new();

        public IterationMemoryManager()
        {
            _memoryBasePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spritely", "iteration_history");
            Directory.CreateDirectory(_memoryBasePath);
        }

        /// <summary>
        /// Records the results of a teams mode iteration.
        /// </summary>
        public void RecordIteration(string taskId, int iteration, IterationMemory memory)
        {
            lock (_lock)
            {
                var taskDir = GetTaskDirectory(taskId);
                var filePath = Path.Combine(taskDir, $"iteration_{iteration}.json");

                var json = JsonSerializer.Serialize(memory, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                File.WriteAllText(filePath, json);
                AppLogger.Debug("IterationMemory", $"Recorded iteration {iteration} for task {taskId}");
            }
        }

        /// <summary>
        /// Retrieves memory from all previous iterations for a task.
        /// </summary>
        public List<IterationMemory> GetPreviousIterations(string taskId)
        {
            lock (_lock)
            {
                var taskDir = GetTaskDirectory(taskId);
                if (!Directory.Exists(taskDir))
                    return new List<IterationMemory>();

                var memories = new List<IterationMemory>();

                foreach (var file in Directory.GetFiles(taskDir, "iteration_*.json").OrderBy(f => f))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var memory = JsonSerializer.Deserialize<IterationMemory>(json);
                        if (memory != null)
                            memories.Add(memory);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("IterationMemory", $"Failed to load iteration memory from {file}", ex);
                    }
                }

                return memories;
            }
        }

        /// <summary>
        /// Builds a context prompt section from previous iteration memories.
        /// </summary>
        public string BuildIterationContext(string taskId, int currentIteration)
        {
            var memories = GetPreviousIterations(taskId);
            if (!memories.Any())
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("\n# PREVIOUS ITERATION HISTORY");
            sb.AppendLine("Use this to REFINE and IMPROVE — do NOT redo work that already succeeded.");

            // Separate successful work from gaps across all iterations
            var allSuccesses = new List<string>();
            var allFailures = new List<FailedApproach>();
            var allFiles = new HashSet<string>();

            foreach (var memory in memories.TakeLast(3)) // Last 3 iterations
            {
                sb.AppendLine($"\n## Iteration {memory.Iteration}");

                if (memory.KeyDiscoveries.Any())
                {
                    sb.AppendLine("### Key Discoveries:");
                    foreach (var discovery in memory.KeyDiscoveries)
                    {
                        sb.AppendLine($"- **{discovery.Type}**: {discovery.Description}");
                        if (!string.IsNullOrEmpty(discovery.Impact))
                            sb.AppendLine($"  Impact: {discovery.Impact}");
                    }
                }

                if (memory.SuccessfulWork.Any())
                {
                    sb.AppendLine("### Completed Successfully (DO NOT REDO):");
                    foreach (var success in memory.SuccessfulWork)
                    {
                        sb.AppendLine($"- {success}");
                        allSuccesses.Add(success);
                    }
                }

                if (memory.FailedApproaches.Any())
                {
                    sb.AppendLine("### Failed Approaches (DO NOT REPEAT):");
                    foreach (var failure in memory.FailedApproaches)
                    {
                        sb.AppendLine($"- **{failure.Approach}**: {failure.Reason}");
                        if (!string.IsNullOrEmpty(failure.ErrorDetails))
                            sb.AppendLine($"  Error: {failure.ErrorDetails}");
                        allFailures.Add(failure);
                    }
                }

                if (memory.RemainingGaps.Any())
                {
                    sb.AppendLine("### Remaining Gaps (FOCUS HERE):");
                    foreach (var gap in memory.RemainingGaps)
                    {
                        sb.AppendLine($"- {gap}");
                    }
                }

                if (memory.ImportantFiles.Any())
                {
                    sb.AppendLine("### Important Files:");
                    foreach (var file in memory.ImportantFiles.Take(5))
                    {
                        sb.AppendLine($"- `{file.Path}`: {file.Purpose}");
                        allFiles.Add(file.Path);
                    }
                }

                if (memory.ProgressMetrics != null)
                {
                    sb.AppendLine($"### Progress: {memory.ProgressMetrics.CompletionPercentage}% complete");
                    if (memory.ProgressMetrics.BlockingIssues.Any())
                    {
                        sb.AppendLine("Blocking issues:");
                        foreach (var issue in memory.ProgressMetrics.BlockingIssues)
                            sb.AppendLine($"- {issue}");
                    }
                }
            }

            // Add pattern analysis
            var patterns = AnalyzePatterns(memories);
            if (patterns.Any())
            {
                sb.AppendLine("\n### Recurring Patterns:");
                foreach (var pattern in patterns)
                {
                    sb.AppendLine($"- {pattern}");
                }
            }

            // Add refinement summary
            if (allSuccesses.Any() || allFailures.Any())
            {
                sb.AppendLine("\n## REFINEMENT DIRECTIVE");
                if (allSuccesses.Any())
                {
                    sb.AppendLine($"**{allSuccesses.Count} items already completed** — verify they exist, don't recreate.");
                }
                if (allFailures.Any())
                {
                    sb.AppendLine($"**{allFailures.Count} approaches failed** — use different strategies for these areas.");
                }
                if (allFiles.Any())
                {
                    sb.AppendLine($"**{allFiles.Count} files already modified** — build on existing changes.");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts memory from teams mode output.
        /// </summary>
        public IterationMemory ExtractMemoryFromOutput(string output, int iteration)
        {
            var memory = new IterationMemory
            {
                Iteration = iteration,
                Timestamp = DateTime.Now
            };

            // Extract discoveries
            ExtractDiscoveries(output, memory);

            // Extract failures
            ExtractFailures(output, memory);

            // Extract file references
            ExtractFileReferences(output, memory);

            // Extract successful work and remaining gaps from structured evaluation output
            ExtractSuccessfulWork(output, memory);
            ExtractRemainingGaps(output, memory);

            // Calculate progress metrics
            memory.ProgressMetrics = CalculateProgressMetrics(output);

            return memory;
        }

        private void ExtractDiscoveries(string output, IterationMemory memory)
        {
            // Pattern matching for discoveries
            var discoveryPatterns = new[]
            {
                @"(?:found|discovered|identified|detected)\s+(?:that\s+)?(.+)",
                @"(?:key\s+)?(?:insight|finding|observation):\s*(.+)",
                @"(?:important|critical|crucial):\s*(.+)",
                @"(?:architecture|design|pattern):\s*(.+)"
            };

            foreach (var pattern in discoveryPatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    output, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        memory.KeyDiscoveries.Add(new Discovery
                        {
                            Type = DetermineDiscoveryType(match.Value),
                            Description = match.Groups[1].Value.Trim(),
                            Timestamp = DateTime.Now
                        });
                    }
                }
            }
        }

        private void ExtractFailures(string output, IterationMemory memory)
        {
            // Pattern matching for failures
            var failurePatterns = new[]
            {
                @"(?:failed|error|exception)\s+(?:when|while|during)\s+(.+?)(?:\.|$)",
                @"(?:could not|unable to|cannot)\s+(.+?)(?:\.|$)",
                @"(?:approach|method|solution)\s+(?:failed|didn't work):\s*(.+)"
            };

            foreach (var pattern in failurePatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    output, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        memory.FailedApproaches.Add(new FailedApproach
                        {
                            Approach = ExtractApproachName(match.Groups[1].Value),
                            Reason = match.Groups[1].Value.Trim(),
                            Timestamp = DateTime.Now
                        });
                    }
                }
            }
        }

        private void ExtractFileReferences(string output, IterationMemory memory)
        {
            // Pattern matching for file references
            var filePattern = @"(?:file|modified|created|updated):\s*([^\s]+\.[a-zA-Z]+)";
            var matches = System.Text.RegularExpressions.Regex.Matches(output, filePattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var seenFiles = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var filePath = match.Groups[1].Value;
                    if (!seenFiles.Contains(filePath))
                    {
                        seenFiles.Add(filePath);
                        memory.ImportantFiles.Add(new FileReference
                        {
                            Path = filePath,
                            Purpose = DetermineFilePurpose(filePath, output),
                            LastModified = DateTime.Now
                        });
                    }
                }
            }
        }

        private void ExtractSuccessfulWork(string output, IterationMemory memory)
        {
            // Look for "WHAT WORKED" section from structured evaluation output
            var whatWorkedMatch = System.Text.RegularExpressions.Regex.Match(
                output, @"###?\s*WHAT WORKED\s*\n((?:- .+\n?)+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (whatWorkedMatch.Success)
            {
                var lines = whatWorkedMatch.Groups[1].Value.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.TrimStart('-', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        memory.SuccessfulWork.Add(trimmed);
                }
            }

            // Also extract from completion patterns
            var successPatterns = new[]
            {
                @"(?:successfully|correctly)\s+(?:implemented|added|created|configured)\s+(.+?)(?:\.|$)",
                @"(?:completed|finished):\s*(.+?)(?:\.|$)"
            };

            foreach (var pattern in successPatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    output, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var item = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(item) && item.Length < 200)
                            memory.SuccessfulWork.Add(item);
                    }
                }
            }

            // Deduplicate
            memory.SuccessfulWork = memory.SuccessfulWork.Distinct().Take(10).ToList();
        }

        private void ExtractRemainingGaps(string output, IterationMemory memory)
        {
            // Look for "GAPS IDENTIFIED" section from structured evaluation output
            var gapsMatch = System.Text.RegularExpressions.Regex.Match(
                output, @"###?\s*GAPS IDENTIFIED\s*\n((?:(?:- |\*\*).+\n?)+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

            if (gapsMatch.Success)
            {
                var lines = gapsMatch.Groups[1].Value.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.TrimStart('-', '*', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Length > 3)
                        memory.RemainingGaps.Add(trimmed);
                }
            }

            // Look for "RECOMMENDED FOCUS" section
            var focusMatch = System.Text.RegularExpressions.Regex.Match(
                output, @"###?\s*RECOMMENDED FOCUS\s*\n((?:- .+\n?)+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (focusMatch.Success)
            {
                var lines = focusMatch.Groups[1].Value.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.TrimStart('-', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !memory.RemainingGaps.Contains(trimmed))
                        memory.RemainingGaps.Add(trimmed);
                }
            }

            // Also extract from NEEDS_MORE_WORK patterns
            var gapPatterns = new[]
            {
                @"(?:missing|incomplete|not implemented|TODO|needs?):\s*(.+?)(?:\.|$)",
                @"(?:gap|issue|problem):\s*(.+?)(?:\.|$)"
            };

            foreach (var pattern in gapPatterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    output, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var item = match.Groups[1].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(item) && item.Length < 200 && !memory.RemainingGaps.Contains(item))
                            memory.RemainingGaps.Add(item);
                    }
                }
            }

            memory.RemainingGaps = memory.RemainingGaps.Take(10).ToList();
        }

        private ProgressMetrics CalculateProgressMetrics(string output)
        {
            var metrics = new ProgressMetrics();

            // Count various completion indicators
            var completedCount = System.Text.RegularExpressions.Regex.Matches(
                output, @"(?:completed|finished|done|implemented)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

            var totalCount = System.Text.RegularExpressions.Regex.Matches(
                output, @"(?:step|task|item)\s+\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

            if (totalCount > 0)
            {
                metrics.CompletionPercentage = (int)((double)completedCount / totalCount * 100);
            }

            // Extract metrics from output
            ExtractMetricsFromOutput(output, metrics);

            return metrics;
        }

        private void ExtractMetricsFromOutput(string output, ProgressMetrics metrics)
        {
            // Look for specific metrics in output
            var metricsPatterns = new Dictionary<string, string>
            {
                [@"files?\s+(?:modified|changed):\s*(\d+)"] = "FilesModified",
                [@"lines?\s+(?:added|inserted):\s*(\d+)"] = "LinesAdded",
                [@"lines?\s+(?:removed|deleted):\s*(\d+)"] = "LinesRemoved",
                [@"tests?\s+(?:passed|passing):\s*(\d+)"] = "TestsPassed",
                [@"tests?\s+(?:failed|failing):\s*(\d+)"] = "TestsFailed",
                [@"errors?\s+(?:resolved|fixed):\s*(\d+)"] = "ErrorsResolved"
            };

            foreach (var kvp in metricsPatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(output, kvp.Key,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var value))
                {
                    switch (kvp.Value)
                    {
                        case "FilesModified": metrics.FilesModified = value; break;
                        case "LinesAdded": metrics.LinesAdded = value; break;
                        case "LinesRemoved": metrics.LinesRemoved = value; break;
                        case "TestsPassed": metrics.TestsPassed = value; break;
                        case "TestsFailed": metrics.TestsFailed = value; break;
                        case "ErrorsResolved": metrics.ErrorsResolved = value; break;
                    }
                }
            }

            // Extract blocking issues
            var blockingPattern = @"(?:blocked|blocking|waiting for):\s*(.+?)(?:\.|$)";
            var blockingMatches = System.Text.RegularExpressions.Regex.Matches(output, blockingPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in blockingMatches)
            {
                if (match.Groups.Count > 1)
                    metrics.BlockingIssues.Add(match.Groups[1].Value.Trim());
            }
        }

        private List<string> AnalyzePatterns(List<IterationMemory> memories)
        {
            var patterns = new List<string>();

            // Check for repeated failures
            var failureCounts = memories
                .SelectMany(m => m.FailedApproaches)
                .GroupBy(f => f.Approach.ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => $"'{g.Key}' failed {g.Count()} times");

            patterns.AddRange(failureCounts);

            // Check for stalled progress
            if (memories.Count >= 3)
            {
                var lastThree = memories.TakeLast(3).ToList();
                var avgProgress = lastThree.Average(m => m.ProgressMetrics?.CompletionPercentage ?? 0);
                if (avgProgress < 20)
                {
                    patterns.Add("Progress appears stalled (< 20% completion over last 3 iterations)");
                }

                // Check for recurring errors
                var recurringIssues = lastThree
                    .SelectMany(m => m.ProgressMetrics?.BlockingIssues ?? new List<string>())
                    .GroupBy(i => i.ToLowerInvariant())
                    .Where(g => g.Count() >= 2)
                    .Select(g => $"Recurring issue: {g.Key}");

                patterns.AddRange(recurringIssues);
            }

            return patterns;
        }

        private string GetTaskDirectory(string taskId)
        {
            var dir = Path.Combine(_memoryBasePath, taskId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string DetermineDiscoveryType(string text)
        {
            if (text.Contains("architecture", StringComparison.OrdinalIgnoreCase))
                return "Architecture";
            if (text.Contains("pattern", StringComparison.OrdinalIgnoreCase))
                return "Pattern";
            if (text.Contains("api", StringComparison.OrdinalIgnoreCase))
                return "API";
            if (text.Contains("dependency", StringComparison.OrdinalIgnoreCase))
                return "Dependency";
            return "General";
        }

        private string ExtractApproachName(string text)
        {
            // Try to extract a concise approach name
            var words = text.Split(' ').Take(5);
            return string.Join(" ", words);
        }

        private string DetermineFilePurpose(string filePath, string context)
        {
            // Infer purpose from file name and context
            var fileName = Path.GetFileName(filePath).ToLowerInvariant();

            if (fileName.Contains("test")) return "Test file";
            if (fileName.Contains("config")) return "Configuration";
            if (fileName.Contains("manager")) return "Manager/Service";
            if (fileName.Contains("model")) return "Data model";
            if (fileName.Contains("interface")) return "Interface definition";

            // Check context around file mention
            var fileIndex = context.IndexOf(filePath, StringComparison.OrdinalIgnoreCase);
            if (fileIndex > 0)
            {
                var contextWindow = context.Substring(Math.Max(0, fileIndex - 50), Math.Min(100, context.Length - fileIndex));
                if (contextWindow.Contains("implement", StringComparison.OrdinalIgnoreCase))
                    return "Implementation";
                if (contextWindow.Contains("fix", StringComparison.OrdinalIgnoreCase))
                    return "Bug fix target";
            }

            return "Project file";
        }

        /// <summary>
        /// Cleans up old iteration memories older than the specified age.
        /// </summary>
        public void CleanupOldMemories(TimeSpan maxAge)
        {
            var cutoffDate = DateTime.Now - maxAge;
            var tasksToClean = new List<string>();

            lock (_lock)
            {
                foreach (var taskDir in Directory.GetDirectories(_memoryBasePath))
                {
                    var dirInfo = new DirectoryInfo(taskDir);
                    if (dirInfo.LastWriteTime < cutoffDate)
                    {
                        tasksToClean.Add(taskDir);
                    }
                }

                foreach (var dir in tasksToClean)
                {
                    try
                    {
                        Directory.Delete(dir, true);
                        AppLogger.Debug("IterationMemory", $"Cleaned up old memory: {Path.GetFileName(dir)}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("IterationMemory", $"Failed to clean up {dir}", ex);
                    }
                }
            }
        }
    }

    // Data structures
    public class IterationMemory
    {
        public int Iteration { get; set; }
        public DateTime Timestamp { get; set; }
        public List<Discovery> KeyDiscoveries { get; set; } = new();
        public List<FailedApproach> FailedApproaches { get; set; } = new();
        public List<FileReference> ImportantFiles { get; set; } = new();
        public List<string> SuccessfulWork { get; set; } = new();
        public List<string> RemainingGaps { get; set; } = new();
        public ProgressMetrics? ProgressMetrics { get; set; }
    }

    public class Discovery
    {
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Impact { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FailedApproach
    {
        public string Approach { get; set; } = "";
        public string Reason { get; set; } = "";
        public string? ErrorDetails { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FileReference
    {
        public string Path { get; set; } = "";
        public string Purpose { get; set; } = "";
        public DateTime LastModified { get; set; }
    }

    public class ProgressMetrics
    {
        public int CompletionPercentage { get; set; }
        public int FilesModified { get; set; }
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public int TestsPassed { get; set; }
        public int TestsFailed { get; set; }
        public int ErrorsResolved { get; set; }
        public List<string> BlockingIssues { get; set; } = new();
    }
}