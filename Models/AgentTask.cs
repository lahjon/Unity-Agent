using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Spritely
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
        Verifying,
        Recommendation,
        Committing,
        SoftStop,
        Stored
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

    public enum TeamsModePhase
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

        // ── INotifyPropertyChanged + helpers ─────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        public void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Sets a backing field, raises PropertyChanged if the value changed, and returns whether it changed.</summary>
        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        /// <summary>Sets a backing field and raises PropertyChanged for the property and any related computed properties.</summary>
        private bool Set<T>(ref T field, T value, string[] alsoNotify, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            var handler = PropertyChanged;
            if (handler != null)
                foreach (var n in alsoNotify)
                    handler(this, new PropertyChangedEventArgs(n));
            return true;
        }

        /// <summary>Raises PropertyChanged for each of the specified property names.</summary>
        private void NotifyAlso(params string[] names)
        {
            var handler = PropertyChanged;
            if (handler == null) return;
            foreach (var n in names)
                handler(this, new PropertyChangedEventArgs(n));
        }

        // ── Persistent data delegation (no notification) ─────────────────

        public string Id => Data.Id;
        public int TaskNumber { get => Data.TaskNumber; set => Data.TaskNumber = value; }
        public string Description { get => Data.Description; set => Data.Description = value; }
        public DateTime StartTime { get => Data.StartTime; set => Data.StartTime = value; }
        public bool SkipPermissions { get => Data.SkipPermissions; set => Data.SkipPermissions = value; }
        public bool Headless { get => Data.Headless; set => Data.Headless = value; }
        public bool IsTeamsMode { get => Data.IsTeamsMode; set => Data.IsTeamsMode = value; }
        public bool UseMcp { get => Data.UseMcp; set => Data.UseMcp = value; }
        public bool SpawnTeam { get => Data.SpawnTeam; set => Data.SpawnTeam = value; }
        public bool ExtendedPlanning { get => Data.ExtendedPlanning; set => Data.ExtendedPlanning = value; }
        public bool PlanOnly { get => Data.PlanOnly; set => Data.PlanOnly = value; }
        public bool UseMessageBus { get => Data.UseMessageBus; set => Data.UseMessageBus = value; }
        public bool AutoDecompose { get => Data.AutoDecompose; set => Data.AutoDecompose = value; }
        public bool ApplyFix { get => Data.ApplyFix; set => Data.ApplyFix = value; }
        public bool UseAutoMode { get => Data.UseAutoMode; set => Data.UseAutoMode = value; }
        public bool AllowTeamsModeInference { get => Data.AllowTeamsModeInference; set => Data.AllowTeamsModeInference = value; }
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
        public string? GitStartHash { get => Data.GitStartHash; set => Data.GitStartHash = value; }
        public string? DependencyContext { get => Data.DependencyContext; set => Data.DependencyContext = value; }
        public string? ParentTaskId { get => Data.ParentTaskId; set => Data.ParentTaskId = value; }
        public List<string> ChildTaskIds { get => Data.ChildTaskIds; set => Data.ChildTaskIds = value; }
        public List<string> TeamsPhaseChildIds { get => Data.TeamsPhaseChildIds; set => Data.TeamsPhaseChildIds = value; }
        public void AddTeamsPhaseChildId(string id) => Data.AddTeamsPhaseChildId(id);
        public void ClearTeamsPhaseChildIds() => Data.ClearTeamsPhaseChildIds();
        public bool ContainsTeamsPhaseChildId(string id) => Data.ContainsTeamsPhaseChildId(id);
        public int TeamsPhaseChildIdCount => Data.TeamsPhaseChildIdCount;
        public string OriginalTeamsDescription { get => Data.OriginalTeamsDescription; set => Data.OriginalTeamsDescription = value; }
        public int? TimeoutMinutes { get => Data.TimeoutMinutes; set => Data.TimeoutMinutes = value; }
        public bool HasTimeoutWarning { get => Data.HasTimeoutWarning; set => Data.HasTimeoutWarning = value; }

        // ── Persistent data delegation (with notification) ───────────────

        public bool IgnoreFileLocks
        {
            get => Data.IgnoreFileLocks;
            set { if (Data.IgnoreFileLocks == value) return; Data.IgnoreFileLocks = value; OnPropertyChanged(); }
        }

        public int Priority
        {
            get => Data.Priority;
            set { if (Data.Priority == value) return; Data.Priority = value; OnPropertyChanged(); NotifyAlso(nameof(HasPriorityBadge), nameof(PriorityBadgeText)); }
        }

        public TaskPriority PriorityLevel
        {
            get => Data.PriorityLevel;
            set { if (Data.PriorityLevel == value) return; Data.PriorityLevel = value; OnPropertyChanged(); NotifyAlso(nameof(HasPriorityBadge), nameof(PriorityBadgeText), nameof(PriorityBadgeColor)); }
        }

        public string ProjectDisplayName
        {
            get => Data.ProjectDisplayName;
            set { if (Data.ProjectDisplayName == value) return; Data.ProjectDisplayName = value; OnPropertyChanged(); NotifyAlso(nameof(ProjectName)); }
        }

        public int CurrentIteration
        {
            get => Data.CurrentIteration;
            set { if (Data.CurrentIteration == value) return; Data.CurrentIteration = value; OnPropertyChanged(); NotifyAlso(nameof(StatusText), nameof(QueueStatusText)); }
        }

        public string CompletionSummary
        {
            get => Data.CompletionSummary;
            set { if (Data.CompletionSummary == value) return; Data.CompletionSummary = value; OnPropertyChanged(); }
        }

        public string Recommendations
        {
            get => Data.Recommendations;
            set { if (Data.Recommendations == value) return; Data.Recommendations = value; OnPropertyChanged(); NotifyAlso(nameof(HasRecommendations)); }
        }

        public string VerificationResult
        {
            get => Data.VerificationResult;
            set { if (Data.VerificationResult == value) return; Data.VerificationResult = value; OnPropertyChanged(); NotifyAlso(nameof(HasVerificationResult)); }
        }

        public bool IsVerified
        {
            get => Data.IsVerified;
            set { if (Data.IsVerified == value) return; Data.IsVerified = value; OnPropertyChanged(); NotifyAlso(nameof(HasVerificationResult)); }
        }

        public bool IsCommitted
        {
            get => Data.IsCommitted;
            set { if (Data.IsCommitted == value) return; Data.IsCommitted = value; OnPropertyChanged(); NotifyAlso(nameof(IsCompletedUncommitted)); }
        }

        public string? CommitHash
        {
            get => Data.CommitHash;
            set { if (Data.CommitHash == value) return; Data.CommitHash = value; OnPropertyChanged(); }
        }

        public string? CommitError
        {
            get => Data.CommitError;
            set { if (Data.CommitError == value) return; Data.CommitError = value; OnPropertyChanged(); NotifyAlso(nameof(HasCommitError)); }
        }

        public List<string> ChangedFiles
        {
            get => Data.ChangedFiles;
            set { Data.ChangedFiles = value; OnPropertyChanged(); }
        }

        public string? CommitDiff
        {
            get => Data.CommitDiff;
            set { if (Data.CommitDiff == value) return; Data.CommitDiff = value; OnPropertyChanged(); }
        }

        public string Summary
        {
            get => Data.Summary;
            set { if (Data.Summary == value) return; Data.Summary = value; OnPropertyChanged(); NotifyAlso(nameof(ShortDescription)); }
        }

        public string Header
        {
            get => Data.Header;
            set { if (Data.Header == value) return; Data.Header = value; OnPropertyChanged(); NotifyAlso(nameof(HasHeader), nameof(ShortDescription)); }
        }

        public bool HasHeader => !string.IsNullOrEmpty(Header);

        public AgentTaskStatus Status
        {
            get => Data.Status;
            set
            {
                if (Data.Status == value) return;
                Data.Status = value;
                OnPropertyChanged();
                NotifyAlso(nameof(StatusText), nameof(QueueStatusText), nameof(StatusColor),
                    nameof(IsRunning), nameof(IsPlanning), nameof(IsQueued), nameof(IsPaused),
                    nameof(IsInitQueued), nameof(IsFinished), nameof(IsCommitting), nameof(IsSoftStopping),
                    nameof(IsRetryable), nameof(IsContinuable),
                    nameof(TimeInfo), nameof(HasPriorityBadge), nameof(IsCompletedUncommitted));
            }
        }

        public DateTime? EndTime
        {
            get => Data.EndTime;
            set { if (Data.EndTime == value) return; Data.EndTime = value; OnPropertyChanged(); NotifyAlso(nameof(TimeInfo)); }
        }

        public TeamsModePhase TeamsModePhase
        {
            get => Data.TeamsModePhase;
            set { if (Data.TeamsModePhase == value) return; Data.TeamsModePhase = value; OnPropertyChanged(); NotifyAlso(nameof(StatusText), nameof(QueueStatusText)); }
        }

        // ── Token tracking ─────────────────────────────────────────────

        public long InputTokens
        {
            get => Data.InputTokens;
            set { Data.InputTokens = value; OnPropertyChanged(); NotifyAlso(nameof(TokenDisplayText), nameof(HasTokenData)); }
        }

        public long OutputTokens
        {
            get => Data.OutputTokens;
            set { Data.OutputTokens = value; OnPropertyChanged(); NotifyAlso(nameof(TokenDisplayText), nameof(HasTokenData)); }
        }

        public long CacheReadTokens
        {
            get => Data.CacheReadTokens;
            set { Data.CacheReadTokens = value; OnPropertyChanged(); NotifyAlso(nameof(TokenDisplayText)); }
        }

        public long CacheCreationTokens
        {
            get => Data.CacheCreationTokens;
            set { Data.CacheCreationTokens = value; OnPropertyChanged(); NotifyAlso(nameof(TokenDisplayText)); }
        }

        public bool HasTokenData => InputTokens > 0 || OutputTokens > 0;
        public long TotalAllTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

        public string TokenDisplayText
        {
            get
            {
                if (!HasTokenData) return "";
                var cost = Helpers.FormatHelpers.EstimateCost(InputTokens, OutputTokens, CacheReadTokens, CacheCreationTokens, Data.LastUsedCliModel ?? Runtime.LastCliModel);
                var costStr = Helpers.FormatHelpers.FormatCost(cost);
                if (CacheReadTokens > 0 || CacheCreationTokens > 0)
                    return $"{FormatTokenCount(TotalAllTokens)} tokens (~{costStr}) | {FormatTokenCount(InputTokens)} in / {FormatTokenCount(OutputTokens)} out / {FormatTokenCount(CacheReadTokens)} cached";
                return $"{FormatTokenCount(TotalAllTokens)} tokens (~{costStr}) | {FormatTokenCount(InputTokens)} in / {FormatTokenCount(OutputTokens)} out";
            }
        }

        public void AddTokenUsage(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            InputTokens += inputTokens;
            OutputTokens += outputTokens;
            CacheReadTokens += cacheReadTokens;
            CacheCreationTokens += cacheCreationTokens;

            // Persist the CLI model for accurate cost display across sessions
            if (Data.LastUsedCliModel == null && Runtime.LastCliModel != null)
                Data.LastUsedCliModel = Runtime.LastCliModel;
        }

        private static string FormatTokenCount(long count) => Helpers.FormatHelpers.FormatTokenCount(count);

        // ── Tool activity feed (UI-bound) ─────────────────────────────

        private string _toolActivityText = "";

        public string ToolActivityText
        {
            get => _toolActivityText;
            private set => Set(ref _toolActivityText, value, [nameof(HasToolActivity)]);
        }

        public bool HasToolActivity => !string.IsNullOrEmpty(_toolActivityText);
        public void AddToolActivity(string action) => ToolActivityText = action;
        public void ClearToolActivity() => ToolActivityText = "";

        // ── Runtime state delegation ──────────────────────────────────

        public StringBuilder OutputBuilder => Runtime.OutputBuilder;
        public System.Windows.Threading.DispatcherTimer? TeamsModeRetryTimer { get => Runtime.TeamsModeRetryTimer; set => Runtime.TeamsModeRetryTimer = value; }
        public System.Windows.Threading.DispatcherTimer? TeamsModeIterationTimer { get => Runtime.TeamsModeIterationTimer; set => Runtime.TeamsModeIterationTimer = value; }
        public System.Windows.Threading.DispatcherTimer? TokenLimitRetryTimer { get => Runtime.TokenLimitRetryTimer; set => Runtime.TokenLimitRetryTimer = value; }
        public int ConsecutiveFailures { get => Runtime.ConsecutiveFailures; set => Runtime.ConsecutiveFailures = value; }
        public int ConsecutiveTokenLimitRetries { get => Runtime.ConsecutiveTokenLimitRetries; set => Runtime.ConsecutiveTokenLimitRetries = value; }
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
        public string? PendingFileLockPath { get => Runtime.PendingFileLockPath; set => Runtime.PendingFileLockPath = value; }
        public string? PendingFileLockBlocker { get => Runtime.PendingFileLockBlocker; set => Runtime.PendingFileLockBlocker = value; }
        public int SubTaskCounter { get => Runtime.SubTaskCounter; set => Runtime.SubTaskCounter = value; }

        // ── Queue position tracking ───────────────────────────────────

        private int _queuePosition = 0;

        /// <summary>Gets or sets the position of this task in the InitQueued queue (1-based, 0 means not queued).</summary>
        public int QueuePosition
        {
            get => _queuePosition;
            set => Set(ref _queuePosition, value, [nameof(QueueStatusText)]);
        }

        // ── Computed properties ───────────────────────────────────────

        public bool HasRecommendations => !string.IsNullOrWhiteSpace(Recommendations);
        public bool HasVerificationResult => !string.IsNullOrWhiteSpace(VerificationResult);
        public bool IsSubTask => ParentTaskId != null;
        public bool HasChildren => ChildTaskIds.Count > 0;
        public int NestingDepth => IsSubTask ? 1 : 0;
        public bool IsWaitingForRetry => TokenLimitRetryTimer != null || TeamsModeRetryTimer != null;
        public bool IsRunning => Status is AgentTaskStatus.Running or AgentTaskStatus.Stored;
        public bool IsPlanning => Status == AgentTaskStatus.Planning;
        public bool IsQueued => Status == AgentTaskStatus.Queued;
        public bool IsPaused => Status == AgentTaskStatus.Paused;
        public bool IsInitQueued => Status == AgentTaskStatus.InitQueued;
        public bool IsCommitting => Status == AgentTaskStatus.Committing;
        public bool IsSoftStopping => Status == AgentTaskStatus.SoftStop;
        public bool IsFinished => Status is AgentTaskStatus.Completed or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed or AgentTaskStatus.Recommendation;
        public bool IsRetryable => Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;
        public bool IsContinuable => Status == AgentTaskStatus.Recommendation;
        public bool HasPriorityBadge => PriorityLevel != TaskPriority.Normal || (Priority > 0 && (IsQueued || IsInitQueued));
        public bool HasActiveToggles => !string.IsNullOrEmpty(ActiveTogglesText);
        public bool HasCommitError => !string.IsNullOrWhiteSpace(CommitError);
        public bool IsPendingCommit => Runtime.PendingCommitTask != null;
        public bool IsCompletedUncommitted => Status == AgentTaskStatus.Completed && !IsCommitted;

        public string ProjectName =>
            !string.IsNullOrEmpty(ProjectDisplayName) ? ProjectDisplayName :
            string.IsNullOrEmpty(ProjectPath) ? "" : Path.GetFileName(ProjectPath);

        public string HierarchyLabel => !IsSubTask ? $"#{TaskNumber:D4}" : $"#{TaskNumber:D4}.{Runtime.SubTaskIndex}";

        public string ShortDescription
        {
            get
            {
                var prefix = HasHeader ? $"[{Header}] " : "";
                if (!string.IsNullOrWhiteSpace(Summary))
                {
                    var line = Summary.Split('\n')[0].TrimEnd('\r').Trim();
                    var full = prefix + line;
                    return full.Length > 80 ? full[..80] + "..." : full;
                }
                if (string.IsNullOrWhiteSpace(Description))
                    return HasHeader ? $"[{Header}] Task #{TaskNumber}" : $"Task #{TaskNumber}";
                var desc = Description.Split('\n')[0].TrimEnd('\r').Trim();
                var result = prefix + desc;
                return result.Length > 60 ? result[..60] + "..." : result;
            }
        }

        public string StatusText => Status switch
        {
            AgentTaskStatus.Running when IsWaitingForRetry => "Retrying soon",
            AgentTaskStatus.Running when IsTeamsMode && TeamsModePhase == TeamsModePhase.TeamPlanning =>
                $"Coordinating ({CurrentIteration}/{MaxIterations}) — Team Planning",
            AgentTaskStatus.Running when IsTeamsMode && TeamsModePhase == TeamsModePhase.Execution =>
                $"Coordinating ({CurrentIteration}/{MaxIterations}) — Execution",
            AgentTaskStatus.Running => IsTeamsMode ? $"Running ({CurrentIteration}/{MaxIterations})" : "Running",
            AgentTaskStatus.Completed => "Finished",
            AgentTaskStatus.Cancelled => "Cancelled",
            AgentTaskStatus.Failed => "Failed",
            AgentTaskStatus.Queued => "Queued",
            AgentTaskStatus.Paused => "Paused",
            AgentTaskStatus.InitQueued => "Waiting",
            AgentTaskStatus.Planning => "Planning",
            AgentTaskStatus.Verifying => "Verifying",
            AgentTaskStatus.Recommendation => "Has Recommendations",
            AgentTaskStatus.Committing => "Committing",
            AgentTaskStatus.SoftStop => "Stopping...",
            AgentTaskStatus.Stored => "Starting",
            _ => "?"
        };

        /// <summary>Gets the status text with queue position for InitQueued tasks.</summary>
        public string QueueStatusText
        {
            get
            {
                if (IsInitQueued && QueuePosition > 0)
                    return $"Queued (#{QueuePosition})";
                return StatusText;
            }
        }

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
            AgentTaskStatus.Recommendation => "#FFB74D",
            AgentTaskStatus.Committing => "#4DD0E1",
            AgentTaskStatus.SoftStop => "#FF8A65",
            AgentTaskStatus.Stored => "#42A5F5",
            _ => "#555555"
        };

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

        public string ActiveTogglesText
        {
            get
            {
                var tags = new List<string>(4);
                if (IsTeamsMode) tags.Add("TEAMS");
                if (ExtendedPlanning) tags.Add("EXT");
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
                if (IsTeamsMode) lines.Add("TEAMS = Teams Mode");
                if (ExtendedPlanning) lines.Add("EXT = Extended Planning");
                if (Headless) lines.Add("HDL = Headless");
                if (SpawnTeam) lines.Add("TEAM = Spawn Team");
                if (AutoDecompose) lines.Add("DEC = Auto Decompose");
                if (UseMcp) lines.Add("MCP = MCP Tools");
                return string.Join("\n", lines);
            }
        }

        /// <summary>Gets whether this task has pending file lock information</summary>
        public bool HasPendingFileLock => !string.IsNullOrEmpty(Runtime.PendingFileLockPath);

        /// <summary>Gets a formatted tooltip for queued tasks showing file lock information</summary>
        public string QueuedTooltip
        {
            get
            {
                if (!IsQueued && !IsInitQueued) return "";

                var lines = new List<string>();

                // Add queue reason if available
                if (!string.IsNullOrEmpty(Runtime.QueuedReason))
                {
                    lines.Add($"Reason: {Runtime.QueuedReason}");
                }

                // Add file lock information
                if (HasPendingFileLock)
                {
                    lines.Add($"Waiting for file: {Runtime.PendingFileLockPath}");
                    if (!string.IsNullOrEmpty(Runtime.PendingFileLockBlocker))
                    {
                        lines.Add($"Locked by: {Runtime.PendingFileLockBlocker}");
                    }
                }

                // Add dependency information
                if (Runtime.DependencyTaskIdCount > 0 || Runtime.DependencyTaskNumbers.Count > 0)
                {
                    if (lines.Count > 0) lines.Add(""); // Add empty line as separator

                    if (Runtime.DependencyTaskNumbers.Count > 0)
                    {
                        lines.Add($"Waiting for tasks: {string.Join(", ", Runtime.DependencyTaskNumbers.Select(n => $"#{n}"))}");
                    }
                }

                return lines.Count > 0 ? string.Join("\n", lines) : "Task is queued";
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
                if (Status == AgentTaskStatus.Committing)
                    return $"{started} | Committing...";
                return $"{started} | Running {(int)running.TotalMinutes}m {running.Seconds}s";
            }
        }

        public string FileLockTooltip
        {
            get
            {
                if (Status != AgentTaskStatus.Queued || string.IsNullOrEmpty(PendingFileLockPath))
                    return "";

                var tooltip = $"Waiting for file: {PendingFileLockPath}";
                if (!string.IsNullOrEmpty(PendingFileLockBlocker))
                {
                    tooltip += $"\nLocked by: {PendingFileLockBlocker}";
                }
                return tooltip;
            }
        }
    }

    public class FileLock : INotifyPropertyChanged
    {
        public string NormalizedPath { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string OwnerTaskId { get; set; } = "";
        public int OwnerTaskNumber { get; set; }
        public string ToolName { get; set; } = "";
        public DateTime AcquiredAt { get; set; } = DateTime.Now;
        public bool IsIgnored { get; set; }
        public bool IsWaiting { get; set; }

        public string FileName => Path.GetFileName(OriginalPath);
        public string TimeText => AcquiredAt.ToString("HH:mm:ss");
        public string StatusText => IsWaiting ? "Waiting" : (IsIgnored ? "Ignored" : "Active");

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
