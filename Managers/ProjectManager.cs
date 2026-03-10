using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Spritely.Helpers;
using Spritely.Dialogs;
using Spritely.Models;

namespace Spritely.Managers
{
    public class ProjectManager : IProjectDataProvider
    {
        private List<ProjectEntry> _savedProjects = new();
        private string _projectPath;
        private readonly string _projectsFile;
        private string _addProjectSelectedPath = "";
        private bool _isSwapping;
        private ObservableCollection<AgentTask>? _activeTasks;
        private ObservableCollection<AgentTask>? _historyTasks;

        // Stored callbacks so background RefreshProjectList(null,null,null) calls
        // don't wipe the swap handler's ability to sync the UI.
        private Action<string>? _storedUpdateTerminal;
        private Action? _storedSaveSettings;
        private Action? _storedSyncSettings;

        private static readonly JsonSerializerOptions _projectJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IProjectPanelView _view;
        private SettingsManager? _settingsManager;

        // ── Sub-managers ──
        public ProjectColorManager Colors { get; }
        public ProjectDescriptionManager Descriptions { get; }
        public ProjectRulesManager Rules { get; }
        public McpConfigManager Mcp { get; }

        // ── Events (forwarded from sub-managers where needed) ──
        public event Action<string>? McpOutputChanged;
        public event Action? ProjectSwapStarted;
        public event Action? ProjectSwapCompleted;
        public event Action<string, string>? ProjectRenamed;

        // ── IProjectDataProvider ──
        public string ProjectPath
        {
            get => _projectPath;
            set => _projectPath = value;
        }

        public List<ProjectEntry> SavedProjects => _savedProjects;
        public IProjectPanelView View => _view;
        public bool HasProjects => _savedProjects.Count > 0;

