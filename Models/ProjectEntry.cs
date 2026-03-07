using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spritely.Models
{
    public enum McpStatus
    {
        Disabled,
        NotConnected,
        Connecting,
        Connected,
        Failed,
        Initialized,
        Investigating,
        Enabled
    }

    public class ProjectEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public McpStatus McpStatus { get; set; } = McpStatus.NotConnected;
        public string McpServerName { get; set; } = "mcp-for-unity-server";
        public string McpAddress { get; set; } = "http://127.0.0.1:8080/mcp";
        public string McpStartCommand { get; set; } = @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools";
        public string ShortDescription { get; set; } = "";
        public string LongDescription { get; set; } = "";
        public string RuleInstruction { get; set; } = "";
        public List<string> ProjectRules { get; set; } = new();
        public string Color { get; set; } = "";
        public bool IsGame { get; set; }
        public string CrashLogPath { get; set; } = "";
        public string AppLogPath { get; set; } = "";
        public string HangLogPath { get; set; } = "";

        [JsonIgnore]
        public bool IsInitializing { get; set; }
        [JsonIgnore]
        public int McpProcessId { get; set; } // Track the MCP server process
        [JsonIgnore]
        public System.Text.StringBuilder McpOutput { get; } = new System.Text.StringBuilder();
        [JsonIgnore]
        public System.Diagnostics.Process? McpProcess { get; set; }
        [JsonIgnore]
        public string FolderName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderName : Name;
        [JsonIgnore]
        public bool IsInitialized =>
            !string.IsNullOrEmpty(Path) &&
            (System.IO.Directory.Exists(System.IO.Path.Combine(Path, ".claude")) ||
             System.IO.File.Exists(System.IO.Path.Combine(Path, "CLAUDE.md")));

        [JsonIgnore]
        public bool IsFeatureRegistryInitialized =>
            !string.IsNullOrEmpty(Path) &&
            System.IO.File.Exists(System.IO.Path.Combine(Path, ".spritely", "features", "_index.json"));
    }
}
