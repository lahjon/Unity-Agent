using System.Text.Json.Serialization;

namespace UnityAgent.Models
{
    public enum McpStatus
    {
        Disabled,
        Initialized,
        Investigating,
        Enabled
    }

    public class ProjectEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public McpStatus McpStatus { get; set; } = McpStatus.Disabled;
        public string ShortDescription { get; set; } = "";
        public string LongDescription { get; set; } = "";

        [JsonIgnore]
        public bool IsInitializing { get; set; }
        [JsonIgnore]
        public string FolderName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderName : Name;
        [JsonIgnore]
        public bool IsInitialized =>
            !string.IsNullOrEmpty(Path) &&
            (System.IO.Directory.Exists(System.IO.Path.Combine(Path, ".claude")) ||
             System.IO.File.Exists(System.IO.Path.Combine(Path, "CLAUDE.md")));
    }
}
