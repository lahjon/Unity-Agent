using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace HappyEngine
{
    public enum AgentTaskStatus
    {
        Running,
        Completed,
        Cancelled,
        Failed,
        Queued,
        Paused,
        InitQueued,
        Planning,
        Verifying
    }

    public enum TaskPriority
    {
        Low,
        Normal,
        High,
        Critical
    }

    public enum ModelType
    {
        ClaudeCode,
        Gemini,
        GeminiGameArt
    }

    public enum FeatureModePhase
    {
        None = 0,
        TeamPlanning = 1,
        PlanConsolidation = 2,
        Execution = 3,
        Evaluation = 4
    }

    public class AgentTask : INotifyPropertyChanged
    {
        /// <summary>Persistent task data, safe for serialization and cross-boundary passing.</summary>
        public AgentTaskData Data { get; } = new();

        /// <summary>Transient runtime state (process, timers, output buffer). Not persisted.</summary>
        public RuntimeTaskContext Runtime { get; } = new();

        // ── Persistent data delegation ────────────────────────────────

        public string Id => Data.Id;
        public int TaskNumber { get => Data.TaskNumber; set => Data.TaskNumber = value; }
        public string Description { get => Data.Description; set => Data.Description = value; }
        public DateTime StartTime { get => Data.StartTime; set => Data.StartTime = value; }
        public bool SkipPermissions { get => Data.SkipPermissions; set => Data.SkipPermissions = value; }
        public bool RemoteSession { get => Data.RemoteSession; set => Data.RemoteSession = value; }
        public bool Headless { get => Data.Headless; set => Data.Headless = value; }
        public bool IsFeatureMode { get => Data.IsFeatureMode; set => Data.IsFeatureMode = value; }
        public bool IgnoreFileLocks
        {
            get => Data.IgnoreFileLocks;
            set { Data.IgnoreFileLocks = value; OnPropertyChanged(); }
        }
        public bool UseMcp { get => Data.UseMcp; set => Data.UseMcp = value; }
        public bool SpawnTeam { get => Data.SpawnTeam; set => Data.SpawnTeam = value; }
        public bool ExtendedPlanning { get => Data.ExtendedPlanning; set => Data.ExtendedPlanning = value; }
        public bool NoGitWrite { get => Data.NoGitWrite; set => Data.NoGitWrite = value; }
        public bool PlanOnly { get => Data.PlanOnly; set => Data.PlanOnly = value; }
        public int Priority
        {
            get => Data.Priority;
            set { Data.Priority = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPriorityBadge)); OnPropertyChanged(nameof(PriorityBadgeText)); }
        }
        public TaskPriority PriorityLevel
        {
            get => Data.PriorityLevel;
            set
            {
                Data.PriorityLevel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPriorityBadge));
                OnPropertyChanged(nameof(PriorityBadgeText));
                OnPropertyChanged(nameof(PriorityBadgeColor));
            }
        }
        public bool UseMessageBus { get => Data.UseMessageBus; set => Data.UseMessageBus = value; }
        public bool AutoDecompose { get => Data.AutoDecompose; set => Data.AutoDecompose = value; }
        public string? GroupId { get => Data.GroupId; set => Data.GroupId = value; }
        public string? GroupName { get => Data.GroupName; set => Data.GroupName = value; }
        public string AdditionalInstructions { get => Data.AdditionalInstructions; set => Data.AdditionalInstructions = value; }
        public string? StoredPrompt { get => Data.StoredPrompt; set => Data.StoredPrompt = value; }
        public string? ConversationId { get => Data.ConversationId; set => Data.ConversationId = value; }
        public string? FullOutput { get => Data.FullOutput; set => Data.FullOutput = value; }
        public ModelType Model { get => Data.Model; set => Data.Model = value; }
        public int MaxIterations { get => Data.MaxIterations; set => Data.MaxIterations = value; }
        public List<string> ImagePaths { get => Data.ImagePaths; set => Data.ImagePaths = value; }
        public List<string> GeneratedImagePaths { get => Data.GeneratedImagePaths; set => Data.GeneratedImagePaths = value; }
        public string ProjectPath { get => Data.ProjectPath; set => Data.ProjectPath = value; }
        public string ProjectColor { get => Data.ProjectColor; set => Data.ProjectColor = value; }

        public string ProjectDisplayName
        {
            get => Data.ProjectDisplayName;
            set { Data.ProjectDisplayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProjectName)); }
        }

        public int CurrentIteration
        {
            get => Data.CurrentIteration;
            set { Data.CurrentIteration = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string CompletionSummary
        {
            get => Data.CompletionSummary;
            set { Data.CompletionSummary = value; OnPropertyChanged(); }
        }

        public string Recommendations
        {
            get => Data.Recommendations;
            set { Data.Recommendations = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRecommendations)); }
        }

        public bool HasRecommendations => !string.IsNullOrWhiteSpace(Recommendations);

        public string VerificationResult
        {
            get => Data.VerificationResult;
            set { Data.VerificationResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasVerificationResult)); }
        }

        public bool IsVerified
        {
            get => Data.IsVerified;
            set { Data.IsVerified = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasVerificationResult)); }
        }

        public bool HasVerificationResult => !string.IsNullOrWhiteSpace(VerificationResult);

        public bool IsRecoveryTask { get => Data.IsRecoveryTask; set => Data.IsRecoveryTask = value; }

        public string Summary
        {
            get => Data.Summary;
            set { Data.Summary = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortDescription)); }
        }

        public AgentTaskStatus Status
        {
            get => Data.Status;
            set
            {
                Data.Status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsPlanning));
                OnPropertyChanged(nameof(IsQueued));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsInitQueued));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsRetryable));
                OnPropertyChanged(nameof(TimeInfo));
                OnPropertyChanged(nameof(HasPriorityBadge));
            }
        }

        public DateTime? EndTime
        {
            get => Data.EndTime;
            set { Data.EndTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeInfo)); }
        }

        // ── Runtime state delegation ──────────────────────────────────

        public StringBuilder OutputBuilder => Runtime.OutputBuilder;
        public string? GitStartHash { get => Data.GitStartHash; set => Data.GitStartHash = value; }
        public System.Windows.Threading.DispatcherTimer? FeatureModeRetryTimer { get => Runtime.FeatureModeRetryTimer; set => Runtime.FeatureModeRetryTimer = value; }
        public System.Windows.Threading.DispatcherTimer? FeatureModeIterationTimer { get => Runtime.FeatureModeIterationTimer; set => Runtime.FeatureModeIterationTimer = value; }
        public System.Windows.Threading.DispatcherTimer? TokenLimitRetryTimer { get => Runtime.TokenLimitRetryTimer; set => Runtime.TokenLimitRetryTimer = value; }
        public int ConsecutiveFailures { get => Runtime.ConsecutiveFailures; set => Runtime.ConsecutiveFailures = value; }
        public int LastIterationOutputStart { get => Runtime.LastIterationOutputStart; set => Runtime.LastIterationOutputStart = value; }
        public Process? Process { get => Runtime.Process; set => Runtime.Process = value; }
        public System.Threading.CancellationTokenSource? Cts { get => Runtime.Cts; set => Runtime.Cts = value; }
        public string? QueuedReason { get => Runtime.QueuedReason; set => Runtime.QueuedReason = value; }
        public string? BlockedByTaskId { get => Runtime.BlockedByTaskId; set => Runtime.BlockedByTaskId = value; }
        public int? BlockedByTaskNumber { get => Runtime.BlockedByTaskNumber; set => Runtime.BlockedByTaskNumber = value; }
        public List<string> DependencyTaskIds { get => Runtime.DependencyTaskIds; set => Runtime.DependencyTaskIds = value; }
        public void AddDependencyTaskId(string id) => Runtime.AddDependencyTaskId(id);
        public bool RemoveDependencyTaskId(string id) => Runtime.RemoveDependencyTaskId(id);
        public bool ContainsDependencyTaskId(string id) => Runtime.ContainsDependencyTaskId(id);
        public int DependencyTaskIdCount => Runtime.DependencyTaskIdCount;
        public void ClearDependencyTaskIds() => Runtime.ClearDependencyTaskIds();
        public List<int> DependencyTaskNumbers { get => Runtime.DependencyTaskNumbers; set => Runtime.DependencyTaskNumbers = value; }
        public bool IsPlanningBeforeQueue { get => Runtime.IsPlanningBeforeQueue; set => Runtime.IsPlanningBeforeQueue = value; }
        public bool NeedsPlanRestart { get => Runtime.NeedsPlanRestart; set => Runtime.NeedsPlanRestart = value; }
        public string? DependencyContext { get => Data.DependencyContext; set => Data.DependencyContext = value; }
        public string? PendingFileLockPath { get => Runtime.PendingFileLockPath; set => Runtime.PendingFileLockPath = value; }
        public string? PendingFileLockBlocker { get => Runtime.PendingFileLockBlocker; set => Runtime.PendingFileLockBlocker = value; }

        // ── Parent-child hierarchy ────────────────────────────────────

        public string? ParentTaskId { get => Data.ParentTaskId; set => Data.ParentTaskId = value; }
        public List<string> ChildTaskIds { get => Data.ChildTaskIds; set => Data.ChildTaskIds = value; }

        // ── Feature mode multi-phase tracking ────────────────────────
        public FeatureModePhase FeatureModePhase { get => Data.FeatureModePhase; set => Data.FeatureModePhase = value; }
        public List<string> FeaturePhaseChildIds { get => Data.FeaturePhaseChildIds; set => Data.FeaturePhaseChildIds = value; }
        public string OriginalFeatureDescription { get => Data.OriginalFeatureDescription; set => Data.OriginalFeatureDescription = value; }
        public int SubTaskCounter { get => Runtime.SubTaskCounter; set => Runtime.SubTaskCounter = value; }

        public bool IsSubTask => ParentTaskId != null;
        public bool HasChildren => ChildTaskIds.Count > 0;

        public string HierarchyLabel
        {
            get
            {
                if (!IsSubTask) return $"#{TaskNumber:D4}";
                return $"#{TaskNumber:D4}.{Runtime.SubTaskIndex}";
            }
        }

        public int NestingDepth => IsSubTask ? 1 : 0;

        // ── Token tracking ─────────────────────────────────────────────

        public long InputTokens
        {
            get => Data.InputTokens;
            set { Data.InputTokens = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenDisplayText)); OnPropertyChanged(nameof(HasTokenData)); }
        }

        public long OutputTokens
        {
            get => Data.OutputTokens;
            set { Data.OutputTokens = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenDisplayText)); OnPropertyChanged(nameof(HasTokenData)); }
        }

        public long CacheReadTokens
        {
            get => Data.CacheReadTokens;
            set { Data.CacheReadTokens = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenDisplayText)); }
        }

        public long CacheCreationTokens
        {
            get => Data.CacheCreationTokens;
            set { Data.CacheCreationTokens = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenDisplayText)); }
        }

        public bool HasTokenData => InputTokens > 0 || OutputTokens > 0;

        public string TokenDisplayText
        {
            get
            {
                if (!HasTokenData) return "";
                var total = InputTokens + OutputTokens;
                return $"{FormatTokenCount(total)} tokens ({FormatTokenCount(InputTokens)} in / {FormatTokenCount(OutputTokens)} out)";
            }
        }

        public void AddTokenUsage(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            InputTokens += inputTokens;
            OutputTokens += outputTokens;
            CacheReadTokens += cacheReadTokens;
            CacheCreationTokens += cacheCreationTokens;
        }

        private static string FormatTokenCount(long count) => Helpers.FormatHelpers.FormatTokenCount(count);

        // ── Tool activity feed (UI-bound) ─────────────────────────────

        private string _toolActivityText = "";

        public string ToolActivityText
        {
            get => _toolActivityText;
            private set { _toolActivityText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasToolActivity)); }
        }

        public bool HasToolActivity => !string.IsNullOrEmpty(_toolActivityText);

        public void AddToolActivity(string action)
        {
            ToolActivityText = action;
        }

        public void ClearToolActivity()
        {
            ToolActivityText = "";
        }

        // ── Computed properties ───────────────────────────────────────

        public string ProjectName =>
            !string.IsNullOrEmpty(ProjectDisplayName) ? ProjectDisplayName :
            string.IsNullOrEmpty(ProjectPath) ? "" : Path.GetFileName(ProjectPath);

        public string ShortDescription
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Summary))
                {
                    var line = Summary.Split('\n')[0].TrimEnd('\r').Trim();
                    return line.Length > 80 ? line[..80] + "..." : line;
                }
                var desc = Description.Split('\n')[0].TrimEnd('\r').Trim();
                return desc.Length > 45 ? desc[..45] + "..." : desc;
            }
        }

        public bool IsWaitingForRetry => TokenLimitRetryTimer != null || FeatureModeRetryTimer != null;

        public string StatusText => Status switch
        {
            AgentTaskStatus.Running when IsWaitingForRetry => "Retrying soon",
            AgentTaskStatus.Running => IsFeatureMode ? $"Running ({CurrentIteration}/{MaxIterations})" : "Running",
            AgentTaskStatus.Completed => "Finished",
            AgentTaskStatus.Cancelled => "Cancelled",
            AgentTaskStatus.Failed => "Failed",
            AgentTaskStatus.Queued => "Queued",
            AgentTaskStatus.Paused => "Paused",
            AgentTaskStatus.InitQueued => "Waiting",
            AgentTaskStatus.Planning => "Planning",
            AgentTaskStatus.Verifying => "Verifying",
            _ => "?"
        };

        public string StatusColor => Status switch
        {
            AgentTaskStatus.Running => "#64B5F6",
            AgentTaskStatus.Completed => "#00E676",
            AgentTaskStatus.Cancelled => "#E0A030",
            AgentTaskStatus.Failed => "#E05555",
            AgentTaskStatus.Queued => "#FFD600",
            AgentTaskStatus.Paused => "#CE93D8",
            AgentTaskStatus.InitQueued => "#FF9800",
            AgentTaskStatus.Planning => "#B39DDB",
            AgentTaskStatus.Verifying => "#80CBC4",
            _ => "#555555"
        };

        public bool IsRunning => Status == AgentTaskStatus.Running;

        public bool IsPlanning => Status == AgentTaskStatus.Planning;

        public bool IsQueued => Status == AgentTaskStatus.Queued;

        public bool IsPaused => Status == AgentTaskStatus.Paused;

        public bool IsInitQueued => Status == AgentTaskStatus.InitQueued;

        public bool IsFinished => Status is AgentTaskStatus.Completed or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed;

        public bool IsRetryable => Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;

        public bool HasPriorityBadge => PriorityLevel != TaskPriority.Normal || (Priority > 0 && (IsQueued || IsInitQueued));

        public string PriorityBadgeText => PriorityLevel switch
        {
            TaskPriority.Critical => "CRIT",
            TaskPriority.High => "HIGH",
            TaskPriority.Low => "LOW",
            _ => (IsQueued || IsInitQueued) && Priority > 0 ? $"P{Priority}" : ""
        };

        public string PriorityBadgeColor => PriorityLevel switch
        {
            TaskPriority.Critical => "#FF5252",
            TaskPriority.High => "#FFD600",
            TaskPriority.Low => "#78909C",
            _ => "#FFD600"
        };

        public bool HasActiveToggles => !string.IsNullOrEmpty(ActiveTogglesText);

        public string ActiveTogglesText
        {
            get
            {
                var tags = new List<string>(4);
                if (IsFeatureMode) tags.Add("FEAT");
                if (ExtendedPlanning) tags.Add("EXT");
                if (RemoteSession) tags.Add("REM");
                if (Headless) tags.Add("HDL");
                if (SpawnTeam) tags.Add("TEAM");
                if (AutoDecompose) tags.Add("DEC");
                if (UseMcp) tags.Add("MCP");
                return string.Join(" ", tags);
            }
        }

        public string ActiveTogglesTooltip
        {
            get
            {
                var lines = new List<string>(4);
                if (IsFeatureMode) lines.Add("FEAT = Feature Mode");
                if (ExtendedPlanning) lines.Add("EXT = Extended Planning");
                if (RemoteSession) lines.Add("REM = Remote Session");
                if (Headless) lines.Add("HDL = Headless");
                if (SpawnTeam) lines.Add("TEAM = Spawn Team");
                if (AutoDecompose) lines.Add("DEC = Auto Decompose");
                if (UseMcp) lines.Add("MCP = MCP Tools");
                return string.Join("\n", lines);
            }
        }

        public string TimeInfo
        {
            get
            {
                var started = $"Started {StartTime:MMM dd, HH:mm:ss}";
                if (Status == AgentTaskStatus.InitQueued)
                    return $"{started} | Waiting (max concurrent reached)";
                if (Status == AgentTaskStatus.Queued)
                {
                    string waitInfo;
                    if (DependencyTaskNumbers.Count > 0)
                        waitInfo = "waiting for " + string.Join(", ", DependencyTaskNumbers.Select(n => $"#{n}"));
                    else if (BlockedByTaskNumber.HasValue)
                        waitInfo = $"waiting for #{BlockedByTaskNumber.Value}";
                    else
                        waitInfo = "waiting";
                    return $"{started} | Queued ({waitInfo})";
                }
                if (EndTime.HasValue)
                {
                    var duration = EndTime.Value - StartTime;
                    return $"{started} | Ran {(int)duration.TotalMinutes}m {duration.Seconds}s";
                }
                var running = DateTime.Now - StartTime;
                if (Status == AgentTaskStatus.Paused)
                    return $"{started} | Paused at {(int)running.TotalMinutes}m {running.Seconds}s";
                if (Status == AgentTaskStatus.Planning)
                    return $"{started} | Planning {(int)running.TotalMinutes}m {running.Seconds}s";
                return $"{started} | Running {(int)running.TotalMinutes}m {running.Seconds}s";
            }
        }

        // ── INotifyPropertyChanged ────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class FileLock : INotifyPropertyChanged
    {
        public string NormalizedPath { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string OwnerTaskId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public DateTime AcquiredAt { get; set; } = DateTime.Now;
        public bool IsIgnored { get; set; }

        public string FileName => Path.GetFileName(OriginalPath);
        public string TimeText => AcquiredAt.ToString("HH:mm:ss");
        public string StatusText => IsIgnored ? "Ignored" : "Active";

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class QueuedTaskInfo
    {
        public AgentTask Task { get; set; } = null!;
        public string ConflictingFilePath { get; set; } = "";
        public string BlockingTaskId { get; set; } = "";
        public HashSet<string> BlockedByTaskIds { get; set; } = new();
    }

    public class StreamingToolState
    {
        public string CurrentToolName { get; set; } = "";
        public bool IsFileModifyTool { get; set; }
        public bool FilePathChecked { get; set; }
        public StringBuilder JsonAccumulator { get; set; } = new();
    }
}
