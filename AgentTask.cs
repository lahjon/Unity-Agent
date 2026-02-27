using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnityAgent
{
    public enum AgentTaskStatus
    {
        Running,
        Completed,
        Cancelled,
        Failed
    }

    public class AgentTask : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public string Description { get; set; } = "";
        public DateTime StartTime { get; set; } = DateTime.Now;
        public bool SkipPermissions { get; set; }
        public StringBuilder OutputBuilder { get; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public Process? Process { get; set; }

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
                OnPropertyChanged(nameof(TimeInfo));
            }
        }

        private DateTime? _endTime;
        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeInfo)); }
        }

        public string ShortDescription =>
            Description.Length > 45 ? Description[..45] + "..." : Description;

        public string StatusText => Status switch
        {
            AgentTaskStatus.Running => "Running",
            AgentTaskStatus.Completed => "Completed",
            AgentTaskStatus.Cancelled => "Cancelled",
            AgentTaskStatus.Failed => "Failed",
            _ => "?"
        };

        public string StatusColor => Status switch
        {
            AgentTaskStatus.Running => "#89B4FA",
            AgentTaskStatus.Completed => "#A6E3A1",
            AgentTaskStatus.Cancelled => "#F38BA8",
            AgentTaskStatus.Failed => "#F9E2AF",
            _ => "#6C7086"
        };

        public string TimeInfo
        {
            get
            {
                var started = $"Started {StartTime:HH:mm:ss}";
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
}
