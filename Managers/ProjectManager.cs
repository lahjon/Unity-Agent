using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Spritely.Helpers;
using Spritely.Dialogs;
using Spritely.Models;

namespace Spritely.Managers
{
    public class ProjectManager : IProjectDataProvider
    {
        private List<ProjectEntry> _savedProjects = new();
        private string _projectPath;

        private readonly IProjectPanelView _view;
        private SettingsManager? _settingsManager;

        // ── Sub-managers ──
        public ProjectColorManager Colors { get; }
        public ProjectDescriptionManager Descriptions { get; }
        public ProjectRulesManager Rules { get; }
        public McpConfigManager Mcp { get; }
        public ProjectPersistenceManager Persistence { get; }
        public ProjectSwitchManager Switch { get; }
        public ProjectListRenderer ListRenderer { get; }

        // ── Events (forwarded from sub-managers) ──
        public event Action<string>? McpOutputChanged;
        public event Action? ProjectSwapStarted
        {
            add => Switch.ProjectSwapStarted += value;
            remove => Switch.ProjectSwapStarted -= value;
        }
        public event Action? ProjectSwapCompleted
        {
            add => Switch.ProjectSwapCompleted += value;
            remove => Switch.ProjectSwapCompleted -= value;
        }
        public event Action<string, string>? ProjectRenamed
        {
            add => ListRenderer.ProjectRenamed += value;
            remove => ListRenderer.ProjectRenamed -= value;
        }

        // ── IProjectDataProvider ──
        public string ProjectPath
        {
            get => _projectPath;
            set => _projectPath = value;
        }

        public void SetProjectPath(string path) => _projectPath = path;
        public List<ProjectEntry> SavedProjects => _savedProjects;
        public IProjectPanelView View => _view;
        public bool HasProjects => _savedProjects.Count > 0;

        public ProjectManager(
            string appDataDir,
            string initialProjectPath,
            IProjectPanelView view)
        {
            _projectPath = initialProjectPath;
            _view = view;

            Persistence = new ProjectPersistenceManager(appDataDir);
            Colors = new ProjectColorManager(this);
            Descriptions = new ProjectDescriptionManager(this);
            Rules = new ProjectRulesManager(this);
            Mcp = new McpConfigManager(this, Colors);
            Switch = new ProjectSwitchManager(this);
            ListRenderer = new ProjectListRenderer(this, Colors, Mcp, Switch, GetProjectStats);

            Mcp.McpOutputChanged += path => McpOutputChanged?.Invoke(path);
        }

        public void SetTaskFactory(ITaskFactory taskFactory)
        {
            Descriptions.SetTaskFactory(taskFactory);
            Rules.SetTaskFactory(taskFactory);
        }

        public void SetSettingsManager(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager;
        }

        public void SetTaskCollections(
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            ListRenderer.SetTaskCollections(activeTasks, historyTasks);
        }

        // ── Stats ──

        public ProjectActivityStats GetProjectStats(string projectPath, IEnumerable<AgentTask> allTasks)
        {
            var normPath = NormalizePath(projectPath);
            var tasks = allTasks
                .Where(t => NormalizePath(t.ProjectPath) == normPath)
                .ToList();

            var completed = tasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var failed = tasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var cancelled = tasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var finishedCount = completed + failed;

            var durations = tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value - t.StartTime)
                .Where(d => d > TimeSpan.Zero)
                .ToList();

            var project = _savedProjects.FirstOrDefault(p =>
                NormalizePath(p.Path) == normPath);

            var displayName = project?.DisplayName
                ?? (string.IsNullOrEmpty(normPath) ? "Unassigned" : Path.GetFileName(normPath));
            var color = project?.Color;
            if (string.IsNullOrEmpty(color)) color = "#666666";

            var totalInputTokens = tasks.Sum(t => t.InputTokens);
            var totalOutputTokens = tasks.Sum(t => t.OutputTokens);

            var mostRecent = tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value)
                .DefaultIfEmpty()
                .Max();