        public ProjectManager(
            string appDataDir,
            string initialProjectPath,
            IProjectPanelView view)
        {
            _projectsFile = Path.Combine(appDataDir, "projects.json");
            _projectPath = initialProjectPath;
            _view = view;

            Colors = new ProjectColorManager(this);
            Descriptions = new ProjectDescriptionManager(this);
            Rules = new ProjectRulesManager(this);
            Mcp = new McpConfigManager(this, Colors);

            // Forward sub-manager events
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
            _activeTasks = activeTasks;
            _historyTasks = historyTasks;
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

        private static string NormalizePath(string? path) => Helpers.FormatHelpers.NormalizePath(path);

        private static string FormatTokenCount(long count) => Helpers.FormatHelpers.FormatTokenCount(count);

        private static string FormatRelativeTime(DateTime time)
        {
            var elapsed = DateTime.Now - time;
            if (elapsed.TotalMinutes < 1) return "just now";
            if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            return time.ToString("MMM d");
        }

        // ── Persistence ──

        public async Task LoadProjectsAsync()
        {
            try
            {
                if (File.Exists(_projectsFile))
                {
                    var json = await File.ReadAllTextAsync(_projectsFile).ConfigureAwait(false);
                    try
                    {
                        _savedProjects = JsonSerializer.Deserialize<List<ProjectEntry>>(json, _projectJsonOptions) ?? new();
                    }
                    catch
                    {
                        var oldList = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                        _savedProjects = oldList.Select(p => new ProjectEntry { Path = p }).ToList();
                        SaveProjects();
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed to load projects", ex); _savedProjects = new(); }

            if (Colors.BackfillColors()) SaveProjects();

            // Seed rules hashes so first save after launch doesn't trigger spurious regen
            foreach (var entry in _savedProjects)
                Rules.SeedRulesHash(entry);

            _view.ViewDispatcher.Invoke(() =>
            {
                RefreshProjectCombo();
                Mcp.UpdateMcpToggleForProject();
            });
        }

        public void SaveProjects()
        {
            try
            {
                var json = JsonSerializer.Serialize(_savedProjects, _projectJsonOptions);
                SafeFileWriter.WriteInBackground(_projectsFile, json, "ProjectManager");
            }
            catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed to save projects", ex); }
        }

        // ── Refresh ──

        public void RefreshProjectCombo()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            _view.PromptProjectLabel.Text = proj?.DisplayName ?? Path.GetFileName(_projectPath);
            _view.PromptProjectLabel.ToolTip = _projectPath;
            Descriptions.RefreshDescriptionBoxes();
        }

        // ── Delegating wrappers (preserve existing public API for callers) ──

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
                _addProjectSelectedPath = dialog.SelectedPath;
                AddProject(updateTerminalWorkingDirectory, saveSettings, syncSettings);
            }
        }

        public void AddProject(Action<string> updateTerminalWorkingDirectory, Action saveSettings, Action syncSettings)
        {
            var path = _addProjectSelectedPath;

            if (string.IsNullOrEmpty(path))
            {
                DarkDialog.ShowAlert("Please select a project folder.", "No Folder Selected");
                return;
            }
            if (!Directory.Exists(path))
            {
                DarkDialog.ShowAlert("The selected path does not exist or is invalid.", "Invalid Path");
                _addProjectSelectedPath = "";
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

            _addProjectSelectedPath = "";

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
                if (entry.IsFeatureRegistryInitialized)
                    return;

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

        // ── Project list UI ──

        public void RefreshProjectList(
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            // Persist non-null callbacks so background refreshes (null,null,null)
            // don't break the project-swap click handler.
            if (updateTerminalWorkingDirectory != null) _storedUpdateTerminal = updateTerminalWorkingDirectory;
            if (saveSettings != null) _storedSaveSettings = saveSettings;
            if (syncSettings != null) _storedSyncSettings = syncSettings;

            if (_view.ProjectListPanel == null) return;

            _view.ViewDispatcher.Invoke(() =>
            {
                _view.ProjectListPanel.UpdateLayout();
            });

            _view.ProjectListPanel.Children.Clear();

            foreach (var proj in _savedProjects)
            {
                var isActive = proj.Path == _projectPath;

                var card = new Border
                {
                    Background = (Brush)Application.Current.FindResource("BgElevated"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    BorderBrush = isActive
                        ? (Brush)Application.Current.FindResource("Accent")
                        : (Brush)Application.Current.FindResource("BorderMedium"),
                    Cursor = Cursors.Hand,
                    MaxHeight = 200
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoPanel = new StackPanel();

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };

                var typeIcon = new TextBlock
                {
                    Text = proj.IsGame ? "\uE7FC" : "\uE770",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14,
                    Foreground = (Brush)Application.Current.FindResource("TextTabHeader"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = proj.IsGame ? "Game" : "App"
                };
                nameRow.Children.Add(typeIcon);

                var nameBrush = !string.IsNullOrEmpty(proj.Color)
                    ? BrushCache.Get(proj.Color)
                    : BrushCache.Theme("TextPrimary");
                var nameBlock = new TextBlock
                {
                    Text = proj.DisplayName,
                    Foreground = nameBrush,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.IBeam,
                    ToolTip = "Click to rename"
                };
                nameBlock.MouseEnter += (_, _) => nameBlock.TextDecorations = TextDecorations.Underline;
                nameBlock.MouseLeave += (_, _) => nameBlock.TextDecorations = null;

                var editIcon = new TextBlock
                {
                    Text = "\u270E",
                    Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0),
                    Cursor = Cursors.IBeam,
                    ToolTip = "Rename project"
                };
                var projEntry = proj;
                void StartRename()
                {
                    var tb = nameBlock;
                    var parent = tb.Parent as StackPanel;
                    if (parent == null) return;
                    var idx = parent.Children.IndexOf(tb);
                    var editBox = new TextBox
                    {
                        Text = projEntry.Name ?? projEntry.FolderName,
                        Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                        Background = (Brush)Application.Current.FindResource("BgDeep"),
                        BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                        BorderThickness = new Thickness(1),
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(2, 0, 2, 0),
                        MinWidth = 80,
                        CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
                        SelectionBrush = (Brush)Application.Current.FindResource("Accent")
                    };

                    parent.Children.RemoveAt(idx);
                    parent.Children.Insert(idx, editBox);

                    var committed = false;
                    void CommitRename()
                    {
                        if (committed) return;
                        committed = true;
                        var newName = editBox.Text?.Trim();
                        if (!string.IsNullOrEmpty(newName))
                        {
                            projEntry.Name = newName;
                            SaveProjects();
                            RefreshProjectCombo();
                            ProjectRenamed?.Invoke(projEntry.Path, newName);
                        }
                        RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    }

                    editBox.KeyDown += (_, ke) =>
                    {
                        if (ke.Key == Key.Enter) CommitRename();
                        else if (ke.Key == Key.Escape) RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    };

                    _view.ViewDispatcher.BeginInvoke(new Action(() =>
                    {
                        editBox.LostFocus += (_, _) => CommitRename();
                    }), DispatcherPriority.Input);

                    editBox.SelectAll();
                    editBox.Focus();
                }
                nameBlock.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
                editIcon.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
                nameRow.Children.Add(nameBlock);
                nameRow.Children.Add(editIcon);
                infoPanel.Children.Add(nameRow);

                infoPanel.Children.Add(new TextBlock
                {
                    Text = proj.Path,
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0),
                    ToolTip = proj.Path
                });

                if (proj.IsInitializing)
                {
                    var initRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                    initRow.Children.Add(new TextBlock
                    {
                        Text = "Initializing descriptions...",
                        Foreground = (Brush)Application.Current.FindResource("WarningAmber"),
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        FontFamily = new FontFamily("Segoe UI")
                    });
                    infoPanel.Children.Add(initRow);
                }
                else if (!string.IsNullOrWhiteSpace(proj.ShortDescription))
                {
                    infoPanel.Children.Add(new TextBlock
                    {
                        Text = proj.ShortDescription,
                        Foreground = (Brush)Application.Current.FindResource("TextTabHeader"),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        Margin = new Thickness(0, 4, 0, 0),
                        ToolTip = proj.ShortDescription
                    });
                }

                var initBrush = proj.IsInitialized
                    ? BrushCache.Theme("SuccessGreen")
                    : BrushCache.Theme("WarningAmber");
                var initIndicator = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = proj.IsInitialized ? "Feature registry initialized" : "Feature registry not initialized"
                };
                initIndicator.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = initBrush,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                initIndicator.Children.Add(new TextBlock
                {
                    Text = proj.IsInitialized ? "Initialized" : "Not Initialized",
                    Foreground = initBrush,
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = proj.IsInitialized ? new Thickness(0) : new Thickness(0, 0, 6, 0)
                });
                if (!proj.IsInitialized)
                {
                    var inlineInitBtn = new Button
                    {
                        Content = "Init",
                        Style = (Style)Application.Current.FindResource("SmallBtn"),
                        Background = (Brush)Application.Current.FindResource("Accent"),
                        FontSize = 10,
                        Padding = new Thickness(8, 1, 8, 1),
                        Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Initialize Feature Registry"
                    };
                    var inlineInitEntry = proj;
                    inlineInitBtn.Click += async (s, ev) =>
                    {
                        ev.Handled = true;
                        if (s is not Button btn) return;
                        btn.IsEnabled = false;
                        btn.Content = "Analyzing...";

                        var pulseAnim = new DoubleAnimation(1.0, 0.4, TimeSpan.FromSeconds(0.8))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase()
                        };
                        btn.BeginAnimation(UIElement.OpacityProperty, pulseAnim);

                        try
                        {
                            var registryManager = new FeatureRegistryManager();
                            var initializer = new FeatureInitializer(registryManager);
                            initializer.ProgressChanged += msg =>
                            {
                                Application.Current?.Dispatcher?.BeginInvoke(() =>
                                    btn.Content = msg.Length > 30 ? msg[..30] + "…" : msg);
                                AppLogger.Info("FeatureInit", $"[{inlineInitEntry.DisplayName}] {msg}");
                            };
                            var result = await initializer.InitializeAsync(inlineInitEntry.Path);
                            btn.BeginAnimation(UIElement.OpacityProperty, null);
                            btn.Opacity = 1.0;
                            if (result != null)
                            {
                                AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {inlineInitEntry.DisplayName}");
                                RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                            }
                            else
                            {
                                btn.Content = "Failed";
                                btn.IsEnabled = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            btn.BeginAnimation(UIElement.OpacityProperty, null);
                            btn.Opacity = 1.0;
                            AppLogger.Error("FeatureInit", $"Feature initialization failed for {inlineInitEntry.DisplayName}", ex);
                            btn.Content = "Failed – Retry";
                            btn.IsEnabled = true;
                        }
                    };
                    initIndicator.Children.Add(inlineInitBtn);
                }
                infoPanel.Children.Add(initIndicator);

                // ── Inline project stats panel ──
                if (_activeTasks != null && _historyTasks != null)
                {
                    var allTasks = _activeTasks
                        .Concat(_historyTasks)
                        .Where(t => t.IsFinished);
                    var stats = GetProjectStats(proj.Path, allTasks);
                    if (stats.TotalTasks > 0)
                    {
                        var statsPanel = new StackPanel
                        {
                            Margin = new Thickness(0, 4, 0, 0)
                        };

                        var line1 = new TextBlock
                        {
                            FontSize = 10,
                            FontFamily = new FontFamily("Segoe UI"),
                            Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                            TextWrapping = TextWrapping.NoWrap,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        line1.Inlines.Add(new System.Windows.Documents.Run($"{stats.TotalTasks} tasks"));
                        line1.Inlines.Add(new System.Windows.Documents.Run(" \u00b7 ") { Foreground = (Brush)Application.Current.FindResource("TextSubdued") });
                        line1.Inlines.Add(new System.Windows.Documents.Run($"{stats.SuccessRate:P0} success")
                        {
                            Foreground = stats.SuccessRate >= 0.7
                                ? BrushCache.Theme("SuccessGreen")
                                : stats.SuccessRate >= 0.4
                                    ? BrushCache.Theme("WarningAmber")
                                    : BrushCache.Theme("DangerBright")
                        });
                        if (stats.AverageDuration > TimeSpan.Zero)
                        {
                            line1.Inlines.Add(new System.Windows.Documents.Run(" \u00b7 ") { Foreground = (Brush)Application.Current.FindResource("TextSubdued") });
                            line1.Inlines.Add(new System.Windows.Documents.Run($"avg {ActivityDashboardManager.FormatDuration(stats.AverageDuration)}"));
                        }
                        statsPanel.Children.Add(line1);

                        var line2Parts = new List<string>();
                        if (stats.TotalTokens > 0)
                            line2Parts.Add($"{FormatTokenCount(stats.TotalTokens)} tokens");
                        if (stats.MostRecentTaskTime.HasValue)
                            line2Parts.Add($"last: {FormatRelativeTime(stats.MostRecentTaskTime.Value)}");
                        if (line2Parts.Count > 0)
                        {
                            statsPanel.Children.Add(new TextBlock
                            {
                                Text = string.Join(" \u00b7 ", line2Parts),
                                FontSize = 10,
                                FontFamily = new FontFamily("Segoe UI"),
                                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                                TextWrapping = TextWrapping.NoWrap,
                                TextTrimming = TextTrimming.CharacterEllipsis
                            });
                        }

                        infoPanel.Children.Add(statsPanel);
                    }
                }

                if (proj.IsGame)
                {
                    var statusColor = proj.McpStatus switch
                    {
                        McpStatus.Connected => BrushCache.Theme("SuccessGreen"),
                        McpStatus.Connecting => BrushCache.Theme("WarningAmber"),
                        McpStatus.Failed => BrushCache.Theme("DangerBright"),
                        _ => BrushCache.Theme("TextMuted")
                    };

                    var statusText = proj.McpStatus switch
                    {
                        McpStatus.Connected => "MCP Connected",
                        McpStatus.Connecting => "MCP Connecting...",
                        McpStatus.Failed => "MCP Failed",
                        _ => "MCP Not Connected"
                    };

                    var mcpStatusPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 4, 0, 0)
                    };

                    var statusIndicator = new System.Windows.Shapes.Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = statusColor,
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = statusText
                    };

                    if (proj.McpStatus == McpStatus.Connecting || proj.McpStatus == McpStatus.Connected)
                    {
                        var animation = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 0.3,
                            To = 1.0,
                            Duration = new Duration(TimeSpan.FromSeconds(1)),
                            AutoReverse = true,
                            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                        };
                        statusIndicator.BeginAnimation(UIElement.OpacityProperty, animation);
                    }

                    mcpStatusPanel.Children.Add(statusIndicator);
                    mcpStatusPanel.Children.Add(new TextBlock
                    {
                        Text = statusText,
                        Foreground = statusColor,
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    infoPanel.Children.Add(mcpStatusPanel);
                }

                Grid.SetRow(infoPanel, 0);
                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

                if (proj.IsGame)
                {
                    var mcpButton = new Button
                    {
                        Content = proj.McpStatus switch
                        {
                            McpStatus.Connected => "Disconnect",
                            McpStatus.Connecting => "Connecting...",
                            _ => "Connect to MCP"
                        },
                        Style = (Style)Application.Current.FindResource("SmallBtn"),
                        Background = proj.McpStatus switch
                        {
                            McpStatus.Connected => (Brush)Application.Current.FindResource("BgHover"),
                            McpStatus.Connecting => (Brush)Application.Current.FindResource("BgCard"),
                            _ => (Brush)Application.Current.FindResource("Accent")
                        },
                        Foreground = proj.McpStatus == McpStatus.Connecting
                            ? (Brush)Application.Current.FindResource("TextMuted")
                            : (Brush)Application.Current.FindResource("TextPrimary"),
                        Tag = proj.Path,
                        IsEnabled = proj.McpStatus != McpStatus.Connecting
                    };

                    mcpButton.Click += async (s, ev) =>
                    {
                        ev.Handled = true;
                        if (s is Button b && b.Tag is string path)
                        {
                            var projEntry2 = _savedProjects.FirstOrDefault(p => p.Path == path);
                            if (projEntry2 != null)
                            {
                                if (projEntry2.McpStatus == McpStatus.Connected)
                                    Mcp.DisconnectMcp(path);
                                else
                                    await Mcp.ConnectMcpAsync(path);
                            }
                        }
                    };

                    var mcpButtonWrapper = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    mcpButtonWrapper.Children.Add(mcpButton);

                    Grid.SetRow(mcpButtonWrapper, 1);
                    Grid.SetColumn(mcpButtonWrapper, 0);
                    Grid.SetColumnSpan(mcpButtonWrapper, 2);
                    grid.Children.Add(mcpButtonWrapper);
                }

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                var closeBtn = new Button
                {
                    Content = "\u2715",
                    Background = Brushes.Transparent,
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 14,
                    Padding = new Thickness(4, 0, 4, 0),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Tag = proj.Path,
                    ToolTip = "Remove project"
                };
                closeBtn.Click += (s, ev) =>
                {
                    ev.Handled = true;
                    var termCb = updateTerminalWorkingDirectory ?? _storedUpdateTerminal;
                    var saveCb = saveSettings ?? _storedSaveSettings;
                    var syncCb = syncSettings ?? _storedSyncSettings;
                    if (s is Button b && b.Tag is string path && termCb != null && saveCb != null && syncCb != null)
                        RemoveProject(path, termCb, saveCb, syncCb);
                };
                btnPanel.Children.Add(closeBtn);

                var gearBtn = new Button
                {
                    Content = "\uE713",
                    Background = Brushes.Transparent,
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Padding = new Thickness(4, 2, 4, 2),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Project settings",
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var gearEntry = proj;
                gearBtn.Click += (s, ev) =>
                {
                    ev.Handled = true;
                    var syncCb = syncSettings ?? _storedSyncSettings;
                    ProjectSettingsDialog.Show(gearEntry, SaveProjects, () =>
                    {
                        RefreshProjectList(null, null, null);
                        syncCb?.Invoke();
                    }, this);
                    RefreshProjectList(null, null, null);
                    syncCb?.Invoke();
                };
                btnPanel.Children.Add(gearBtn);

                if (!proj.IsFeatureRegistryInitialized)
                {
                    var initFeaturesBtn = new Button
                    {
                        Content = "\uE946",
                        Background = Brushes.Transparent,
                        Foreground = (Brush)Application.Current.FindResource("Accent"),
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 12,
                        Padding = new Thickness(4, 2, 4, 2),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        ToolTip = "Initialize Feature Registry",
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    var initEntry = proj;
                    initFeaturesBtn.Click += async (s, ev) =>
                    {
                        ev.Handled = true;
                        if (s is not Button btn) return;
                        btn.IsEnabled = false;
                        btn.Content = "\u23F3";
                        btn.ToolTip = "Analyzing project structure...";

                        var pulseAnim = new DoubleAnimation(1.0, 0.4, TimeSpan.FromSeconds(0.8))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase()
                        };
                        btn.BeginAnimation(UIElement.OpacityProperty, pulseAnim);

                        try
                        {
                            var registryManager = new FeatureRegistryManager();
                            var initializer = new FeatureInitializer(registryManager);
                            initializer.ProgressChanged += msg =>
                            {
                                Application.Current?.Dispatcher?.BeginInvoke(() => btn.ToolTip = msg);
                                AppLogger.Info("FeatureInit", $"[{initEntry.DisplayName}] {msg}");
                            };
                            var result = await initializer.InitializeAsync(initEntry.Path);
                            btn.BeginAnimation(UIElement.OpacityProperty, null);
                            btn.Opacity = 1.0;
                            if (result != null)
                            {
                                AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {initEntry.DisplayName}");
                                RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                            }
                            else
                            {
                                btn.Content = "\u274C";
                                btn.ToolTip = "Initialization returned no results. Click to retry.";
                                btn.IsEnabled = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            btn.BeginAnimation(UIElement.OpacityProperty, null);
                            btn.Opacity = 1.0;
                            AppLogger.Error("FeatureInit", $"Feature initialization failed for {initEntry.DisplayName}", ex);
                            btn.Content = "\u274C";
                            btn.ToolTip = $"Failed: {ex.Message}. Click to retry.";
                            btn.IsEnabled = true;
                        }
                    };
                    btnPanel.Children.Add(initFeaturesBtn);
                }

                Grid.SetRow(btnPanel, 0);
                Grid.SetColumn(btnPanel, 1);
                grid.Children.Add(btnPanel);

                card.Child = grid;

                var projPath = proj.Path;
                card.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.OriginalSource is TextBox) return;
                    if (projPath == _projectPath) return;
                    if (_isSwapping) return;
                    if (!DarkDialog.ShowConfirm("Are you sure you want to change project?", "Change Project"))
                        return;
                    _isSwapping = true;
                    _projectPath = projPath;
                    ProjectSwapStarted?.Invoke();

                    // Use stored callbacks — the local captures may be null if a background
                    // refresh (feature-init, MCP config, etc.) rebuilt the card list.
                    var termCb = _storedUpdateTerminal;
                    var saveCb = _storedSaveSettings;
                    var syncCb = _storedSyncSettings;

                    _view.ViewDispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            try { termCb?.Invoke(projPath); }
                            catch (Exception ex) { AppLogger.Warn("ProjectManager", "Terminal update failed during project swap", ex); }

                            await Task.Yield();
                            saveCb?.Invoke();
                            syncCb?.Invoke();
                        }
                        catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed during project swap", ex); }
                        finally
                        {
                            _isSwapping = false;
                            ProjectSwapCompleted?.Invoke();
                        }
                    }));
                };

                _view.ProjectListPanel.Children.Add(card);
            }
        }
    }
}
