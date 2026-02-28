using System;

namespace AgenticEngine.Models
{
    internal class SavedPromptEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string PromptText { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Model { get; set; } = "ClaudeCode";
        public bool RemoteSession { get; set; }
        public bool Headless { get; set; }
        public bool SpawnTeam { get; set; }
        public bool Overnight { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool PlanOnly { get; set; }
        public bool IgnoreFileLocks { get; set; } = true;
        public bool UseMcp { get; set; }
        public bool NoGitWrite { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