            return new ProjectActivityStats
            {
                ProjectPath = normPath,
                ProjectName = displayName,
                ProjectColor = color,
                TotalTasks = tasks.Count,
                CompletedTasks = completed,
                FailedTasks = failed,
                CancelledTasks = cancelled,
                SuccessRate = finishedCount > 0 ? (double)completed / finishedCount : 0,
                FailureRate = finishedCount > 0 ? (double)failed / finishedCount : 0,
                AverageDuration = durations.Count > 0
                    ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks))
                    : TimeSpan.Zero,
                TotalDuration = durations.Count > 0
                    ? TimeSpan.FromTicks(durations.Sum(d => d.Ticks))
                    : TimeSpan.Zero,
                ShortestDuration = durations.Count > 0 ? durations.Min() : null,
                LongestDuration = durations.Count > 0 ? durations.Max() : null,
                TotalInputTokens = totalInputTokens,
                TotalOutputTokens = totalOutputTokens,
                MostRecentTaskTime = mostRecent == default ? null : mostRecent
            };
        }

        private static string NormalizePath(string? path) => FormatHelpers.NormalizePath(path);

        // ── Persistence ──

        public async Task LoadProjectsAsync()
        {
            _savedProjects = await Persistence.LoadAsync();
            if (Colors.BackfillColors()) SaveProjects();

            foreach (var entry in _savedProjects)
                Rules.SeedRulesHash(entry);

            _view.ViewDispatcher.Invoke(() =>
            {
                RefreshProjectCombo();
                Mcp.UpdateMcpToggleForProject();
            });
        }

        public void SaveProjects() => Persistence.Save(_savedProjects);

        // ── Refresh ──

        public void RefreshProjectCombo()
        {
            var combo = _view.PromptProjectLabel;
            combo.SelectionChanged -= Switch.OnPromptProjectComboChanged;

            combo.Items.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < _savedProjects.Count; i++)
            {
                var p = _savedProjects[i];
                combo.Items.Add(new ComboBoxItem
                {
                    Content = p.DisplayName,
                    Tag = p.Path,
                    ToolTip = p.Path
                });
                if (p.Path == _projectPath)
                    selectedIndex = i;
            }

            if (selectedIndex >= 0)
                combo.SelectedIndex = selectedIndex;

            combo.SelectionChanged += Switch.OnPromptProjectComboChanged;
            Descriptions.RefreshDescriptionBoxes();
        }

        public void RefreshProjectList(
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
            => ListRenderer.RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);

        // ── Delegating wrappers (preserve existing public API) ──

        public void RefreshDescriptionBoxes() => Descriptions.RefreshDescriptionBoxes();
        public string GetProjectDescription(AgentTask task) => Descriptions.GetProjectDescription(task);
        public async Task RegenerateProjectDescriptionAsync(ProjectEntry entry) => await Descriptions.RegenerateProjectDescriptionAsync(entry);
        public async Task GenerateProjectDescriptionInBackground(ProjectEntry entry) => await Descriptions.GenerateProjectDescriptionInBackground(entry);
        public void RegenerateDescriptions() => Descriptions.RegenerateDescriptions();
        public void SaveShortDesc() => Descriptions.SaveShortDesc();
        public void SaveLongDesc() => Descriptions.SaveLongDesc();

        public void SaveRuleInstruction() => Rules.SaveRuleInstruction();
        public void AddProjectRule(string rule) { Rules.AddProjectRule(rule); Descriptions.RefreshDescriptionBoxes(); }
        public void RemoveProjectRule(string rule) { Rules.RemoveProjectRule(rule); Descriptions.RefreshDescriptionBoxes(); }
        public string GetProjectRulesBlock(string projectPath) => Rules.GetProjectRulesBlock(projectPath);
        public static string GetDefaultCrashLogPath() => ProjectRulesManager.GetDefaultCrashLogPath();
        public static string GetDefaultAppLogPath() => ProjectRulesManager.GetDefaultAppLogPath();
        public static string GetDefaultHangLogPath() => ProjectRulesManager.GetDefaultHangLogPath();
        public List<string> GetCrashLogPaths(string projectPath) => Rules.GetCrashLogPaths(projectPath);
        public void SaveCrashLogPaths(string crashLogPath, string appLogPath, string hangLogPath) => Rules.SaveCrashLogPaths(crashLogPath, appLogPath, hangLogPath);
        public bool IsGameProject(string projectPath) => Rules.IsGameProject(projectPath);
        public void NotifyRulesChanged(ProjectEntry entry) => Rules.NotifyRulesChanged(entry);

        public string GetProjectColor(string projectPath) => Colors.GetProjectColor(projectPath);
        public string GetProjectDisplayName(string projectPath) => Colors.GetProjectDisplayName(projectPath);

        public void UpdateMcpToggleForProject() => Mcp.UpdateMcpToggleForProject();
        public async Task ConnectMcpAsync(string projectPath) => await Mcp.ConnectMcpAsync(projectPath);
        public void DisconnectMcp(string projectPath) => Mcp.DisconnectMcp(projectPath);
        public void StopMcpServer(ProjectEntry entry) => Mcp.StopMcpServer(entry);
        public void StopAllMcpServers() => Mcp.StopAllMcpServers();
        public void NotifyMcpOutputChanged(string projectPath) => Mcp.NotifyMcpOutputChanged(projectPath);

        // ── Project CRUD ──

        public ProjectEntry? GetCurrentProject()
        {
            return _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
        }

        public void HandleAddProjectPathClick(Action<string> updateTerminalWorkingDirectory, Action saveSettings, Action syncSettings)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a project folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                AddProject(dialog.SelectedPath, updateTerminalWorkingDirectory, saveSettings, syncSettings);
            }
        }

        public void AddProject(Action<string> updateTerminalWorkingDirectory, Action saveSettings, Action syncSettings)
            => AddProject("", updateTerminalWorkingDirectory, saveSettings, syncSettings);

        private void AddProject(string path, Action<string> updateTerminalWorkingDirectory, Action saveSettings, Action syncSettings)
        {
            if (string.IsNullOrEmpty(path))
            {
                DarkDialog.ShowAlert("Please select a project folder.", "No Folder Selected");
                return;
            }
            if (!Directory.Exists(path))
            {
                DarkDialog.ShowAlert("The selected path does not exist or is invalid.", "Invalid Path");
                return;
            }
            if (_savedProjects.Any(p => p.Path == path))
            {
                DarkDialog.ShowAlert("This project path is already added.", "Duplicate");
                return;
            }

            var name = Path.GetFileName(path);
            var entry = new ProjectEntry
            {
                Name = name,
                Path = path,
                IsInitializing = true,
                Color = Colors.PickProjectColor(),
                McpServerName = _settingsManager?.DefaultMcpServerName ?? "UnityMCP",
                McpAddress = _settingsManager?.DefaultMcpAddress ?? "http://127.0.0.1:8080/mcp",
                McpStartCommand = _settingsManager?.DefaultMcpStartCommand ?? @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools"
            };
            _savedProjects.Add(entry);
            SaveProjects();
            _projectPath = path;
            updateTerminalWorkingDirectory(path);
            saveSettings();
            syncSettings();

            _ = InitializeNewProjectAsync(entry);
        }

        public void CreateProject(Action<string> updateTerminalWorkingDirectory, Action saveSettings, Action syncSettings)
        {
            var result = Dialogs.CreateProjectDialog.Show();
            if (result == null) return;

            var fullPath = result.Path;

            if (_savedProjects.Any(p => p.Path == fullPath))
            {
                DarkDialog.ShowAlert("A project with this path already exists.", "Duplicate");
                return;
            }

            try
            {
                if (!Directory.Exists(fullPath))
                    Directory.CreateDirectory(fullPath);
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Failed to create directory:\n{ex.Message}", "Error");
                return;
            }

            if (result.InitGit)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "init",
                        WorkingDirectory = fullPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(10000);
                    if (proc?.ExitCode != 0)
                    {
                        var err = proc?.StandardError.ReadToEnd() ?? "Unknown error";
                        DarkDialog.ShowAlert($"Git init failed:\n{err}", "Git Error");
                    }
                }
                catch (Exception ex)
                {
                    DarkDialog.ShowAlert($"Could not run git init:\n{ex.Message}", "Git Error");
                }
            }

            var entry = new ProjectEntry
            {
                Name = result.Name,
                Path = fullPath,
                IsInitializing = true,
                Color = Colors.PickProjectColor(),
                IsGame = result.IsGame,
                McpServerName = _settingsManager?.DefaultMcpServerName ?? "UnityMCP",
                McpAddress = _settingsManager?.DefaultMcpAddress ?? "http://127.0.0.1:8080/mcp",
                McpStartCommand = _settingsManager?.DefaultMcpStartCommand ?? @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools",
                McpStatus = result.IsGame ? McpStatus.NotConnected : McpStatus.Disabled
            };
            _savedProjects.Add(entry);
            SaveProjects();
            _projectPath = fullPath;
            updateTerminalWorkingDirectory(fullPath);
            saveSettings();
            syncSettings();

            _ = InitializeNewProjectAsync(entry);
        }

        public void RemoveProject(string projectPath, Action<string> updateTerminalWorkingDirectory,
            Action saveSettings, Action syncSettings)
        {
            if (!DarkDialog.ShowConfirm($"Remove this project from the list?\n\n{projectPath}", "Remove Project"))
                return;

            _savedProjects.RemoveAll(p => p.Path == projectPath);
            SaveProjects();
            _projectPath = _savedProjects.Count > 0 ? _savedProjects[0].Path : Directory.GetCurrentDirectory();
            updateTerminalWorkingDirectory(_projectPath);
            saveSettings();
            syncSettings();
        }

        // ── Project initialization ──

        private async Task InitializeNewProjectAsync(ProjectEntry entry)
        {
            await Descriptions.GenerateProjectDescriptionInBackground(entry);

            var claudeTask = Descriptions.EnsureClaudeMdAsync(entry);
            var featureTask = InitializeFeaturesAsync(entry);
            await Task.WhenAll(claudeTask, featureTask);
        }

        private async Task InitializeFeaturesAsync(ProjectEntry entry)
        {
            try
            {
                if (entry.IsFeatureRegistryInitialized) return;

                AppLogger.Info("ProjectManager", $"Initializing feature registry for {entry.DisplayName}...");
                var registryManager = new FeatureRegistryManager();
                var initializer = new FeatureInitializer(registryManager);
                initializer.ProgressChanged += msg => AppLogger.Info("FeatureInit", $"[{entry.DisplayName}] {msg}");

                var result = await initializer.InitializeAsync(entry.Path);
                if (result != null)
                {
                    AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {entry.DisplayName}");
                    _view.ViewDispatcher.Invoke(() => RefreshProjectList(null, null, null));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", $"Feature initialization failed for {entry.DisplayName}", ex);
            }
        }

        public async Task ForceInitializeProjectAsync(ProjectEntry entry)
        {
            entry.IsInitializing = true;
            entry.ShortDescription = "";
            entry.LongDescription = "";
            SaveProjects();
            _view.ViewDispatcher.Invoke(() =>
            {
                RefreshProjectCombo();
                RefreshProjectList(null, null, null);
            });

            await Descriptions.GenerateProjectDescriptionInBackground(entry);

            var claudeTask = Descriptions.EnsureClaudeMdAsync(entry);
            var featureTask = ForceInitializeFeaturesAsync(entry);
            await Task.WhenAll(claudeTask, featureTask);
        }

        public async Task ForceInitializeFeaturesAsync(ProjectEntry entry, Action<string>? onProgress = null)
        {
            try
            {
                AppLogger.Info("ProjectManager", $"Force-initializing feature registry for {entry.DisplayName}...");
                var registryManager = new FeatureRegistryManager();
                var initializer = new FeatureInitializer(registryManager);
                initializer.ProgressChanged += msg =>
                {
                    AppLogger.Info("FeatureInit", $"[{entry.DisplayName}] {msg}");
                    onProgress?.Invoke(msg);
                };

                var result = await initializer.InitializeAsync(entry.Path);
                if (result != null)
                {
                    AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {entry.DisplayName}");
                    _view.ViewDispatcher.Invoke(() => RefreshProjectList(null, null, null));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", $"Feature initialization failed for {entry.DisplayName}", ex);
                throw;
            }
        }
    }
}
