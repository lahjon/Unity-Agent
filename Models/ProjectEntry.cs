using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HappyEngine.Models
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
        public string McpServerName { get; set; } = "mcp-for-unity-server";
        public string McpAddress { get; set; } = "http://127.0.0.1:8080/mcp";
        public string McpStartCommand { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string LongDescription { get; set; } = "";
        public string RuleInstruction { get; set; } = "";
        public List<string> ProjectRules { get; set; } = new();
        public string Color { get; set; } = "";
        public bool IsGame { get; set; }
        public List<string> CrashLogPaths { get; set; } = new();

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
