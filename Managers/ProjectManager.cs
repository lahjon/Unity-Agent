using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Spritely.Helpers;
using Spritely.Dialogs;
using Spritely.Models;

namespace Spritely.Managers
{
    public class ProjectManager
    {
        private List<ProjectEntry> _savedProjects = new();
        private string _projectPath;
        private readonly string _projectsFile;
        private string _addProjectSelectedPath = "";
        private bool _isSwapping;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        private static readonly Random _rng = new();
        private ObservableCollection<AgentTask>? _activeTasks;
        private ObservableCollection<AgentTask>? _historyTasks;
        private readonly RulesManager _rulesManager = new();

        private static readonly string[] ProjectColorPalette =
        {
            "#D4806B", // soft coral
            "#6BA3A0", // soft teal
            "#9B8EC4", // soft lavender
            "#C4A94D", // soft gold
            "#7BAF7B", // soft sage
            "#6B8FD4", // soft blue
            "#C47B8E", // soft rose
            "#D4A06B", // soft amber
            "#6BC4A0", // soft mint
            "#B08EB0", // soft mauve
            "#8BAFC4", // soft steel
            "#C49B6B", // soft tan
        };

        private static readonly JsonSerializerOptions _projectJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IProjectPanelView _view;
        private ITaskFactory? _taskFactory;
        private SettingsManager? _settingsManager;

        public event Action<AgentTask>? McpInvestigationRequested;
        public event Action<string>? McpOutputChanged; // (projectPath)
        public event Action? ProjectSwapStarted;
        public event Action? ProjectSwapCompleted;
        public event Action<string, string>? ProjectRenamed; // (projectPath, newName)

        public string ProjectPath
        {
            get => _projectPath;
            set => _projectPath = value;
        }

        public List<ProjectEntry> SavedProjects => _savedProjects;

        /// <summary>Whether any projects have been added.</summary>
        public bool HasProjects => _savedProjects.Count > 0;

        public ProjectManager(
            string appDataDir,
            string initialProjectPath,
            IProjectPanelView view)
        {
            _projectsFile = Path.Combine(appDataDir, "projects.json");
            _projectPath = initialProjectPath;
            _view = view;
        }

