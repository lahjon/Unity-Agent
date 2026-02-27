using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnityAgent
{
    public enum AgentTaskStatus
    {
        Running,
        Completed,
        Cancelled,
        Failed,
        Queued,
        Ongoing
    }

    public enum ModelType
    {
        ClaudeCode,
        Gemini
    }

    public class AgentTask : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
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
        public ModelType Model { get; set; } = ModelType.ClaudeCode;
        public int MaxIterations { get; set; } = 50;

        private int _currentIteration;
        public int CurrentIteration
        {
            get => _currentIteration;
            set { _currentIteration = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public string ProjectPath { get; set; } = "";
        public string ProjectColor { get; set; } = "#666666";
        public List<string> ImagePaths { get; set; } = new();
        public List<string> GeneratedImagePaths { get; set; } = new();
        public StringBuilder OutputBuilder { get; } = new();

        // Captured at task start for completion summary diff
        [System.Text.Json.Serialization.JsonIgnore]
        public string? GitStartHash { get; set; }

        private string _completionSummary = "";
        public string CompletionSummary
        {
            get => _completionSummary;
            set { _completionSummary = value; OnPropertyChanged(); }
        }

        private string _summary = "";
        public string Summary
        {
            get => _summary;
            set { _summary = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortDescription)); }
        }

        // Used by overnight mode to store the retry timer so it can be cancelled
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Threading.DispatcherTimer? OvernightRetryTimer { get; set; }

        // Overnight safety: per-iteration timeout timer
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Threading.DispatcherTimer? OvernightIterationTimer { get; set; }

        // Overnight safety: track consecutive failures to detect crash loops
        [System.Text.Json.Serialization.JsonIgnore]
        public int ConsecutiveFailures { get; set; }

        // Overnight safety: marks the start of the current iteration's output in OutputBuilder
        [System.Text.Json.Serialization.JsonIgnore]
        public int LastIterationOutputStart { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public Process? Process { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? QueuedReason { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? BlockedByTaskId { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public List<string> DependencyTaskIds { get; set; } = new();

        private AgentTaskStatus _status = AgentTaskStatus.Running;
        public AgentTaskStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsQueued));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(TimeInfo));
            }
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeInfo)); }
        }

        public string ProjectName =>
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
            AgentTaskStatus.Ongoing => "Ongoing",
            _ => "?"
        };

        public string StatusColor => Status switch
        {
            AgentTaskStatus.Running => "#64B5F6",
            AgentTaskStatus.Completed => "#00E676",
            AgentTaskStatus.Cancelled => "#E0A030",
            AgentTaskStatus.Failed => "#E05555",
            AgentTaskStatus.Queued => "#FFD600",
            AgentTaskStatus.Ongoing => "#64B5F6",
            _ => "#555555"
        };

        public bool IsRunning => Status == AgentTaskStatus.Running || Status == AgentTaskStatus.Ongoing;

        public bool IsQueued => Status == AgentTaskStatus.Queued;

        public bool IsFinished => Status is AgentTaskStatus.Completed or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed;

        public string TimeInfo
        {
            get
            {
                var started = $"Started {StartTime:HH:mm:ss}";
                if (Status == AgentTaskStatus.Queued)
                {
                    string waitInfo;
                    if (DependencyTaskIds.Count > 0)
                        waitInfo = "waiting for " + string.Join(", ", DependencyTaskIds.Select(id => $"#{id}"));
                    else if (!string.IsNullOrEmpty(BlockedByTaskId))
                        waitInfo = $"waiting for #{BlockedByTaskId}";
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
                return $"{started} | Running {(int)running.TotalMinutes}m {running.Seconds}s";
            }
        }

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
