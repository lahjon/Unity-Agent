using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public class ProjectTaskManager
    {
        private readonly string _tasksFile;
        private readonly List<ProjectTaskItem> _tasks = new();

        public List<ProjectTaskItem> Tasks => _tasks;

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
                    _tasks.Clear();
                    _tasks.AddRange(tasks.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt));
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
                var json = JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true });
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

            var task = new ProjectTaskItem { Text = text.Trim() };
            _tasks.Add(task);
            SaveTasks();
            return task; // Return the added task to avoid accessing the list directly
        }

        public void CompleteTask(ProjectTaskItem task)
        {
            task.IsCompleted = true;
            task.CompletedAt = DateTime.UtcNow;
            SaveTasks();
        }

        public void RemoveTask(ProjectTaskItem task)
        {
            _tasks.Remove(task);
            SaveTasks();
        }
    }
}