        public void SetTaskFactory(ITaskFactory taskFactory)
        {
            _taskFactory = taskFactory;
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

        private string PickProjectColor()
        {
            // Pick a color not already used; generate a random unique one if palette exhausted
            var used = new HashSet<string>(
                _savedProjects
                    .Where(p => !string.IsNullOrEmpty(p.Color))
                    .Select(p => p.Color),
                StringComparer.OrdinalIgnoreCase);
            var available = ProjectColorPalette.Where(c => !used.Contains(c)).ToArray();
            if (available.Length > 0)
                return available[_rng.Next(available.Length)];

            // All palette colors taken – generate a random color that isn't already used
            for (var i = 0; i < 200; i++)
            {
                var candidate = $"#{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}";
                if (!used.Contains(candidate))
                    return candidate;
            }
            // Extremely unlikely fallback
            return $"#{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}{_rng.Next(0x40, 0xD0):X2}";
        }

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

            // Backfill colors for projects that don't have one yet
            var needsSave = false;
            foreach (var p in _savedProjects.Where(p => string.IsNullOrEmpty(p.Color)))
            {
                p.Color = PickProjectColor();
                needsSave = true;
            }
            if (needsSave) SaveProjects();

            _view.ViewDispatcher.Invoke(() =>
            {
                RefreshProjectCombo();
                UpdateMcpToggleForProject();
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

        public void RefreshProjectCombo()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            _view.PromptProjectLabel.Text = proj?.DisplayName ?? Path.GetFileName(_projectPath);
            _view.PromptProjectLabel.ToolTip = _projectPath;
            RefreshDescriptionBoxes();
        }

        public void RefreshDescriptionBoxes()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            var initializing = entry?.IsInitializing == true;

            if (initializing)
            {
                _view.ShortDescBox.Text = "Initializing...";
                _view.LongDescBox.Text = "Initializing...";
                _view.ShortDescBox.FontStyle = FontStyles.Italic;
                _view.LongDescBox.FontStyle = FontStyles.Italic;
            }
            else
            {
                _view.ShortDescBox.Text = entry?.ShortDescription ?? "";
                _view.LongDescBox.Text = entry?.LongDescription ?? "";
                _view.ShortDescBox.FontStyle = FontStyles.Normal;
                _view.LongDescBox.FontStyle = FontStyles.Normal;
            }

            _view.RuleInstructionBox.Text = entry?.RuleInstruction ?? "";
            _view.ProjectRulesList.ItemsSource = null;
            _view.ProjectRulesList.ItemsSource = entry?.ProjectRules ?? new List<string>();

            _view.CrashLogPathBox.Text = !string.IsNullOrEmpty(entry?.CrashLogPath) ? entry.CrashLogPath : GetDefaultCrashLogPath();
            _view.AppLogPathBox.Text = !string.IsNullOrEmpty(entry?.AppLogPath) ? entry.AppLogPath : GetDefaultAppLogPath();
            _view.HangLogPathBox.Text = !string.IsNullOrEmpty(entry?.HangLogPath) ? entry.HangLogPath : GetDefaultHangLogPath();
            _view.EditCrashLogPathsToggle.IsChecked = false;

            _view.EditShortDescToggle.IsChecked = false;
            _view.EditLongDescToggle.IsChecked = false;
            _view.EditRuleInstructionToggle.IsChecked = false;
            _view.EditShortDescToggle.IsEnabled = !initializing;
            _view.EditLongDescToggle.IsEnabled = !initializing;
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
                Color = PickProjectColor(),
                McpServerName = _settingsManager?.DefaultMcpServerName ?? "mcp-for-unity-server",
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

            _ = GenerateProjectDescriptionInBackground(entry);
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
                Color = PickProjectColor(),
                IsGame = result.IsGame,
                McpServerName = _settingsManager?.DefaultMcpServerName ?? "mcp-for-unity-server",
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

            _ = GenerateProjectDescriptionInBackground(entry);
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

        public void UpdateMcpToggleForProject()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            if (proj != null)
            {
                _view.UseMcpToggle.IsEnabled = true;
                // For game projects with MCP connected, enable by default
                _view.UseMcpToggle.IsChecked = proj.IsGame && proj.McpStatus == McpStatus.Connected;
                _view.UseMcpToggle.Opacity = 1.0;
                _view.UseMcpToggle.ToolTip = proj.IsGame && proj.McpStatus == McpStatus.Connected
                    ? "MCP is connected and will be used for Unity-specific commands"
                    : "Enable to use MCP for this project";
            }
            else
            {
                _view.UseMcpToggle.IsChecked = false;
                _view.UseMcpToggle.IsEnabled = false;
                _view.UseMcpToggle.Opacity = 0.4;
                _view.UseMcpToggle.ToolTip = null;
            }
        }

        public ProjectEntry? GetCurrentProject()
        {
            return _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
        }

        public string GetProjectColor(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.Color ?? "#666666";
        }

        public string GetProjectDisplayName(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.DisplayName ?? Path.GetFileName(projectPath);
        }

        public bool IsGameProject(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.IsGame == true;
        }

        public string GetProjectDescription(AgentTask task)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == task.ProjectPath);
            if (entry == null) return "";
            return task.ExtendedPlanning
                ? (string.IsNullOrWhiteSpace(entry.LongDescription) ? entry.ShortDescription : entry.LongDescription)
                : entry.ShortDescription;
        }

