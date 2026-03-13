using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Spritely.Helpers;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages the IDE integration panel in the Statistics tabs.
    /// Shows per-task file changes, inline diffs, and allows browsing modified files.
    /// </summary>
    public partial class IdePanelManager : IDisposable
    {
        private readonly IGitHelper _gitHelper;
        private readonly Func<string> _getProjectPath;
        private readonly FileLockManager _fileLockManager;
        private readonly Func<IEnumerable<AgentTask>> _getActiveTasks;
        private readonly Func<IEnumerable<AgentTask>> _getHistoryTasks;
        private readonly Dispatcher _dispatcher;
        private bool _isDirty = true;
        private bool _isDisposed;
        private bool _isRefreshing;

        // Currently selected task for detail view
        private AgentTask? _selectedTask;

        // Cached file changes for selected task
        private List<IdeFileEntry> _fileEntries = new();

        // Currently expanded file (showing diff)
        private string? _expandedFilePath;

        // Cached diff content per file
        private readonly Dictionary<string, string> _diffCache = new();

        // UI cache
        private StackPanel? _cachedRoot;
        private StackPanel? _taskListSection;
        private Border? _detailSection;
        private bool _uiCacheValid;

        public IdePanelManager(
            IGitHelper gitHelper,
            Func<string> getProjectPath,
            FileLockManager fileLockManager,
            Func<IEnumerable<AgentTask>> getActiveTasks,
            Func<IEnumerable<AgentTask>> getHistoryTasks,
            Dispatcher dispatcher)
        {
            _gitHelper = gitHelper;
            _getProjectPath = getProjectPath;
            _fileLockManager = fileLockManager;
            _getActiveTasks = getActiveTasks;
            _getHistoryTasks = getHistoryTasks;
            _dispatcher = dispatcher;
        }

        public void MarkDirty()
        {
            _isDirty = true;
            _uiCacheValid = false;
        }

        public void OnTaskCompleted(string taskId) => MarkDirty();

        public void RefreshIfNeeded(ScrollViewer container)
        {
            if (!_isDirty) return;
            _isDirty = false;
            _ = RefreshAsync(container);
        }

        public async Task RefreshAsync(ScrollViewer container)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                // Collect tasks with changed files
                var tasks = GetTasksWithChanges();

                // If a task is selected, load its file changes
                if (_selectedTask != null)
                {
                    await LoadFileEntriesAsync(_selectedTask);
                }

                _dispatcher.Invoke(() =>
                {
                    container.Content = BuildContent(tasks);
                    _uiCacheValid = true;
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("IdePanelManager", "Failed to refresh IDE panel", ex);
                _dispatcher.Invoke(() =>
                {
                    container.Content = BuildErrorContent(ex.Message);
                });
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        public void SelectTask(AgentTask task, ScrollViewer container)
        {
            _selectedTask = task;
            _expandedFilePath = null;
            _diffCache.Clear();
            MarkDirty();
            _ = RefreshAsync(container);
        }

        public void ClearSelection(ScrollViewer container)
        {
            _selectedTask = null;
            _expandedFilePath = null;
            _diffCache.Clear();
            _fileEntries.Clear();
            MarkDirty();
            _ = RefreshAsync(container);
        }

        public void ToggleFileDiff(string filePath, ScrollViewer container)
        {
            _expandedFilePath = _expandedFilePath == filePath ? null : filePath;
            _uiCacheValid = false;
            _ = RefreshAsync(container);
        }

        private List<AgentTask> GetTasksWithChanges()
        {
            var result = new List<AgentTask>();
            var seen = new HashSet<string>();

            foreach (var task in _getActiveTasks())
            {
                if ((task.ChangedFiles.Count > 0 || task.IsRunning || task.IsCommitting) && seen.Add(task.Id))
                    result.Add(task);
            }

            foreach (var task in _getHistoryTasks())
            {
                if (task.ChangedFiles.Count > 0 && seen.Add(task.Id))
                    result.Add(task);
            }

            // Sort: running first, then by task number descending
            result.Sort((a, b) =>
            {
                if (a.IsRunning && !b.IsRunning) return -1;
                if (!a.IsRunning && b.IsRunning) return 1;
                return b.TaskNumber.CompareTo(a.TaskNumber);
            });

            return result;
        }

        private async Task LoadFileEntriesAsync(AgentTask task)
        {
            _fileEntries.Clear();
            var projectPath = task.ProjectPath;

            if (string.IsNullOrEmpty(projectPath) || task.ChangedFiles.Count == 0)
                return;

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativePath in task.ChangedFiles)
            {
                if (!seenFiles.Add(relativePath))
                    continue;

                var fullPath = Path.Combine(projectPath, relativePath);
                var entry = new IdeFileEntry
                {
                    RelativePath = relativePath,
                    FullPath = fullPath,
                    FileName = Path.GetFileName(relativePath),
                    Directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "",
                    Extension = Path.GetExtension(relativePath).TrimStart('.').ToUpperInvariant(),
                    Exists = File.Exists(fullPath)
                };

                // Determine status from git
                entry.Status = entry.Exists ? "M" : "D";

                // Try to get line stats if we have a git start hash
                if (!string.IsNullOrEmpty(task.GitStartHash))
                {
                    try
                    {
                        var statsResult = await _gitHelper.RunGitCommandAsync(
                            projectPath,
                            $"diff {task.GitStartHash} -- \"{relativePath}\" --stat",
                            CancellationToken.None);

                        if (statsResult.IsSuccess && !string.IsNullOrEmpty(statsResult.Output))
                        {
                            var lines = statsResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                if (line.Contains('|'))
                                {
                                    var parts = line.Split('|');
                                    if (parts.Length >= 2)
                                    {
                                        var statPart = parts[1].Trim();
                                        entry.LinesAdded = statPart.Count(c => c == '+');
                                        entry.LinesRemoved = statPart.Count(c => c == '-');
                                    }
                                }
                            }
                        }
                    }
                    catch { /* ignore stat failures */ }
                }

                _fileEntries.Add(entry);
            }

            // Sort by directory then filename
            _fileEntries.Sort((a, b) =>
            {
                var dirCmp = string.Compare(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);
                return dirCmp != 0 ? dirCmp : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
        }

        public async Task<string> GetFileDiffAsync(AgentTask task, string relativePath)
        {
            if (_diffCache.TryGetValue(relativePath, out var cached))
                return cached;

            var projectPath = task.ProjectPath;
            if (string.IsNullOrEmpty(projectPath))
                return "No project path available.";

            try
            {
                string diffArgs;
                if (!string.IsNullOrEmpty(task.GitStartHash))
                    diffArgs = $"diff {task.GitStartHash} -- \"{relativePath}\"";
                else if (!string.IsNullOrEmpty(task.CommitHash))
                    diffArgs = $"diff {task.CommitHash}~1 {task.CommitHash} -- \"{relativePath}\"";
                else
                    diffArgs = $"diff HEAD -- \"{relativePath}\"";

                var result = await _gitHelper.RunGitCommandAsync(projectPath, diffArgs, CancellationToken.None);

                var diff = result.IsSuccess && !string.IsNullOrEmpty(result.Output)
                    ? result.Output
                    : "(no changes detected)";

                _diffCache[relativePath] = diff;
                return diff;
            }
            catch (Exception ex)
            {
                var error = $"Error loading diff: {ex.Message}";
                _diffCache[relativePath] = error;
                return error;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
    }

    /// <summary>Represents a file entry in the IDE panel's file tree.</summary>
    public class IdeFileEntry
    {
        public string RelativePath { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Directory { get; set; } = "";
        public string Extension { get; set; } = "";
        public string Status { get; set; } = "M";
        public bool Exists { get; set; } = true;
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
    }
}
