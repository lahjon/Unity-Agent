using System;
using System.Collections.Generic;

namespace AgenticEngine
{
    /// <summary>
    /// Pure persistent data for an agent task. Safe to serialize and pass across
    /// boundaries without dragging along runtime process state.
    /// </summary>
    public class AgentTaskData
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..16];
        public int TaskNumber { get; set; }
        public string Description { get; set; } = "";
        public DateTime StartTime { get; set; } = DateTime.Now;
        public bool SkipPermissions { get; set; }
        public bool RemoteSession { get; set; }
        public bool Headless { get; set; }
        public bool IsOvernight { get; set; }
        public bool IgnoreFileLocks { get; set; }
        public bool UseMcp { get; set; }
        public bool SpawnTeam { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool NoGitWrite { get; set; }
        public bool PlanOnly { get; set; }
        public bool UseMessageBus { get; set; }
        public bool AutoDecompose { get; set; }
        public string AdditionalInstructions { get; set; } = "";
        public string? StoredPrompt { get; set; }
        public string? ConversationId { get; set; }
        public string? FullOutput { get; set; }
        public ModelType Model { get; set; } = ModelType.ClaudeCode;
        public int MaxIterations { get; set; } = 50;
        public int CurrentIteration { get; set; }
        public string ProjectPath { get; set; } = "";
        public string ProjectColor { get; set; } = "#666666";
        public string ProjectDisplayName { get; set; } = "";
        public List<string> ImagePaths { get; set; } = new();
        public List<string> GeneratedImagePaths { get; set; } = new();
        public string CompletionSummary { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public string ContinueReason { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? DependencyContext { get; set; }
        public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Running;
        public DateTime? EndTime { get; set; }
        public string? GitStartHash { get; set; }

        /// <summary>Scheduling priority. Higher values run sooner among peer tasks at the same DAG depth.</summary>
        public int Priority { get; set; }

        // Parent-child task hierarchy
        public string? ParentTaskId { get; set; }
        public List<string> ChildTaskIds { get; set; } = new();

        // Task group tracking
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }

        // Token usage tracking
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
    }
}