        public async System.Threading.Tasks.Task GenerateProjectDescriptionInBackground(ProjectEntry entry)
        {
            try
            {
                var (shortDesc, longDesc) = await _taskFactory!.GenerateProjectDescriptionAsync(entry.Path, default, entry.IsGame);
                _view.ViewDispatcher.Invoke(() =>
                {
                    entry.ShortDescription = shortDesc;
                    entry.LongDescription = longDesc;
                    entry.IsInitializing = false;
                    SaveProjects();
                    RefreshProjectCombo();
                    RefreshProjectList(null, null, null);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", $"Failed to generate description for {entry.Path}", ex);
                _view.ViewDispatcher.Invoke(() =>
                {
                    entry.IsInitializing = false;
                    RefreshProjectList(null, null, null);
                });
            }
        }

        public async void RegenerateDescriptions()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;

            entry.IsInitializing = true;
            entry.ShortDescription = "";
            entry.LongDescription = "";
            SaveProjects();
            RefreshProjectCombo();
            RefreshProjectList(null, null, null);
            RefreshDescriptionBoxes();

            _view.RegenerateDescBtn.IsEnabled = false;
            _view.RegenerateDescBtn.Content = "Regenerating...";

            await GenerateProjectDescriptionInBackground(entry);

            _view.RegenerateDescBtn.Content = "Regenerate Descriptions";
            _view.RegenerateDescBtn.IsEnabled = true;
            RefreshDescriptionBoxes();
        }

        public void SaveShortDesc()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.ShortDescription = _view.ShortDescBox.Text;
                SaveProjects();
            }
            _view.EditShortDescToggle.IsChecked = false;
        }

        public void SaveLongDesc()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.LongDescription = _view.LongDescBox.Text;
                SaveProjects();
            }
            _view.EditLongDescToggle.IsChecked = false;
        }

        public void SaveRuleInstruction()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.RuleInstruction = _view.RuleInstructionBox.Text;
                SaveProjects();
            }
            _view.EditRuleInstructionToggle.IsChecked = false;
        }

        public void AddProjectRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return;
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;
            entry.ProjectRules.Add(rule.Trim());
            SaveProjects();
            RefreshDescriptionBoxes();
        }

        public void RemoveProjectRule(string rule)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;
            entry.ProjectRules.Remove(rule);
            SaveProjects();
            RefreshDescriptionBoxes();
        }

        public static string GetDefaultCrashLogPath() => Path.Combine(AppLogger.GetLogDir(), "crash.log");
        public static string GetDefaultAppLogPath() => Path.Combine(AppLogger.GetLogDir(), "app.log");
        public static string GetDefaultHangLogPath() => Path.Combine(AppLogger.GetLogDir(), "hang.log");

        public List<string> GetCrashLogPaths(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            return new List<string>
            {
                !string.IsNullOrEmpty(entry?.CrashLogPath) ? entry.CrashLogPath : GetDefaultCrashLogPath(),
                !string.IsNullOrEmpty(entry?.AppLogPath) ? entry.AppLogPath : GetDefaultAppLogPath(),
                !string.IsNullOrEmpty(entry?.HangLogPath) ? entry.HangLogPath : GetDefaultHangLogPath()
            };
        }

        public void SaveCrashLogPaths(string crashLogPath, string appLogPath, string hangLogPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.CrashLogPath = crashLogPath;
                entry.AppLogPath = appLogPath;
                entry.HangLogPath = hangLogPath;
                SaveProjects();
            }
            _view.EditCrashLogPathsToggle.IsChecked = false;
        }

        public string GetProjectRulesBlock(string projectPath)
        {
            var sb = new System.Text.StringBuilder();

            // 1. CLAUDE.md / .claude/rules/ discovered from the target project
            var claudeRules = _rulesManager.GetRulesBlock(projectPath);
            if (!string.IsNullOrWhiteSpace(claudeRules))
                sb.Append(claudeRules);

            // 2. Spritely-managed per-project rules (UI-configured)
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry != null)
            {
                var hasInstruction = !string.IsNullOrWhiteSpace(entry.RuleInstruction);
                var hasRules = entry.ProjectRules.Count > 0;

                if (hasInstruction || hasRules)
                {
                    sb.Append("# PROJECT RULES\n");
                    if (hasInstruction)
                        sb.Append(entry.RuleInstruction.Trim()).Append("\n");
                    if (hasRules)
                    {
                        if (hasInstruction) sb.Append("\n");
                        foreach (var rule in entry.ProjectRules)
                            sb.Append("- ").Append(rule).Append("\n");
                    }
                    sb.Append("\n");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Invalidates cached CLAUDE.md rules for a project (e.g., after files change).
        /// </summary>
        public void InvalidateRulesCache(string projectPath) => _rulesManager.InvalidateCache(projectPath);

        public void RefreshProjectList(
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            if (_view.ProjectListPanel == null) return;

            // Ensure the projects panel doesn't cause layout issues
            _view.ViewDispatcher.Invoke(() =>
            {
                // Force the layout to update before clearing
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
                    MaxHeight = 200 // Prevent project cards from growing too tall
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
                    : BrushCache.Theme("TextMuted");
                var initIndicator = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = proj.IsInitialized ? "Project is initialized (.claude or CLAUDE.md found)" : "Project is not initialized"
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
                    VerticalAlignment = VerticalAlignment.Center
                });
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

                if (proj.IsGame) // Only show MCP for game projects
                {
                    // Add MCP status to info panel
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

                    // Add pulsing animation for Connecting and Connected states
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

                if (proj.IsGame) // Only show MCP button for game projects
                {
                    // Connect/Disconnect button
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
                            var projEntry = _savedProjects.FirstOrDefault(p => p.Path == path);
                            if (projEntry != null)
                            {
                                if (projEntry.McpStatus == McpStatus.Connected)
                                    DisconnectMcp(path);
                                else
                                    await ConnectMcpAsync(path);
                            }
                        }
                    };

                    // Create a wrapper to position the button at bottom right
                    var mcpButtonWrapper = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    mcpButtonWrapper.Children.Add(mcpButton);

                    // Position MCP button in bottom row, spanning both columns
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
                    if (s is Button b && b.Tag is string path && updateTerminalWorkingDirectory != null && saveSettings != null && syncSettings != null)
                        RemoveProject(path, updateTerminalWorkingDirectory, saveSettings, syncSettings);
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
                    ProjectSettingsDialog.Show(gearEntry, SaveProjects, () =>
                    {
                        // Refresh UI after MCP/game toggle changes
                        RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                        syncSettings?.Invoke();
                    }, this);
                    // Refresh after dialog closes in case settings changed
                    RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    syncSettings?.Invoke();
                };
                btnPanel.Children.Add(gearBtn);

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
                    // Defer heavy work so we're not modifying the visual tree
                    // from inside the click handler of a card that will be destroyed.
                    // Use async to yield between operations and keep the UI responsive.
                    _view.ViewDispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            try { updateTerminalWorkingDirectory?.Invoke(projPath); }
                            catch (Exception ex) { AppLogger.Warn("ProjectManager", "Terminal update failed during project swap", ex); }

                            // Yield to let UI repaint after terminal update
                            await System.Threading.Tasks.Task.Yield();
                            saveSettings?.Invoke();
                            syncSettings?.Invoke();
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

        public async System.Threading.Tasks.Task ConnectMcpAsync(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            // Check if Unity is running first
            if (!IsUnityRunning())
            {
                // Show warning to user
                entry.McpOutput.Clear();
                entry.McpOutput.AppendLine("❌ Unity Editor is not running!");
                entry.McpOutput.AppendLine("");
                entry.McpOutput.AppendLine("MCP connection requires Unity Editor to be running.");
                entry.McpOutput.AppendLine("Please start Unity Editor and try again.");

                RefreshProjectList(null, null, null);

                // Also show a message dialog
                System.Windows.MessageBox.Show(
                    "Unity Editor must be running before connecting to MCP.\n\nPlease start Unity Editor and try again.",
                    "Unity Not Running",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);

                return;
            }

            // Start the MCP server
            var success = await StartMcpServerAsync(entry);

            if (!success)
            {
                // If failed after 3 retries, try to diagnose
                var investigateTask = new AgentTask
                {
                    Description = $"Failed to start MCP server after 3 attempts (waited 30 seconds each). Please check:\n" +
                        "1. Unity Editor is running with the MCP plugin installed\n" +
                        "2. The MCP start command is correct: {entry.McpStartCommand}\n" +
                        "3. The MCP address is accessible: {entry.McpAddress}\n" +
                        "4. No firewall or antivirus is blocking the connection\n" +
                        "5. Port is not already in use by another process\n\n" +
                        "Check the logs for more detailed error information.",
                    SkipPermissions = true,
                    ProjectPath = projectPath,
                    ProjectColor = GetProjectColor(projectPath),
                    ProjectDisplayName = GetProjectDisplayName(projectPath)
                };
                McpInvestigationRequested?.Invoke(investigateTask);
            }

            UpdateMcpToggleForProject();
        }

        public void DisconnectMcp(string projectPath)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            StopMcpServer(entry);
            UpdateMcpToggleForProject();
        }

        private bool IsUnityRunning()
        {
            try
            {
                // Check for Unity.exe process
                var unityProcesses = System.Diagnostics.Process.GetProcessesByName("Unity");

                if (unityProcesses.Length > 0)
                {
                    AppLogger.Info("ProjectManager", $"Found {unityProcesses.Length} Unity process(es) running");
                    // Clean up process handles
                    foreach (var process in unityProcesses)
                    {
                        process.Dispose();
                    }
                    return true;
                }

                // Also check for Unity Hub Unity.exe (sometimes named differently)
                var unityHubProcesses = System.Diagnostics.Process.GetProcessesByName("Unity Hub");
                if (unityHubProcesses.Length > 0)
                {
                    AppLogger.Debug("ProjectManager", "Unity Hub is running, but Unity Editor itself is not");
                    foreach (var process in unityHubProcesses)
                    {
                        process.Dispose();
                    }
                }

                AppLogger.Info("ProjectManager", "No Unity processes found");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("ProjectManager", "Error checking if Unity is running", ex);
                // If we can't check, assume Unity might be running to avoid blocking
                return true;
            }
        }

        private async System.Threading.Tasks.Task<bool> CheckMcpHealth(string url)
        {
            try
            {
                // MCP servers expect JSON-RPC requests, not plain GET requests
                var jsonRequest = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                            experimental = new { }
                        },
                        clientInfo = new
                        {
                            name = "Spritely",
                            version = "1.0.0"
                        }
                    },
                    id = 1
                };

                var json = System.Text.Json.JsonSerializer.Serialize(jsonRequest);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                // Use POST request with proper headers for JSON-RPC
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = content;
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);

                // For MCP health check, we just need to know if the server responds
                // Even error responses indicate the server is running
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable &&
                       response.StatusCode != System.Net.HttpStatusCode.NotFound;
            }
            catch (HttpRequestException)
            {
                // Connection refused or timeout - server not running
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProjectManager", $"MCP health check failed for {url}", ex);
                return false;
            }
        }

        private void KillProcessOnPort(int port, ProjectEntry entry)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netstat -ano | findstr \"LISTENING\" | findstr \":{port} \"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        try
                        {
                            var existing = System.Diagnostics.Process.GetProcessById(pid);
                            if (!existing.HasExited)
                            {
                                AppLogger.Info("ProjectManager", $"Killing stale process {pid} ({existing.ProcessName}) on port {port}");
                                entry.McpOutput.AppendLine($"Killing stale process on port {port} (PID {pid})...");
                                existing.Kill();
                                existing.WaitForExit(5000);
                            }
                        }
                        catch { /* Process may have already exited */ }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProjectManager", $"Error checking port {port}", ex);
            }
        }

        private async Task<bool> StartMcpServerAsync(ProjectEntry entry)
        {
            const int MAX_RETRIES = 3;
            const int SERVER_START_TIMEOUT_SECONDS = 30;

            try
            {
                entry.McpStatus = McpStatus.Connecting;
                entry.McpOutput.Clear(); // Clear any previous output
                entry.McpOutput.AppendLine($"Connecting to server...");
                SaveProjects();
                RefreshProjectList(null, null, null);

                // First check if server is already running
                AppLogger.Info("ProjectManager", $"Checking if MCP server is already running at {entry.McpAddress}");
                entry.McpOutput.AppendLine($"Checking if MCP server is already running at {entry.McpAddress}");

                if (await CheckMcpHealth(entry.McpAddress))
                {
                    AppLogger.Info("ProjectManager", "MCP server is already running, connecting...");
                    entry.McpOutput.AppendLine($"Server already running, verifying connection...");

                    // Register with Claude Code
                    await RegisterMcpWithClaudeAsync(entry.McpServerName);

                    entry.McpStatus = McpStatus.Connected;
                    entry.McpOutput.AppendLine($"✓ Connection verified - MCP server is ready!");
                    entry.McpOutput.AppendLine($"Unity operations available: create scene items, make prefabs, take screenshots");
                    SaveProjects();
                    RefreshProjectList(null, null, null);
                    return true;
                }

                // Kill any stale process holding the port before starting
                if (Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri))
                {
                    KillProcessOnPort(uri.Port, entry);
                    await Task.Delay(500); // Brief pause for port release
                }

                // Try to start the server with retries
                for (int retry = 1; retry <= MAX_RETRIES; retry++)
                {
                    AppLogger.Info("ProjectManager", $"Attempting to start MCP server (attempt {retry}/{MAX_RETRIES})");
                    entry.McpOutput.AppendLine($"Starting MCP server (attempt {retry}/{MAX_RETRIES})...");

                    // Start the server process asynchronously
                    var processInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {entry.McpStartCommand}",
                        WorkingDirectory = entry.Path,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    // Start the process asynchronously without waiting
                    var startTask = Task.Run(() =>
                    {
                        try
                        {
                            var process = System.Diagnostics.Process.Start(processInfo);
                            if (process != null)
                            {
                                entry.McpProcessId = process.Id;
                                entry.McpProcess = process;

                                // Capture output asynchronously in real-time
                                process.OutputDataReceived += (sender, args) =>
                                {
                                    if (!string.IsNullOrEmpty(args.Data))
                                    {
                                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        {
                                            entry.McpOutput.AppendLine(args.Data);
                                            // Notify any UI listeners that output has changed
                                            McpOutputChanged?.Invoke(entry.Path);
                                        });
                                        AppLogger.Debug("ProjectManager", $"MCP server output: {args.Data}");
                                    }
                                };

                                process.ErrorDataReceived += (sender, args) =>
                                {
                                    if (!string.IsNullOrEmpty(args.Data))
                                    {
                                        Application.Current?.Dispatcher?.InvokeAsync(() =>
                                        {
                                            entry.McpOutput.AppendLine(args.Data);
                                            McpOutputChanged?.Invoke(entry.Path);
                                        });
                                        AppLogger.Warn("ProjectManager", $"MCP server error: {args.Data}");
                                    }
                                };

                                process.BeginOutputReadLine();
                                process.BeginErrorReadLine();

                                return true;
                            }
                            return false;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("ProjectManager", $"Failed to start process on attempt {retry}", ex);
                            entry.McpOutput.AppendLine($"Failed to start process: {ex.Message}");
                            return false;
                        }
                    });

                    var processStarted = await startTask;
                    if (!processStarted)
                    {
                        AppLogger.Warn("ProjectManager", $"Failed to start MCP server process on attempt {retry}");
                        if (retry < MAX_RETRIES)
                        {
                            await Task.Delay(2000); // Wait 2 seconds before retry
                            continue;
                        }
                        else
                        {
                            break; // All retries failed
                        }
                    }

                    // Wait for server to start (up to 30 seconds)
                    AppLogger.Info("ProjectManager", $"Waiting for MCP server to start (up to {SERVER_START_TIMEOUT_SECONDS} seconds)...");
                    entry.McpOutput.AppendLine($"Waiting for server to respond...");

                    var startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < SERVER_START_TIMEOUT_SECONDS)
                    {
                        if (await CheckMcpHealth(entry.McpAddress))
                        {
                            AppLogger.Info("ProjectManager", $"MCP server started successfully after {(DateTime.Now - startTime).TotalSeconds:F1} seconds");
                            entry.McpOutput.AppendLine($"Server started successfully!");
                            entry.McpOutput.AppendLine($"Verifying MCP connection...");

                            // Register with Claude Code
                            await RegisterMcpWithClaudeAsync(entry.McpServerName);

                            entry.McpStatus = McpStatus.Connected;
                            entry.McpOutput.AppendLine($"✓ Connection verified - MCP server is ready!");
                            entry.McpOutput.AppendLine($"Unity operations available: create scene items, make prefabs, take screenshots");
                            SaveProjects();
                            RefreshProjectList(null, null, null);
                            return true;
                        }

                        // Check less frequently at the beginning, more frequently as time goes on
                        var elapsed = (DateTime.Now - startTime).TotalSeconds;
                        var delay = elapsed < 5 ? 1000 : elapsed < 15 ? 500 : 250;
                        await Task.Delay(delay);
                    }

                    AppLogger.Warn("ProjectManager", $"MCP server failed to respond within {SERVER_START_TIMEOUT_SECONDS} seconds on attempt {retry}");

                    // Kill the process if it's still running and we're going to retry
                    if (retry < MAX_RETRIES && entry.McpProcessId > 0)
                    {
                        try
                        {
                            var process = System.Diagnostics.Process.GetProcessById(entry.McpProcessId);
                            if (!process.HasExited)
                            {
                                process.Kill();
                                await Task.Delay(1000); // Give it time to clean up
                            }
                        }
                        catch { /* Process may have already exited */ }
                        entry.McpProcessId = 0;
                    }
                }

                // All retries failed
                AppLogger.Error("ProjectManager", $"Failed to start MCP server after {MAX_RETRIES} attempts");
                entry.McpStatus = McpStatus.Failed;
                SaveProjects();
                RefreshProjectList(null, null, null);
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("ProjectManager", "Unexpected error starting MCP server", ex);
                entry.McpStatus = McpStatus.Failed;
                SaveProjects();
                RefreshProjectList(null, null, null);
                return false;
            }
        }

        public void StopMcpServer(ProjectEntry entry)
        {
            try
            {
                if (entry.McpProcessId > 0 || entry.McpProcess != null)
                {
                    try
                    {
                        var process = entry.McpProcess ?? (entry.McpProcessId > 0 ? System.Diagnostics.Process.GetProcessById(entry.McpProcessId) : null);
                        if (process != null && !process.HasExited)
                        {
                            // Stop reading output
                            try
                            {
                                process.CancelOutputRead();
                                process.CancelErrorRead();
                            }
                            catch { }

                            process.Kill();
                            process.WaitForExit(5000);
                            process.Dispose();
                        }
                    }
                    catch { /* Process may have already exited */ }

                    entry.McpProcessId = 0;
                    entry.McpProcess = null;
                }

                // Also kill any stale process holding the port
                if (Uri.TryCreate(entry.McpAddress, UriKind.Absolute, out var uri))
                {
                    KillProcessOnPort(uri.Port, entry);
                }

                entry.McpStatus = McpStatus.NotConnected;
                entry.McpOutput.AppendLine($"Server disconnected.");
                SaveProjects();
                RefreshProjectList(null, null, null);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", "Error stopping MCP server", ex);
            }
        }

        public void StopAllMcpServers()
        {
            foreach (var entry in _savedProjects.Where(p => p.McpStatus is McpStatus.Connected or McpStatus.Connecting))
            {
                StopMcpServer(entry);
            }
        }

        public void NotifyMcpOutputChanged(string projectPath)
        {
            McpOutputChanged?.Invoke(projectPath);
        }

        private async Task RegisterMcpWithClaudeAsync(string serverName)
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"mcp add --scope local --transport http {serverName} http://127.0.0.1:8080/mcp",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = await Task.Run(() => System.Diagnostics.Process.Start(processInfo));
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectManager", "Failed to register MCP server with Claude", ex);
            }
        }
    }
}
