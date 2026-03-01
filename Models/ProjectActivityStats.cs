using System;
using System.Collections.Generic;

namespace HappyEngine.Models
{
    public class ProjectActivityStats
    {
        public string ProjectPath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string ProjectColor { get; set; } = "#666666";

        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int FailedTasks { get; set; }
        public int CancelledTasks { get; set; }
        public int RunningTasks { get; set; }

        public double SuccessRate { get; set; }
        public double FailureRate { get; set; }

        public TimeSpan AverageDuration { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public TimeSpan? ShortestDuration { get; set; }
        public TimeSpan? LongestDuration { get; set; }

        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TotalTokens => TotalInputTokens + TotalOutputTokens;
        public DateTime? MostRecentTaskTime { get; set; }

        public List<SparklinePoint> RecentActivity { get; set; } = new();
    }

    public class SparklinePoint
    {
        public DateTime Timestamp { get; set; }
        public bool Succeeded { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
