using System;

namespace UnityAgent.Models
{
    internal class StoredTaskEntry
    {
        public string Description { get; set; } = "";
        public string Summary { get; set; } = "";
        public string StoredPrompt { get; set; } = "";
        public string FullOutput { get; set; } = "";
        public string? ProjectPath { get; set; }
        public string ProjectColor { get; set; } = "#666666";
        public DateTime CreatedAt { get; set; }
        public bool SkipPermissions { get; set; }
    }
}
