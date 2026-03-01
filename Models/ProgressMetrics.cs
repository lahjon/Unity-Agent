using System;
using System.Collections.Generic;

namespace HappyEngine.Models
{
    /// <summary>
    /// Represents progress metrics for a task or iteration.
    /// Used for tracking advancement and detecting stalls.
    /// </summary>
    public class TaskProgressMetrics
    {
        public string TaskId { get; set; } = "";
        public int Iteration { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // File-based metrics
        public int FilesCreated { get; set; }
        public int FilesModified { get; set; }
        public int FilesDeleted { get; set; }
        public HashSet<string> ModifiedFiles { get; set; } = new();

        // Code metrics
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public int NetLinesChanged => LinesAdded - LinesRemoved;

        // Test metrics
        public int TestsAdded { get; set; }
        public int TestsPassed { get; set; }
        public int TestsFailed { get; set; }
        public int TestsTotal => TestsPassed + TestsFailed;
        public double TestPassRate => TestsTotal > 0 ? (double)TestsPassed / TestsTotal : 0;

        // Error metrics
        public int ErrorsIntroduced { get; set; }
        public int ErrorsResolved { get; set; }
        public int NetErrorChange => ErrorsIntroduced - ErrorsResolved;
        public List<string> ErrorTypes { get; set; } = new();

        // Implementation progress
        public int StepsCompleted { get; set; }
        public int StepsTotal { get; set; }
        public double CompletionPercentage => StepsTotal > 0 ? (double)StepsCompleted / StepsTotal * 100 : 0;

        // Token usage
        public long TokensUsed { get; set; }
        public double EstimatedCost { get; set; }

        // Velocity metrics
        public double ProductivityScore { get; set; }
        public TimeSpan ElapsedTime { get; set; }

        // Stall indicators
        public bool IsStalled { get; set; }
        public string? StallReason { get; set; }
        public List<string> BlockingIssues { get; set; } = new();

        /// <summary>
        /// Calculates a composite progress score (0-100).
        /// </summary>
        public double CalculateProgressScore()
        {
            double score = 0;

            // Base score from completion percentage (40% weight)
            score += CompletionPercentage * 0.4;

            // File activity (20% weight)
            var fileActivity = Math.Min(10, FilesCreated + FilesModified) / 10.0;
            score += fileActivity * 20;

            // Test health (20% weight)
            score += TestPassRate * 20;

            // Error resolution (20% weight)
            var errorScore = NetErrorChange <= 0 ? 1.0 : Math.Max(0, 1.0 - (NetErrorChange / 5.0));
            score += errorScore * 20;

            return Math.Min(100, Math.Max(0, score));
        }

        /// <summary>
        /// Compares this metrics snapshot with a previous one to detect progress.
        /// </summary>
        public ProgressComparison CompareWith(TaskProgressMetrics previous)
        {
            return new ProgressComparison
            {
                FilesChangedDelta = (FilesCreated + FilesModified) - (previous.FilesCreated + previous.FilesModified),
                LinesChangedDelta = NetLinesChanged - previous.NetLinesChanged,
                ErrorsDelta = NetErrorChange - previous.NetErrorChange,
                TestsDelta = TestsTotal - previous.TestsTotal,
                CompletionDelta = CompletionPercentage - previous.CompletionPercentage,
                TokensDelta = TokensUsed - previous.TokensUsed,
                IsProgressing = IsProgressingComparedTo(previous)
            };
        }

        private bool IsProgressingComparedTo(TaskProgressMetrics previous)
        {
            // Progress if any of these are true:
            // 1. Completion percentage increased
            if (CompletionPercentage > previous.CompletionPercentage + 5)
                return true;

            // 2. Files were modified
            if (FilesModified > previous.FilesModified || FilesCreated > previous.FilesCreated)
                return true;

            // 3. Tests improved
            if (TestPassRate > previous.TestPassRate + 0.1)
                return true;

            // 4. Errors were resolved
            if (ErrorsResolved > previous.ErrorsResolved && NetErrorChange <= previous.NetErrorChange)
                return true;

            return false;
        }
    }

    public class ProgressComparison
    {
        public int FilesChangedDelta { get; set; }
        public int LinesChangedDelta { get; set; }
        public int ErrorsDelta { get; set; }
        public int TestsDelta { get; set; }
        public double CompletionDelta { get; set; }
        public long TokensDelta { get; set; }
        public bool IsProgressing { get; set; }
    }

    /// <summary>
    /// Represents a stall condition detected in task progress.
    /// </summary>
    public class StallCondition
    {
        public StallType Type { get; set; }
        public string Description { get; set; } = "";
        public int ConsecutiveIterations { get; set; }
        public double Confidence { get; set; }
        public DateTime DetectedAt { get; set; } = DateTime.Now;
        public List<string> Evidence { get; set; } = new();
    }

    public enum StallType
    {
        NoFileChanges,
        RecurringErrors,
        TestFailureLoop,
        LowProductivity,
        TokenBurnWithoutProgress,
        CompletionPlateau
    }
}