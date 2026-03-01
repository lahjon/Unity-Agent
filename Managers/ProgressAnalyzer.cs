using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Analyzes task progress to detect stalls and provide insights for early termination decisions.
    /// </summary>
    public class ProgressAnalyzer
    {
        private readonly Dictionary<string, List<TaskProgressMetrics>> _progressHistory = new();
        private readonly object _lock = new();

        // Thresholds for stall detection
        private const int MinIterationsForAnalysis = 2;
        private const int StallIterationThreshold = 3;
        private const double MinProgressPerIteration = 10.0;
        private const double MaxTokenBurnRate = 50000; // tokens per 10% progress

        /// <summary>
        /// Records progress metrics for a task iteration.
        /// </summary>
        public void RecordProgress(string taskId, TaskProgressMetrics metrics)
        {
            lock (_lock)
            {
                if (!_progressHistory.ContainsKey(taskId))
                    _progressHistory[taskId] = new List<TaskProgressMetrics>();

                _progressHistory[taskId].Add(metrics);

                // Keep only last 10 iterations
                if (_progressHistory[taskId].Count > 10)
                    _progressHistory[taskId].RemoveAt(0);
            }
        }

        /// <summary>
        /// Analyzes task output to extract progress metrics.
        /// </summary>
        public TaskProgressMetrics ExtractMetricsFromOutput(string taskId, string output, int iteration)
        {
            var metrics = new TaskProgressMetrics
            {
                TaskId = taskId,
                Iteration = iteration,
                Timestamp = DateTime.Now
            };

            ExtractFileMetrics(output, metrics);
            ExtractCodeMetrics(output, metrics);
            ExtractTestMetrics(output, metrics);
            ExtractErrorMetrics(output, metrics);
            ExtractCompletionMetrics(output, metrics);

            return metrics;
        }

        /// <summary>
        /// Detects if a task is stalled based on progress history.
        /// </summary>
        public StallDetectionResult DetectStall(string taskId)
        {
            lock (_lock)
            {
                if (!_progressHistory.TryGetValue(taskId, out var history) ||
                    history.Count < MinIterationsForAnalysis)
                {
                    return new StallDetectionResult { IsStalled = false };
                }

                var recentMetrics = history.TakeLast(StallIterationThreshold).ToList();
                var conditions = new List<StallCondition>();

                // Check various stall conditions
                CheckFileActivityStall(recentMetrics, conditions);
                CheckErrorLoopStall(recentMetrics, conditions);
                CheckTestFailureStall(recentMetrics, conditions);
                CheckProductivityStall(recentMetrics, conditions);
                CheckTokenBurnStall(recentMetrics, conditions);
                CheckCompletionPlateauStall(recentMetrics, conditions);

                // Determine if stalled based on conditions
                var isStalled = conditions.Any(c => c.Confidence > 0.7) ||
                               conditions.Count(c => c.Confidence > 0.5) >= 2;

                return new StallDetectionResult
                {
                    IsStalled = isStalled,
                    Conditions = conditions,
                    Recommendation = GenerateRecommendation(conditions),
                    ConfidenceScore = conditions.Any() ? conditions.Max(c => c.Confidence) : 0
                };
            }
        }

        /// <summary>
        /// Calculates productivity velocity over recent iterations.
        /// </summary>
        public VelocityAnalysis CalculateVelocity(string taskId)
        {
            lock (_lock)
            {
                if (!_progressHistory.TryGetValue(taskId, out var history) || history.Count < 2)
                {
                    return new VelocityAnalysis { HasSufficientData = false };
                }

                var recent = history.TakeLast(5).ToList();
                var velocities = new List<double>();

                for (int i = 1; i < recent.Count; i++)
                {
                    var prev = recent[i - 1];
                    var curr = recent[i];
                    var comparison = curr.CompareWith(prev);

                    // Calculate velocity as progress per iteration
                    var velocity = comparison.CompletionDelta;
                    if (velocity <= 0)
                    {
                        // Use alternative metrics if completion isn't increasing
                        velocity = (comparison.FilesChangedDelta * 5) +
                                 (comparison.TestsDelta * 3) -
                                 (comparison.ErrorsDelta * 10);
                    }

                    velocities.Add(velocity);
                }

                return new VelocityAnalysis
                {
                    HasSufficientData = true,
                    AverageVelocity = velocities.Average(),
                    VelocityTrend = CalculateTrend(velocities),
                    EstimatedIterationsRemaining = EstimateRemainingIterations(recent.Last(), velocities.Average())
                };
            }
        }

        private void ExtractFileMetrics(string output, TaskProgressMetrics metrics)
        {
            // File creation/modification patterns
            var filePatterns = new[]
            {
                @"(?:Created?|Added?)\s+(?:file\s+)?[`']([^`']+)[`']",
                @"(?:Modified?|Updated?|Changed?)\s+(?:file\s+)?[`']([^`']+)[`']",
                @"(?:Deleted?|Removed?)\s+(?:file\s+)?[`']([^`']+)[`']",
                @"Writing to\s+[`']([^`']+)[`']",
                @"File\s+([^\s]+)\s+(?:created|modified|updated)"
            };

            foreach (var pattern in filePatterns)
            {
                var matches = Regex.Matches(output, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var file = match.Groups[1].Value;
                        metrics.ModifiedFiles.Add(file);

                        if (pattern.Contains("Created") || pattern.Contains("Added"))
                            metrics.FilesCreated++;
                        else if (pattern.Contains("Deleted") || pattern.Contains("Removed"))
                            metrics.FilesDeleted++;
                        else
                            metrics.FilesModified++;
                    }
                }
            }
        }

        private void ExtractCodeMetrics(string output, TaskProgressMetrics metrics)
        {
            // Line change patterns
            var linePatterns = new Dictionary<string, string>
            {
                [@"(\d+)\s+(?:lines?\s+)?added"] = "added",
                [@"(\d+)\s+(?:lines?\s+)?removed"] = "removed",
                [@"(\d+)\s+(?:lines?\s+)?deleted"] = "removed",
                [@"\+(\d+).*\-(\d+)"] = "diff"
            };

            foreach (var kvp in linePatterns)
            {
                var matches = Regex.Matches(output, kvp.Key, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (kvp.Value == "diff" && match.Groups.Count > 2)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var added))
                            metrics.LinesAdded += added;
                        if (int.TryParse(match.Groups[2].Value, out var removed))
                            metrics.LinesRemoved += removed;
                    }
                    else if (match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out var count))
                    {
                        if (kvp.Value == "added")
                            metrics.LinesAdded += count;
                        else
                            metrics.LinesRemoved += count;
                    }
                }
            }
        }

        private void ExtractTestMetrics(string output, TaskProgressMetrics metrics)
        {
            var testPatterns = new Dictionary<string, string>
            {
                [@"(\d+)\s+(?:tests?\s+)?passed"] = "passed",
                [@"(\d+)\s+(?:tests?\s+)?failed"] = "failed",
                [@"Tests:\s*(\d+)\s+passed,\s*(\d+)\s+failed"] = "summary",
                [@"All\s+(\d+)\s+tests?\s+passed"] = "all_passed"
            };

            foreach (var kvp in testPatterns)
            {
                var matches = Regex.Matches(output, kvp.Key, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (kvp.Value == "summary" && match.Groups.Count > 2)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var passed))
                            metrics.TestsPassed = Math.Max(metrics.TestsPassed, passed);
                        if (int.TryParse(match.Groups[2].Value, out var failed))
                            metrics.TestsFailed = Math.Max(metrics.TestsFailed, failed);
                    }
                    else if (match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out var count))
                    {
                        if (kvp.Value == "passed" || kvp.Value == "all_passed")
                            metrics.TestsPassed = Math.Max(metrics.TestsPassed, count);
                        else if (kvp.Value == "failed")
                            metrics.TestsFailed = Math.Max(metrics.TestsFailed, count);
                    }
                }
            }
        }

        private void ExtractErrorMetrics(string output, TaskProgressMetrics metrics)
        {
            // Error patterns
            var errorIntroduced = Regex.Matches(output,
                @"(?:error|exception|failure):\s*(.+)", RegexOptions.IgnoreCase).Count;

            var errorResolved = Regex.Matches(output,
                @"(?:fixed|resolved|corrected)\s+(?:the\s+)?(?:error|issue|problem|bug)",
                RegexOptions.IgnoreCase).Count;

            metrics.ErrorsIntroduced = errorIntroduced;
            metrics.ErrorsResolved = errorResolved;

            // Extract error types
            var errorTypePattern = @"(\w+(?:Error|Exception))";
            var errorMatches = Regex.Matches(output, errorTypePattern);
            foreach (Match match in errorMatches)
            {
                metrics.ErrorTypes.Add(match.Groups[1].Value);
            }
        }

        private void ExtractCompletionMetrics(string output, TaskProgressMetrics metrics)
        {
            // Completion patterns
            var completionPatterns = new[]
            {
                @"(?:Step|Task)\s+(\d+)\s+of\s+(\d+)\s+(?:complete|done)",
                @"(\d+)/(\d+)\s+(?:steps?|tasks?)\s+(?:complete|done)",
                @"Progress:\s*(\d+)%",
                @"(\d+)%\s+(?:complete|done)"
            };

            foreach (var pattern in completionPatterns)
            {
                var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (match.Groups.Count > 2)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var completed) &&
                            int.TryParse(match.Groups[2].Value, out var total))
                        {
                            metrics.StepsCompleted = completed;
                            metrics.StepsTotal = total;
                            break;
                        }
                    }
                    else if (match.Groups.Count > 1)
                    {
                        if (int.TryParse(match.Groups[1].Value, out var percentage))
                        {
                            metrics.StepsCompleted = percentage;
                            metrics.StepsTotal = 100;
                            break;
                        }
                    }
                }
            }
        }

        private void CheckFileActivityStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var noFileChanges = metrics.All(m => m.FilesCreated == 0 && m.FilesModified == 0);
            if (noFileChanges)
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.NoFileChanges,
                    Description = "No files have been modified in recent iterations",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.8,
                    Evidence = new List<string> { $"Last {metrics.Count} iterations show no file activity" }
                });
            }
        }

        private void CheckErrorLoopStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var recurringErrors = metrics
                .SelectMany(m => m.ErrorTypes)
                .GroupBy(e => e)
                .Where(g => g.Count() >= metrics.Count - 1)
                .Select(g => g.Key)
                .ToList();

            if (recurringErrors.Any())
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.RecurringErrors,
                    Description = "Same errors appearing repeatedly",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.9,
                    Evidence = recurringErrors.Select(e => $"Recurring error: {e}").ToList()
                });
            }
        }

        private void CheckTestFailureStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var testFailureRate = metrics.Where(m => m.TestsTotal > 0).Select(m => m.TestPassRate);
            if (testFailureRate.Any() && testFailureRate.All(r => r < 0.5))
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.TestFailureLoop,
                    Description = "Persistent test failures",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.7,
                    Evidence = new List<string>
                    {
                        $"Average test pass rate: {testFailureRate.Average():P0}",
                        $"Total failed tests: {metrics.Sum(m => m.TestsFailed)}"
                    }
                });
            }
        }

        private void CheckProductivityStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var avgProgress = metrics.Average(m => m.CalculateProgressScore());
            if (avgProgress < MinProgressPerIteration)
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.LowProductivity,
                    Description = "Low productivity across iterations",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.6,
                    Evidence = new List<string>
                    {
                        $"Average progress score: {avgProgress:F1}%",
                        $"Expected minimum: {MinProgressPerIteration}%"
                    }
                });
            }
        }

        private void CheckTokenBurnStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var totalTokens = metrics.Sum(m => m.TokensUsed);
            var totalProgress = metrics.Last().CompletionPercentage - metrics.First().CompletionPercentage;

            if (totalProgress > 0 && totalTokens / totalProgress > MaxTokenBurnRate)
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.TokenBurnWithoutProgress,
                    Description = "High token consumption without proportional progress",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.8,
                    Evidence = new List<string>
                    {
                        $"Tokens used: {totalTokens:N0}",
                        $"Progress gained: {totalProgress:F1}%",
                        $"Token burn rate: {totalTokens / totalProgress:N0} tokens per 1% progress"
                    }
                });
            }
        }

        private void CheckCompletionPlateauStall(List<TaskProgressMetrics> metrics, List<StallCondition> conditions)
        {
            var completions = metrics.Select(m => m.CompletionPercentage).ToList();
            var isPlateaued = true;

            for (int i = 1; i < completions.Count; i++)
            {
                if (Math.Abs(completions[i] - completions[i - 1]) > 5)
                {
                    isPlateaued = false;
                    break;
                }
            }

            if (isPlateaued && completions.Any())
            {
                conditions.Add(new StallCondition
                {
                    Type = StallType.CompletionPlateau,
                    Description = "Completion percentage has plateaued",
                    ConsecutiveIterations = metrics.Count,
                    Confidence = 0.7,
                    Evidence = new List<string>
                    {
                        $"Stuck at ~{completions.Average():F0}% completion",
                        $"No significant progress in {metrics.Count} iterations"
                    }
                });
            }
        }

        private VelocityTrend CalculateTrend(List<double> velocities)
        {
            if (velocities.Count < 2)
                return VelocityTrend.Stable;

            var firstHalf = velocities.Take(velocities.Count / 2).Average();
            var secondHalf = velocities.Skip(velocities.Count / 2).Average();

            if (secondHalf > firstHalf * 1.2)
                return VelocityTrend.Accelerating;
            if (secondHalf < firstHalf * 0.8)
                return VelocityTrend.Decelerating;

            return VelocityTrend.Stable;
        }

        private int EstimateRemainingIterations(TaskProgressMetrics current, double avgVelocity)
        {
            if (avgVelocity <= 0 || current.CompletionPercentage >= 100)
                return 0;

            var remaining = 100 - current.CompletionPercentage;
            return (int)Math.Ceiling(remaining / avgVelocity);
        }

        private string GenerateRecommendation(List<StallCondition> conditions)
        {
            if (!conditions.Any())
                return "Continue with current approach";

            var highConfidence = conditions.Where(c => c.Confidence > 0.7).ToList();
            if (highConfidence.Any(c => c.Type == StallType.RecurringErrors))
                return "Consider terminating - task is stuck in an error loop";

            if (highConfidence.Any(c => c.Type == StallType.TokenBurnWithoutProgress))
                return "Consider terminating - excessive token usage without progress";

            if (highConfidence.Count >= 2)
                return "Consider terminating - multiple stall indicators detected";

            return "Monitor closely - potential stall detected";
        }

        /// <summary>
        /// Clears progress history for a task.
        /// </summary>
        public void ClearTaskHistory(string taskId)
        {
            lock (_lock)
            {
                _progressHistory.Remove(taskId);
            }
        }
    }

    // Result structures
    public class StallDetectionResult
    {
        public bool IsStalled { get; set; }
        public List<StallCondition> Conditions { get; set; } = new();
        public string Recommendation { get; set; } = "";
        public double ConfidenceScore { get; set; }
    }

    public class VelocityAnalysis
    {
        public bool HasSufficientData { get; set; }
        public double AverageVelocity { get; set; }
        public VelocityTrend VelocityTrend { get; set; }
        public int EstimatedIterationsRemaining { get; set; }
    }

    public enum VelocityTrend
    {
        Accelerating,
        Stable,
        Decelerating
    }
}