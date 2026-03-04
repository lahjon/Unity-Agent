using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Models;

namespace Spritely.Managers
{
    public class ProjectTaskManager
    {
        private readonly string _tasksFile;
        private readonly List<ProjectTaskItem> _allTasks = new();
        private string _currentProjectPath = string.Empty;

        /// <summary>Returns only the tasks for the current project (excludes completed).</summary>
        public List<ProjectTaskItem> Tasks =>
            _allTasks.Where(t => !t.IsCompleted && NormalizePath(t.ProjectPath) == NormalizePath(_currentProjectPath)).ToList();

        public string CurrentProjectPath
        {
            get => _currentProjectPath;
            set => _currentProjectPath = value;
        }

        public ProjectTaskManager(string appDataDir)
        {
            _tasksFile = Path.Combine(appDataDir, "projecttasks.json");
        }

        public async Task LoadTasksAsync()
        {
            try
            {
                if (!File.Exists(_tasksFile)) return;
                var json = await File.ReadAllTextAsync(_tasksFile).ConfigureAwait(false);
                var tasks = JsonSerializer.Deserialize<List<ProjectTaskItem>>(json);
                if (tasks != null)
                {
                    _allTasks.Clear();
                    _allTasks.AddRange(tasks.OrderBy(t => t.CreatedAt));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectTaskManager", "Failed to load project tasks", ex);
            }
        }

        public void SaveTasks()
        {
            try
            {
                // Save all tasks (all projects), but exclude completed ones
                var toSave = _allTasks.Where(t => !t.IsCompleted).ToList();
                var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
                SafeFileWriter.WriteInBackground(_tasksFile, json, "ProjectTaskManager");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectTaskManager", "Failed to save project tasks", ex);
            }
        }

        public async Task SaveTasksAsync()
        {
            await Task.Run(() => SaveTasks());
        }

        public ProjectTaskItem AddTask(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Task text cannot be empty", nameof(text));
            if (string.IsNullOrEmpty(_currentProjectPath))
                throw new InvalidOperationException("No project selected");

            var task = new ProjectTaskItem
            {
                Text = text.Trim(),
                ProjectPath = _currentProjectPath
            };
            _allTasks.Add(task);
            SaveTasks();
            return task;
        }

        public void CompleteTask(ProjectTaskItem task)
        {
            task.IsCompleted = true;
            task.CompletedAt = DateTime.UtcNow;
            SaveTasks();
        }

        public void RemoveTask(ProjectTaskItem task)
        {
            _allTasks.Remove(task);
            SaveTasks();
        }

        private static string NormalizePath(string path) =>
            path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }
}