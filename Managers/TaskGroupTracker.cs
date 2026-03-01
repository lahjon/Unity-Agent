using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace HappyEngine.Managers
{
    public class TaskGroupState
    {
        public string GroupId { get; set; } = "";
        public string GroupName { get; set; } = "";
        public DateTime StartTime { get; set; } = DateTime.Now;
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public List<TaskGroupEntry> Tasks { get; set; } = new();
    }

    public class TaskGroupEntry
    {
        public string TaskId { get; set; } = "";
        public int TaskNumber { get; set; }
        public string Description { get; set; } = "";
        public AgentTaskStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string CompletionSummary { get; set; } = "";
        public string Recommendations { get; set; } = "";
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    public class GroupCompletedEventArgs : EventArgs
    {
        public TaskGroupState GroupState { get; set; } = null!;
    }

    public class TaskGroupTracker
    {
        private readonly ConcurrentDictionary<string, TaskGroupState> _groups = new();

        public event EventHandler<GroupCompletedEventArgs>? GroupCompleted;

        public void RegisterTask(AgentTask task)
        {
            if (string.IsNullOrEmpty(task.GroupId)) return;

            var state = _groups.GetOrAdd(task.GroupId, _ => new TaskGroupState
            {
                GroupId = task.GroupId!,
                GroupName = task.GroupName ?? task.GroupId!,
                StartTime = DateTime.Now
            });

            lock (state)
            {
                if (state.Tasks.Any(t => t.TaskId == task.Id)) return;

                state.TotalCount++;
                state.Tasks.Add(new TaskGroupEntry
                {
                    TaskId = task.Id,
                    TaskNumber = task.TaskNumber,
                    Description = task.Description,
                    Status = task.Status,
                    StartTime = task.StartTime
                });
            }
        }

        public void OnTaskCompleted(AgentTask task)
        {
            if (string.IsNullOrEmpty(task.GroupId)) return;
            if (!_groups.TryGetValue(task.GroupId!, out var state)) return;

            bool allDone;
            lock (state)
            {
                var entry = state.Tasks.FirstOrDefault(t => t.TaskId == task.Id);
                if (entry == null) return;

                entry.Status = task.Status;
                entry.EndTime = task.EndTime;
                entry.CompletionSummary = task.CompletionSummary;
                entry.Recommendations = task.Recommendations;
                entry.InputTokens = task.InputTokens;
                entry.OutputTokens = task.OutputTokens;

                if (task.Status == AgentTaskStatus.Failed)
                    state.FailedCount++;
                else
                    state.CompletedCount++;

                allDone = (state.CompletedCount + state.FailedCount) >= state.TotalCount;
            }

            if (allDone)
            {
                GroupCompleted?.Invoke(this, new GroupCompletedEventArgs { GroupState = state });
            }
        }

        public TaskGroupState? GetGroupState(string groupId)
        {
            return _groups.TryGetValue(groupId, out var state) ? state : null;
        }

        public IReadOnlyCollection<string> GetActiveGroupIds()
        {
            return _groups.Keys.ToList();
        }

        public string GenerateAggregateSummary(TaskGroupState state)
        {
            var lines = new List<string>();
            lines.Add($"=== Group Summary: {state.GroupName} ===");
            lines.Add($"Total: {state.TotalCount} | Completed: {state.CompletedCount} | Failed: {state.FailedCount}");

            var elapsed = (state.Tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value)
                .DefaultIfEmpty(DateTime.Now)
                .Max()) - state.StartTime;
            lines.Add($"Duration: {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s");
            lines.Add("");

            foreach (var task in state.Tasks)
            {
                var duration = task.EndTime.HasValue
                    ? (task.EndTime.Value - task.StartTime)
                    : TimeSpan.Zero;
                lines.Add($"--- Task #{task.TaskNumber:D4}: {task.Description} ---");
                lines.Add($"Status: {task.Status} | Duration: {(int)duration.TotalMinutes}m {duration.Seconds}s");
                if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                    lines.Add($"Summary: {task.CompletionSummary}");
                if (!string.IsNullOrWhiteSpace(task.Recommendations))
                    lines.Add($"Recommendations: {task.Recommendations}");
                lines.Add("");
            }

            // Combined recommendations
            var allRecs = state.Tasks
                .Where(t => !string.IsNullOrWhiteSpace(t.Recommendations))
                .Select(t => $"[#{t.TaskNumber:D4}] {t.Recommendations}")
                .ToList();
            if (allRecs.Count > 0)
            {
                lines.Add("=== Combined Recommendations ===");
                lines.AddRange(allRecs);
            }

            return string.Join("\n", lines);
        }
    }
}
