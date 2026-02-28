using System;

namespace UnityAgent.Models
{
    internal class TaskHistoryEntry
    {
        public string Description { get; set; } = "";
        public string Summary { get; set; } = "";
        public string StoredPrompt { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool SkipPermissions { get; set; }
        public bool RemoteSession { get; set; }
        public string? ProjectPath { get; set; }
        public string ProjectColor { get; set; } = "#666666";
        public bool IsOvernight { get; set; }
        public int MaxIterations { get; set; }
        public int CurrentIteration { get; set; }
        public string CompletionSummary { get; set; } = "";
    }
}
