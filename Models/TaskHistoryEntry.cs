using System;

namespace HappyEngine.Models
{
    internal class TaskHistoryEntry : TaskRecordBase
    {
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool RemoteSession { get; set; }
        public bool IsFeatureMode { get; set; }
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

        // Recovery fields — set when persisting active queued/planning tasks on shutdown
        public bool WasActive { get; set; }
        public string Model { get; set; } = "";
        public bool Headless { get; set; }
        public bool IgnoreFileLocks { get; set; }
        public bool UseMcp { get; set; }
        public bool SpawnTeam { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool NoGitWrite { get; set; }
        public bool PlanOnly { get; set; }
        public bool UseMessageBus { get; set; }
        public bool AutoDecompose { get; set; }
        public bool ApplyFix { get; set; } = true;
        public string AdditionalInstructions { get; set; } = "";
    }
}
