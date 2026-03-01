using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public class HistoryManager
    {
        private readonly string _historyFile;
        private readonly string _storedTasksFile;
        private readonly string _activeQueueFile;
        private readonly object _historyLock;
        private readonly object _storedLock;

        public HistoryManager(string appDataDir, object historyLock, object storedLock)
        {
            _historyFile = Path.Combine(appDataDir, "task_history.json");
            _storedTasksFile = Path.Combine(appDataDir, "stored_tasks.json");
            _activeQueueFile = Path.Combine(appDataDir, "active_queue.json");
            _historyLock = historyLock;
            _storedLock = storedLock;
        }

        public void SaveHistory(ObservableCollection<AgentTask> historyTasks)
        {
            try
            {
                var dir = Path.GetDirectoryName(_historyFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Snapshot under lock so background writes never race with collection changes
                List<TaskHistoryEntry> entries;
                lock (_historyLock)
                {
                    entries = historyTasks.Select(t => new TaskHistoryEntry
                    {
                        Description = t.Description,
                        Summary = t.Summary ?? "",
                        StoredPrompt = t.StoredPrompt ?? "",
                        ConversationId = t.ConversationId ?? "",
                        Status = t.Status.ToString(),
                        StartTime = t.StartTime,
                        EndTime = t.EndTime,
                        SkipPermissions = t.SkipPermissions,
                        RemoteSession = t.RemoteSession,
                        ProjectPath = t.ProjectPath,
                        ProjectColor = t.ProjectColor,
                        ProjectDisplayName = t.ProjectDisplayName,
                        IsFeatureMode = t.IsFeatureMode,
                        MaxIterations = t.MaxIterations,
                        CurrentIteration = t.CurrentIteration,
                        CompletionSummary = t.CompletionSummary,
                        Recommendations = t.Recommendations ?? "",
                        GroupId = t.GroupId,
                        GroupName = t.GroupName,
                        InputTokens = t.InputTokens,
                        OutputTokens = t.OutputTokens,
                        CacheReadTokens = t.CacheReadTokens,
                        CacheCreationTokens = t.CacheCreationTokens
                    }).ToList();
                }

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_historyFile, json, "HistoryManager");
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to save task history", ex); }
        }

        public async Task<List<AgentTask>> LoadHistoryAsync(int retentionHours)
        {
            var results = new List<AgentTask>();
            try
            {
                if (!File.Exists(_historyFile)) return results;
                var json = await File.ReadAllTextAsync(_historyFile).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<List<TaskHistoryEntry>>(json);
                if (entries == null) return results;

                var cutoff = DateTime.Now.AddHours(-retentionHours);
                foreach (var entry in entries.Where(e => e.StartTime > cutoff))
                {
                    var task = new AgentTask
                    {
                        Description = entry.Description,
                        StoredPrompt = string.IsNullOrEmpty(entry.StoredPrompt) ? null : entry.StoredPrompt,
                        ConversationId = string.IsNullOrEmpty(entry.ConversationId) ? null : entry.ConversationId,
                        SkipPermissions = entry.SkipPermissions,
                        RemoteSession = entry.RemoteSession,
                        ProjectPath = entry.ProjectPath ?? "",
                        ProjectColor = entry.ProjectColor ?? "#666666",
                        ProjectDisplayName = entry.ProjectDisplayName ?? "",
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime,
                        IsFeatureMode = entry.IsFeatureMode,
                        MaxIterations = entry.MaxIterations > 0 ? entry.MaxIterations : 2,
                        CurrentIteration = entry.CurrentIteration,
                        CompletionSummary = entry.CompletionSummary ?? "",
                        Recommendations = entry.Recommendations ?? "",
                        GroupId = entry.GroupId,
                        GroupName = entry.GroupName,
                        InputTokens = entry.InputTokens,
                        OutputTokens = entry.OutputTokens,
                        CacheReadTokens = entry.CacheReadTokens,
                        CacheCreationTokens = entry.CacheCreationTokens
                    };
                    task.Summary = entry.Summary ?? "";
                    task.Status = Enum.TryParse<AgentTaskStatus>(entry.Status, out var s)
                        ? s : AgentTaskStatus.Completed;

                    results.Add(task);
                }
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to load task history", ex); }
            return results;
        }

        public void CleanupOldHistory(
            ObservableCollection<AgentTask> historyTasks,
            Dictionary<string, TabItem> tabs,
            TabControl outputTabs,
            Dictionary<string, System.Windows.Controls.RichTextBox> outputBoxes,
            int retentionHours)
        {
            var cutoff = DateTime.Now.AddHours(-retentionHours);
            List<AgentTask> stale;
            lock (_historyLock)
            {
                stale = historyTasks.Where(t => t.StartTime < cutoff).ToList();
                foreach (var task in stale)
                {
                    historyTasks.Remove(task);
                }
            }
            foreach (var task in stale)
            {
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

        /// <summary>
        /// Persists active tasks that are in InitQueued, Queued, or Planning state so they
        /// can be recovered on the next startup if the app crashes or is closed unexpectedly.
        /// </summary>
        public void SaveActiveQueue(ObservableCollection<AgentTask> activeTasks, object activeTasksLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_activeQueueFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<TaskHistoryEntry> entries;
                lock (activeTasksLock)
                {
                    entries = activeTasks
                        .Where(t => t.Status is AgentTaskStatus.InitQueued
                                              or AgentTaskStatus.Queued
                                              or AgentTaskStatus.Planning)
                        .Select(t => new TaskHistoryEntry
                        {
                            WasActive = true,
                            Description = t.Description,
                            Summary = t.Summary ?? "",
                            StoredPrompt = t.StoredPrompt ?? "",
                            ConversationId = t.ConversationId ?? "",
                            Status = t.Status.ToString(),
                            StartTime = t.StartTime,
                            EndTime = t.EndTime,
                            SkipPermissions = t.SkipPermissions,
                            RemoteSession = t.RemoteSession,
                            ProjectPath = t.ProjectPath,
                            ProjectColor = t.ProjectColor,
                            ProjectDisplayName = t.ProjectDisplayName,
                            IsFeatureMode = t.IsFeatureMode,
                            MaxIterations = t.MaxIterations,
                            CurrentIteration = t.CurrentIteration,
                            CompletionSummary = t.CompletionSummary,
                            Recommendations = t.Recommendations ?? "",
                            GroupId = t.GroupId,
                            GroupName = t.GroupName,
                            InputTokens = t.InputTokens,
                            OutputTokens = t.OutputTokens,
                            CacheReadTokens = t.CacheReadTokens,
                            CacheCreationTokens = t.CacheCreationTokens,
                            // Task configuration for relaunch
                            Model = t.Model.ToString(),
                            Headless = t.Headless,
                            IgnoreFileLocks = t.IgnoreFileLocks,
                            UseMcp = t.UseMcp,
                            SpawnTeam = t.SpawnTeam,
                            ExtendedPlanning = t.ExtendedPlanning,
                            NoGitWrite = t.NoGitWrite,
                            PlanOnly = t.PlanOnly,
                            UseMessageBus = t.UseMessageBus,
                            AutoDecompose = t.AutoDecompose,
                            ApplyFix = t.ApplyFix,
                            AdditionalInstructions = t.AdditionalInstructions ?? ""
                        }).ToList();
                }

                if (entries.Count == 0)
                {
                    // No queued tasks — remove any stale queue file
                    if (File.Exists(_activeQueueFile))
                        File.Delete(_activeQueueFile);
                    return;
                }

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                // Write synchronously — this runs during Dispose, so background writes may not complete
                File.WriteAllText(_activeQueueFile, json);
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to save active queue for recovery", ex); }
        }

        /// <summary>
        /// Loads tasks that were in the active queue when the app last shut down.
        /// Returns empty list if no recovery file exists.
        /// </summary>
        public async Task<List<AgentTask>> LoadActiveQueueAsync()
        {
            var results = new List<AgentTask>();
            try
            {
                if (!File.Exists(_activeQueueFile)) return results;
                var json = await File.ReadAllTextAsync(_activeQueueFile).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<List<TaskHistoryEntry>>(json);
                if (entries == null) return results;

                foreach (var entry in entries.Where(e => e.WasActive))
                {
                    var task = new AgentTask
                    {
                        Description = entry.Description,
                        StoredPrompt = string.IsNullOrEmpty(entry.StoredPrompt) ? null : entry.StoredPrompt,
                        ConversationId = null, // Don't restore old conversation
                        SkipPermissions = entry.SkipPermissions,
                        RemoteSession = entry.RemoteSession,
                        ProjectPath = entry.ProjectPath ?? "",
                        ProjectColor = entry.ProjectColor ?? "#666666",
                        ProjectDisplayName = entry.ProjectDisplayName ?? "",
                        IsFeatureMode = entry.IsFeatureMode,
                        MaxIterations = entry.MaxIterations > 0 ? entry.MaxIterations : 2,
                        GroupId = entry.GroupId,
                        GroupName = entry.GroupName,
                        // Restore task configuration
                        Headless = entry.Headless,
                        IgnoreFileLocks = entry.IgnoreFileLocks,
                        UseMcp = entry.UseMcp,
                        SpawnTeam = entry.SpawnTeam,
                        ExtendedPlanning = entry.ExtendedPlanning,
                        NoGitWrite = entry.NoGitWrite,
                        PlanOnly = entry.PlanOnly,
                        UseMessageBus = entry.UseMessageBus,
                        AutoDecompose = entry.AutoDecompose,
                        ApplyFix = entry.ApplyFix,
                        AdditionalInstructions = entry.AdditionalInstructions ?? ""
                    };
                    task.Summary = entry.Summary ?? "";
                    task.Model = Enum.TryParse<ModelType>(entry.Model, out var m) ? m : ModelType.ClaudeCode;

                    results.Add(task);
                }
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to load active queue for recovery", ex); }
            return results;
        }

        /// <summary>
        /// Removes the active queue file after tasks have been successfully recovered or dismissed.
        /// </summary>
        public void ClearActiveQueue()
        {
            try
            {
                if (File.Exists(_activeQueueFile))
                    File.Delete(_activeQueueFile);
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to clear active queue file", ex); }
        }

        public void SaveStoredTasks(ObservableCollection<AgentTask> storedTasks)
        {
            try
            {
                var dir = Path.GetDirectoryName(_storedTasksFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Snapshot under lock so background writes never race with collection changes
                List<StoredTaskEntry> entries;
                lock (_storedLock)
                {
                    entries = storedTasks.Select(t => new StoredTaskEntry
                    {
                        Description = t.Description,
                        Summary = t.Summary,
                        StoredPrompt = t.StoredPrompt ?? "",
                        ConversationId = t.ConversationId ?? "",
                        FullOutput = t.FullOutput ?? "",
                        ProjectPath = t.ProjectPath,
                        ProjectColor = t.ProjectColor,
                        ProjectDisplayName = t.ProjectDisplayName,
                        CreatedAt = t.StartTime,
                        SkipPermissions = t.SkipPermissions
                    }).ToList();
                }

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_storedTasksFile, json, "HistoryManager");
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to save stored tasks", ex); }
        }

        public async Task<List<AgentTask>> LoadStoredTasksAsync()
        {
            var results = new List<AgentTask>();
            try
            {
                if (!File.Exists(_storedTasksFile)) return results;
                var json = await File.ReadAllTextAsync(_storedTasksFile).ConfigureAwait(false);
                var entries = JsonSerializer.Deserialize<List<StoredTaskEntry>>(json);
                if (entries == null) return results;

                foreach (var entry in entries)
                {
                    var task = new AgentTask
                    {
                        Description = entry.Description,
                        StoredPrompt = entry.StoredPrompt,
                        ConversationId = string.IsNullOrEmpty(entry.ConversationId) ? null : entry.ConversationId,
                        FullOutput = entry.FullOutput,
                        ProjectPath = entry.ProjectPath ?? "",
                        ProjectColor = entry.ProjectColor ?? "#666666",
                        ProjectDisplayName = entry.ProjectDisplayName ?? "",
                        SkipPermissions = entry.SkipPermissions,
                        StartTime = entry.CreatedAt
                    };
                    task.Summary = entry.Summary ?? "";
                    task.Status = AgentTaskStatus.Completed;

                    results.Add(task);
                }
            }
            catch (Exception ex) { AppLogger.Warn("HistoryManager", "Failed to load stored tasks", ex); }
            return results;
        }
    }
}
