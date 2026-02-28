using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgenticEngine
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
        Planning
    }

    public enum ModelType
    {
        ClaudeCode,
        Gemini
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
        public bool IsOvernight { get => Data.IsOvernight; set => Data.IsOvernight = value; }
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
        public bool UseMessageBus { get => Data.UseMessageBus; set => Data.UseMessageBus = value; }
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
                OnPropertyChanged(nameof(TimeInfo));
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
        public System.Windows.Threading.DispatcherTimer? OvernightRetryTimer { get => Runtime.OvernightRetryTimer; set => Runtime.OvernightRetryTimer = value; }
        public System.Windows.Threading.DispatcherTimer? OvernightIterationTimer { get => Runtime.OvernightIterationTimer; set => Runtime.OvernightIterationTimer = value; }
        public int ConsecutiveFailures { get => Runtime.ConsecutiveFailures; set => Runtime.ConsecutiveFailures = value; }
        public int LastIterationOutputStart { get => Runtime.LastIterationOutputStart; set => Runtime.LastIterationOutputStart = value; }
        public Process? Process { get => Runtime.Process; set => Runtime.Process = value; }
        public System.Threading.CancellationTokenSource? Cts { get => Runtime.Cts; set => Runtime.Cts = value; }
        public string? QueuedReason { get => Runtime.QueuedReason; set => Runtime.QueuedReason = value; }
        public string? BlockedByTaskId { get => Runtime.BlockedByTaskId; set => Runtime.BlockedByTaskId = value; }
        public int? BlockedByTaskNumber { get => Runtime.BlockedByTaskNumber; set => Runtime.BlockedByTaskNumber = value; }
        public List<string> DependencyTaskIds { get => Runtime.DependencyTaskIds; set => Runtime.DependencyTaskIds = value; }
        public List<int> DependencyTaskNumbers { get => Runtime.DependencyTaskNumbers; set => Runtime.DependencyTaskNumbers = value; }
        public bool IsPlanningBeforeQueue { get => Runtime.IsPlanningBeforeQueue; set => Runtime.IsPlanningBeforeQueue = value; }
        public bool NeedsPlanRestart { get => Runtime.NeedsPlanRestart; set => Runtime.NeedsPlanRestart = value; }
        public string? DependencyContext { get => Data.DependencyContext; set => Data.DependencyContext = value; }
        public string? PendingFileLockPath { get => Runtime.PendingFileLockPath; set => Runtime.PendingFileLockPath = value; }
        public string? PendingFileLockBlocker { get => Runtime.PendingFileLockBlocker; set => Runtime.PendingFileLockBlocker = value; }

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

        public string ShortDescription =>
            !string.IsNullOrWhiteSpace(Summary) ? Summary :
            Description.Length > 45 ? Description[..45] + "..." : Description;

        public string StatusText => Status switch
        {
            AgentTaskStatus.Running => IsOvernight ? $"Running ({CurrentIteration}/{MaxIterations})" : "Running",
            AgentTaskStatus.Completed => "Finished",
            AgentTaskStatus.Cancelled => "Cancelled",
            AgentTaskStatus.Failed => "Failed",
            AgentTaskStatus.Queued => "Queued",
            AgentTaskStatus.Paused => "Paused",
            AgentTaskStatus.InitQueued => "Waiting",
            AgentTaskStatus.Planning => "Planning",
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
            _ => "#555555"
        };

        public bool IsRunning => Status == AgentTaskStatus.Running;

        public bool IsPlanning => Status == AgentTaskStatus.Planning;

        public bool IsQueued => Status == AgentTaskStatus.Queued;

        public bool IsPaused => Status == AgentTaskStatus.Paused;

        public bool IsInitQueued => Status == AgentTaskStatus.InitQueued;

        public bool IsFinished => Status is AgentTaskStatus.Completed or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed;

        public string TimeInfo
        {
            get
            {
                var started = $"Started {StartTime:HH:mm:ss}";
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
