using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using UnityAgent.Models;

namespace UnityAgent.Managers
{
    public class HistoryManager
    {
        private readonly string _historyFile;

        public HistoryManager(string appDataDir)
        {
            _historyFile = Path.Combine(appDataDir, "task_history.json");
        }

        public void SaveHistory(ObservableCollection<AgentTask> historyTasks)
        {
            try
            {
                var dir = Path.GetDirectoryName(_historyFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var entries = historyTasks.Select(t => new TaskHistoryEntry
                {
                    Description = t.Description,
                    Status = t.Status.ToString(),
                    StartTime = t.StartTime,
                    EndTime = t.EndTime,
                    SkipPermissions = t.SkipPermissions,
                    RemoteSession = t.RemoteSession,
                    ProjectPath = t.ProjectPath,
                    IsOvernight = t.IsOvernight,
                    MaxIterations = t.MaxIterations,
                    CurrentIteration = t.CurrentIteration,
                    CompletionSummary = t.CompletionSummary
                }).ToList();

                File.WriteAllText(_historyFile,
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void LoadHistory(ObservableCollection<AgentTask> historyTasks, int retentionHours)
        {
            try
            {
                if (!File.Exists(_historyFile)) return;
                var entries = JsonSerializer.Deserialize<List<TaskHistoryEntry>>(
                    File.ReadAllText(_historyFile));
                if (entries == null) return;

                var cutoff = DateTime.Now.AddHours(-retentionHours);
                foreach (var entry in entries.Where(e => e.StartTime > cutoff))
                {
                    var task = new AgentTask
                    {
                        Description = entry.Description,
                        SkipPermissions = entry.SkipPermissions,
                        RemoteSession = entry.RemoteSession,
                        ProjectPath = entry.ProjectPath ?? "",
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime,
                        IsOvernight = entry.IsOvernight,
                        MaxIterations = entry.MaxIterations > 0 ? entry.MaxIterations : 50,
                        CurrentIteration = entry.CurrentIteration,
                        CompletionSummary = entry.CompletionSummary ?? ""
                    };
                    task.Status = Enum.TryParse<AgentTaskStatus>(entry.Status, out var s)
                        ? s : AgentTaskStatus.Completed;

                    historyTasks.Add(task);
                }
            }
            catch { }
        }

        public void CleanupOldHistory(
            ObservableCollection<AgentTask> historyTasks,
            Dictionary<string, TabItem> tabs,
            TabControl outputTabs,
            Dictionary<string, System.Windows.Controls.TextBox> outputBoxes,
            int retentionHours)
        {
            var cutoff = DateTime.Now.AddHours(-retentionHours);
            var stale = historyTasks.Where(t => t.StartTime < cutoff).ToList();
            foreach (var task in stale)
            {
                historyTasks.Remove(task);
                if (tabs.TryGetValue(task.Id, out var tab))
                {
                    outputTabs.Items.Remove(tab);
                    tabs.Remove(task.Id);
                }
                outputBoxes.Remove(task.Id);
            }
            if (stale.Count > 0)
            {
                SaveHistory(historyTasks);
            }
        }
    }
}
