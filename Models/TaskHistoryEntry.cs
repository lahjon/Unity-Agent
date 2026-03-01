using System;

namespace HappyEngine.Models
{
    internal class TaskHistoryEntry : TaskRecordBase
    {
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool RemoteSession { get; set; }
        public bool IsOvernight { get; set; }
        public int MaxIterations { get; set; }
        public int CurrentIteration { get; set; }
        public string CompletionSummary { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
    }
}
