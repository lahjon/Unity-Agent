using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnityAgent.Dialogs;
using UnityAgent.Models;

namespace UnityAgent.Managers
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

        private readonly TextBlock _promptProjectLabel;
        private readonly TextBlock _addProjectPath;
        private readonly StackPanel _projectListPanel;
        private readonly ToggleButton _useMcpToggle;
        private readonly TextBox _shortDescBox;
        private readonly TextBox _longDescBox;
        private readonly ToggleButton _editShortDescToggle;
        private readonly ToggleButton _editLongDescToggle;
        private readonly StackPanel _shortDescEditButtons;
        private readonly StackPanel _longDescEditButtons;
        private readonly Button _regenerateDescBtn;
        private readonly Dispatcher _dispatcher;

        public event Action<AgentTask>? McpInvestigationRequested;

        public string ProjectPath
        {
            get => _projectPath;
            set => _projectPath = value;
        }

        public List<ProjectEntry> SavedProjects => _savedProjects;

        public ProjectManager(
            string appDataDir,
            string initialProjectPath,
            TextBlock promptProjectLabel,
            TextBlock addProjectPath,
            StackPanel projectListPanel,
            ToggleButton useMcpToggle,
            TextBox shortDescBox,
            TextBox longDescBox,
            ToggleButton editShortDescToggle,
            ToggleButton editLongDescToggle,
            StackPanel shortDescEditButtons,
            StackPanel longDescEditButtons,
            Button regenerateDescBtn,
            Dispatcher dispatcher)
        {
            _projectsFile = Path.Combine(appDataDir, "projects.json");
            _projectPath = initialProjectPath;
            _promptProjectLabel = promptProjectLabel;
            _addProjectPath = addProjectPath;
            _projectListPanel = projectListPanel;
            _useMcpToggle = useMcpToggle;
            _shortDescBox = shortDescBox;
            _longDescBox = longDescBox;
            _editShortDescToggle = editShortDescToggle;
            _editLongDescToggle = editLongDescToggle;
            _shortDescEditButtons = shortDescEditButtons;
            _longDescEditButtons = longDescEditButtons;
            _regenerateDescBtn = regenerateDescBtn;
            _dispatcher = dispatcher;
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

        public void LoadProjects()
        {
            try
            {
                if (File.Exists(_projectsFile))
                {
                    var json = File.ReadAllText(_projectsFile);
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

            RefreshProjectCombo();
            UpdateMcpToggleForProject();
        }

        public void SaveProjects()
        {
            try
            {
                File.WriteAllText(_projectsFile,
                    JsonSerializer.Serialize(_savedProjects, _projectJsonOptions));
            }
            catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed to save projects", ex); }
        }

        public void RefreshProjectCombo()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            _promptProjectLabel.Text = proj?.DisplayName ?? Path.GetFileName(_projectPath);
            _promptProjectLabel.ToolTip = _projectPath;
            RefreshDescriptionBoxes();
        }

        public void RefreshDescriptionBoxes()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            var initializing = entry?.IsInitializing == true;

            if (initializing)
            {
                _shortDescBox.Text = "Initializing...";
                _longDescBox.Text = "Initializing...";
                _shortDescBox.FontStyle = FontStyles.Italic;
                _longDescBox.FontStyle = FontStyles.Italic;
            }
            else
            {
                _shortDescBox.Text = entry?.ShortDescription ?? "";
                _longDescBox.Text = entry?.LongDescription ?? "";
                _shortDescBox.FontStyle = FontStyles.Normal;
                _longDescBox.FontStyle = FontStyles.Normal;
            }

            _editShortDescToggle.IsChecked = false;
            _editLongDescToggle.IsChecked = false;
            _editShortDescToggle.IsEnabled = !initializing;
            _editLongDescToggle.IsEnabled = !initializing;
        }

        public void HandleAddProjectPathClick()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a project folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _addProjectSelectedPath = dialog.SelectedPath;
                _addProjectPath.Text = dialog.SelectedPath;
                _addProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8E8E8"));
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
                _addProjectPath.Text = "Click to select project folder...";
                _addProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));
                return;
            }
            if (_savedProjects.Any(p => p.Path == path))
            {
                DarkDialog.ShowAlert("This project path is already added.", "Duplicate");
                return;
            }

            var name = Path.GetFileName(path);
            var entry = new ProjectEntry { Name = name, Path = path, IsInitializing = true, Color = PickProjectColor() };
            _savedProjects.Add(entry);
            SaveProjects();
            _projectPath = path;
            updateTerminalWorkingDirectory(path);
            saveSettings();
            syncSettings();

            _addProjectSelectedPath = "";
            _addProjectPath.Text = "Click to select project folder...";
            _addProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));

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

            var entry = new ProjectEntry { Name = result.Name, Path = fullPath, IsInitializing = true, Color = PickProjectColor() };
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
            _useMcpToggle.IsChecked = proj?.McpStatus == McpStatus.Enabled;
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
                var (shortDesc, longDesc) = await TaskLauncher.GenerateProjectDescriptionAsync(entry.Path);
                _dispatcher.Invoke(() =>
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
                _dispatcher.Invoke(() =>
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

            _regenerateDescBtn.IsEnabled = false;
            _regenerateDescBtn.Content = "Regenerating...";

            await GenerateProjectDescriptionInBackground(entry);

            _regenerateDescBtn.Content = "Regenerate Descriptions";
            _regenerateDescBtn.IsEnabled = true;
            RefreshDescriptionBoxes();
        }

        public void SaveShortDesc()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.ShortDescription = _shortDescBox.Text;
                SaveProjects();
            }
            _editShortDescToggle.IsChecked = false;
        }

        public void SaveLongDesc()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.LongDescription = _longDescBox.Text;
                SaveProjects();
            }
            _editLongDescToggle.IsChecked = false;
        }

        public void RefreshProjectList(
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            if (_projectListPanel == null) return;
            _projectListPanel.Children.Clear();

            foreach (var proj in _savedProjects)
            {
                var isActive = proj.Path == _projectPath;

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(isActive ? 3 : 1, 1, 1, 1),
                    BorderBrush = new SolidColorBrush(isActive
                        ? Color.FromRgb(0xDA, 0x77, 0x56)
                        : Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    Cursor = Cursors.Hand
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoPanel = new StackPanel();

                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var nameColor = !string.IsNullOrEmpty(proj.Color)
                    ? (Color)ColorConverter.ConvertFromString(proj.Color)
                    : Color.FromRgb(0xE8, 0xE8, 0xE8);
                var nameBlock = new TextBlock
                {
                    Text = proj.DisplayName,
                    Foreground = new SolidColorBrush(nameColor),
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
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
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
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                        Background = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                        BorderThickness = new Thickness(1),
                        FontWeight = FontWeights.Bold,
                        FontSize = 14,
                        FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(2, 0, 2, 0),
                        MinWidth = 80,
                        CaretBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                        SelectionBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56))
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
                        }
                        RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    }

                    editBox.KeyDown += (_, ke) =>
                    {
                        if (ke.Key == Key.Enter) CommitRename();
                        else if (ke.Key == Key.Escape) RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    };

                    _dispatcher.BeginInvoke(new Action(() =>
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
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
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
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30)),
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
                        Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        Margin = new Thickness(0, 4, 0, 0),
                        ToolTip = proj.ShortDescription
                    });
                }

                var initColor = proj.IsInitialized
                    ? Color.FromRgb(0x4C, 0xAF, 0x50)
                    : Color.FromRgb(0x66, 0x66, 0x66);
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
                    Fill = new SolidColorBrush(initColor),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                initIndicator.Children.Add(new TextBlock
                {
                    Text = proj.IsInitialized ? "Initialized" : "Not Initialized",
                    Foreground = new SolidColorBrush(initColor),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                infoPanel.Children.Add(initIndicator);

                var mcpColor = proj.McpStatus switch
                {
                    McpStatus.Disabled => Color.FromRgb(0x66, 0x66, 0x66),
                    McpStatus.Initialized => Color.FromRgb(0xE0, 0xA0, 0x30),
                    McpStatus.Investigating => Color.FromRgb(0xE0, 0x80, 0x30),
                    McpStatus.Enabled => Color.FromRgb(0x4C, 0xAF, 0x50),
                    _ => Color.FromRgb(0x66, 0x66, 0x66)
                };
                var mcpIndicator = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = $"MCP: {proj.McpStatus}"
                };
                mcpIndicator.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(mcpColor),
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                mcpIndicator.Children.Add(new TextBlock
                {
                    Text = "MCP",
                    Foreground = new SolidColorBrush(mcpColor),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                infoPanel.Children.Add(mcpIndicator);

                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

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
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
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

                if (proj.McpStatus == McpStatus.Investigating)
                {
                    var cancelMcpBtn = new Button
                    {
                        Content = "Cancel MCP",
                        Style = (Style)Application.Current.FindResource("DangerBtn"),
                        Margin = new Thickness(0, 4, 0, 0),
                        Tag = proj.Path
                    };
                    cancelMcpBtn.Click += (s, ev) => { ev.Handled = true; CancelMcp(s as Button); };
                    btnPanel.Children.Add(cancelMcpBtn);
                }
                else
                {
                    var connectBtn = new Button
                    {
                        Content = proj.McpStatus == McpStatus.Enabled ? "MCP OK" : "Connect MCP",
                        Style = (Style)Application.Current.FindResource(
                            proj.McpStatus == McpStatus.Enabled ? "SuccessBtn" : "SmallBtn"),
                        Background = proj.McpStatus == McpStatus.Enabled
                            ? new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C))
                            : new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                        Margin = new Thickness(0, 4, 0, 0),
                        Tag = proj.Path
                    };
                    connectBtn.Click += async (s, ev) => { ev.Handled = true; await ConnectMcpAsync(s as Button); };
                    btnPanel.Children.Add(connectBtn);
                }

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
                    // Defer heavy work so we're not modifying the visual tree
                    // from inside the click handler of a card that will be destroyed.
                    // Use async to yield between operations and keep the UI responsive.
                    _dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            updateTerminalWorkingDirectory?.Invoke(projPath);
                            // Yield to let UI repaint after terminal update
                            await System.Threading.Tasks.Task.Yield();
                            saveSettings?.Invoke();
                            syncSettings?.Invoke();
                        }
                        catch (Exception ex) { AppLogger.Warn("ProjectManager", "Failed during project swap", ex); }
                        finally
                        {
                            _isSwapping = false;
                        }
                    }));
                };

                _projectListPanel.Children.Add(card);
            }
        }

        public async System.Threading.Tasks.Task ConnectMcpAsync(Button? btn)
        {
            if (btn?.Tag is not string projectPath) return;

            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            btn.IsEnabled = false;
            btn.Content = "Checking...";

            var mcpUrl = "http://127.0.0.1:8080/mcp";
            var mcpJsonPath = Path.Combine(projectPath, ".mcp.json");
            var mcpJsonContent = "{\n  \"mcpServers\": {\n    \"mcp-for-unity-server\": {\n      \"type\": \"http\",\n      \"url\": \"" + mcpUrl + "\"\n    }\n  }\n}";

            var reachable = await CheckMcpHealth(mcpUrl);

            if (reachable)
            {
                if (!File.Exists(mcpJsonPath))
                    File.WriteAllText(mcpJsonPath, mcpJsonContent);

                entry.McpStatus = McpStatus.Enabled;
                SaveProjects();
                RefreshProjectList(null, null, null);
                UpdateMcpToggleForProject();
                return;
            }

            if (!File.Exists(mcpJsonPath))
            {
                File.WriteAllText(mcpJsonPath, mcpJsonContent);
                entry.McpStatus = McpStatus.Initialized;
                SaveProjects();
                RefreshProjectList(null, null, null);

                reachable = await CheckMcpHealth(mcpUrl);
                if (reachable)
                {
                    entry.McpStatus = McpStatus.Enabled;
                    SaveProjects();
                    RefreshProjectList(null, null, null);
                    UpdateMcpToggleForProject();
                    return;
                }
            }

            entry.McpStatus = McpStatus.Investigating;
            SaveProjects();
            RefreshProjectList(null, null, null);

            var investigateTask = new AgentTask
            {
                Description = "The MCP server mcp-for-unity-server at http://127.0.0.1:8080/mcp is not responding. " +
                    "Diagnose and fix the connection. Check if the Unity Editor is running with the MCP plugin installed and enabled.",
                SkipPermissions = true,
                ProjectPath = projectPath,
                ProjectColor = GetProjectColor(projectPath)
            };
            McpInvestigationRequested?.Invoke(investigateTask);
        }

        public void CancelMcp(Button? btn)
        {
            if (btn?.Tag is not string projectPath) return;

            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            entry.McpStatus = McpStatus.Disabled;
            SaveProjects();
            RefreshProjectList(null, null, null);
            UpdateMcpToggleForProject();
        }

        private async System.Threading.Tasks.Task<bool> CheckMcpHealth(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProjectManager", $"MCP health check failed for {url}: {ex.Message}");
                return false;
            }
        }
    }
}
