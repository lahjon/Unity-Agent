using System;

namespace AgenticEngine.Models
{
    public class TaskTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool SkipPermissions { get; set; } = true;
        public bool RemoteSession { get; set; }
        public bool Headless { get; set; }
        public bool IsOvernight { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool NoGitWrite { get; set; } = true;
        public bool PlanOnly { get; set; }
        public bool UseMessageBus { get; set; }
        public bool SpawnTeam { get; set; }
        public bool IgnoreFileLocks { get; set; } = true;
        public bool UseMcp { get; set; }
        public int MaxIterations { get; set; }
        public string AdditionalInstructions { get; set; } = "";
        public string Model { get; set; } = "ClaudeCode";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
