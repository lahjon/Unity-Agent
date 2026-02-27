using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UnityAgent
{
    public enum McpStatus
    {
        Disabled,
        Initialized,
        Investigating,
        Enabled
    }

    public class ProjectEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public McpStatus McpStatus { get; set; } = McpStatus.Disabled;
        public string ShortDescription { get; set; } = "";
        public string LongDescription { get; set; } = "";

        [JsonIgnore]
        public bool IsInitializing { get; set; }
        [JsonIgnore]
        public string FolderName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FolderName : Name;
    }

    public class StringToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string colorStr && !string.IsNullOrEmpty(colorStr))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorStr);
                    return new SolidColorBrush(color);
                }
                catch { }
            }
            return new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }

    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Enable dark title bar on Windows 10/11
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }

            // Load saved system prompt (or use default)
            LoadSystemPrompt();
            SystemPromptBox.Text = SystemPrompt;

            // Sync history retention combo with loaded setting
            foreach (ComboBoxItem item in HistoryRetentionCombo.Items)
            {
                if (int.TryParse(item.Tag?.ToString(), out var h) && h == _historyRetentionHours)
                {
                    HistoryRetentionCombo.SelectedItem = item;
                    break;
                }
            }
        }

        private void ProjectsResize_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var newHeight = ProjectsPanelRow.Height.Value + e.VerticalChange;
            if (newHeight >= ProjectsPanelRow.MinHeight)
            {
                ProjectsPanelRow.Height = new GridLength(newHeight);
                if (newHeight >= PromptPanelRow.MinHeight)
                    PromptPanelRow.Height = new GridLength(newHeight);
            }
        }

        private void PromptResize_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var newHeight = PromptPanelRow.Height.Value + e.VerticalChange;
            if (newHeight >= PromptPanelRow.MinHeight)
            {
                PromptPanelRow.Height = new GridLength(newHeight);
                if (newHeight >= ProjectsPanelRow.MinHeight)
                    ProjectsPanelRow.Height = new GridLength(newHeight);
            }
        }

        private static bool ShowDarkConfirm(string message, string title)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true
            };

            var outerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            // Allow dragging the dialog by its border
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var result = false;
            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            };

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Background = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Padding = new Thickness(18, 8, 18, 8),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) => { result = false; dlg.Close(); };

            var confirmBtn = new Button
            {
                Content = "Confirm",
                Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Padding = new Thickness(18, 8, 18, 8),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            confirmBtn.Click += (_, _) => { result = true; dlg.Close(); };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(confirmBtn);

            stack.Children.Add(titleBlock);
            stack.Children.Add(msgBlock);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            dlg.Owner = Application.Current.MainWindow;
            dlg.ShowDialog();
            return result;
        }

        private static void ShowDarkAlert(string message, string title)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true
            };

            var outerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            };

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okBtn = new Button
            {
                Content = "OK",
                Background = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Padding = new Thickness(24, 8, 24, 8),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            okBtn.Click += (_, _) => dlg.Close();

            btnPanel.Children.Add(okBtn);

            stack.Children.Add(titleBlock);
            stack.Children.Add(msgBlock);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            dlg.Owner = Application.Current.MainWindow;
            dlg.ShowDialog();
        }

        // ── System Prompt Persistence ────────────────────────────────

        private string SystemPromptFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityAgent", "system_prompt.txt");

        private void LoadSystemPrompt()
        {
            try
            {
                if (File.Exists(SystemPromptFile))
                {
                    var text = File.ReadAllText(SystemPromptFile);
                    // Strip legacy baked-in MCP block (now added dynamically via toggle)
                    if (text.Contains("# MCP VERIFICATION"))
                    {
                        text = text.Replace(TaskLauncher.McpPromptBlock, "");
                        File.WriteAllText(SystemPromptFile, text);
                    }
                    SystemPrompt = text;
                    SystemPromptBox.Text = SystemPrompt;
                }
            }
            catch { }
        }

        private void EditSystemPromptToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditSystemPromptToggle.IsChecked == true;
            SystemPromptBox.IsReadOnly = !editing;
            SystemPromptBox.Opacity = editing ? 1.0 : 0.6;
            PromptEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SavePrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SystemPrompt = SystemPromptBox.Text;
                Directory.CreateDirectory(Path.GetDirectoryName(SystemPromptFile)!);
                File.WriteAllText(SystemPromptFile, SystemPrompt);
            }
            catch { }

            EditSystemPromptToggle.IsChecked = false;
        }

        private void ResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(SystemPromptFile))
                    File.Delete(SystemPromptFile);
            }
            catch { }

            SystemPrompt = DefaultSystemPrompt;
            SystemPromptBox.Text = SystemPrompt;
            EditSystemPromptToggle.IsChecked = false;
        }

        // ── Settings Sync on Project Switch ───────────────────────

        private void SyncSettingsForProject()
        {
            RefreshProjectCombo();          // Updates active label + descriptions
            RefreshProjectList();           // Rebuilds project cards
            UpdateMcpToggleForProject();    // Syncs MCP toggle
            RefreshFilterCombos();          // Rebuilds task list filters

            // Auto-select current project in both task list filter combos
            SelectProjectInFilterCombo(ActiveFilterCombo, _projectPath, ActiveFilter_Changed);
            SelectProjectInFilterCombo(HistoryFilterCombo, _projectPath, HistoryFilter_Changed);

            // Reset system prompt edit toggle (discard unsaved edits)
            if (EditSystemPromptToggle.IsChecked == true)
            {
                EditSystemPromptToggle.IsChecked = false;
                SystemPromptBox.Text = SystemPrompt;
            }

            UpdateStatus();
        }

        private void SelectProjectInFilterCombo(ComboBox combo, string projectPath,
            SelectionChangedEventHandler handler)
        {
            combo.SelectionChanged -= handler;
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag as string == projectPath)
                {
                    combo.SelectedItem = item;
                    combo.SelectionChanged += handler;
                    handler(combo, null!);
                    return;
                }
            }
            // Fallback: keep "All Projects" selected
            combo.SelectedIndex = 0;
            combo.SelectionChanged += handler;
            handler(combo, null!);
        }

        // ── Project Description Editing ────────────────────────────

        private void RefreshDescriptionBoxes()
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            var initializing = entry?.IsInitializing == true;

            if (initializing)
            {
                ShortDescBox.Text = "Initializing...";
                LongDescBox.Text = "Initializing...";
                ShortDescBox.FontStyle = FontStyles.Italic;
                LongDescBox.FontStyle = FontStyles.Italic;
            }
            else
            {
                ShortDescBox.Text = entry?.ShortDescription ?? "";
                LongDescBox.Text = entry?.LongDescription ?? "";
                ShortDescBox.FontStyle = FontStyles.Normal;
                LongDescBox.FontStyle = FontStyles.Normal;
            }

            // Reset edit state and disable toggles while initializing
            EditShortDescToggle.IsChecked = false;
            EditLongDescToggle.IsChecked = false;
            EditShortDescToggle.IsEnabled = !initializing;
            EditLongDescToggle.IsEnabled = !initializing;
        }

        private void EditShortDescToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditShortDescToggle.IsChecked == true;
            ShortDescBox.IsReadOnly = !editing;
            ShortDescBox.Opacity = editing ? 1.0 : 0.6;
            ShortDescEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EditLongDescToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditLongDescToggle.IsChecked == true;
            LongDescBox.IsReadOnly = !editing;
            LongDescBox.Opacity = editing ? 1.0 : 0.6;
            LongDescEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveShortDesc_Click(object sender, RoutedEventArgs e)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.ShortDescription = ShortDescBox.Text;
                SaveProjects();
            }
            EditShortDescToggle.IsChecked = false;
        }

        private void SaveLongDesc_Click(object sender, RoutedEventArgs e)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry != null)
            {
                entry.LongDescription = LongDescBox.Text;
                SaveProjects();
            }
            EditLongDescToggle.IsChecked = false;
        }

        private async void RegenerateDescriptions_Click(object sender, RoutedEventArgs e)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == _projectPath);
            if (entry == null) return;

            entry.IsInitializing = true;
            entry.ShortDescription = "";
            entry.LongDescription = "";
            SaveProjects();
            RefreshProjectCombo();
            RefreshProjectList();
            RefreshDescriptionBoxes();

            RegenerateDescBtn.IsEnabled = false;
            RegenerateDescBtn.Content = "Regenerating...";

            await GenerateProjectDescriptionInBackground(entry);

            RegenerateDescBtn.Content = "Regenerate Descriptions";
            RegenerateDescBtn.IsEnabled = true;
            RefreshDescriptionBoxes();
        }

        private void DefaultToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (SkipPermissionsToggle != null && DefaultSkipPermsToggle != null)
                SkipPermissionsToggle.IsChecked = DefaultSkipPermsToggle.IsChecked == true;
        }

        private void OvernightToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ExecuteButton == null) return;
            ExecuteButton.Content = OvernightToggle.IsChecked == true ? "Start Overnight Task" : "Execute Task";
        }

        private void HistoryRetention_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryRetentionCombo?.SelectedItem is not ComboBoxItem item) return;
            if (int.TryParse(item.Tag?.ToString(), out var hours))
            {
                _historyRetentionHours = hours;
                SaveSettings();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFile)) return;
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(_settingsFile));
                if (dict == null) return;

                if (dict.TryGetValue("historyRetentionHours", out var val))
                    _historyRetentionHours = val.GetInt32();
                if (dict.TryGetValue("selectedProject", out var sp))
                    _lastSelectedProject = sp.GetString();
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var dict = new Dictionary<string, object>
                {
                    ["historyRetentionHours"] = _historyRetentionHours,
                    ["selectedProject"] = _projectPath ?? ""
                };
                File.WriteAllText(_settingsFile,
                    JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private const string DefaultSystemPrompt = TaskLauncher.DefaultSystemPrompt;

        private string SystemPrompt;

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private readonly Dictionary<string, TabItem> _tabs = new();
        private readonly Dictionary<string, TextBox> _outputBoxes = new();
        private Button? _overflowBtn;
        private readonly DispatcherTimer _statusTimer;
        private string _projectPath;
        private string? _lastSelectedProject;
        private readonly string _historyFile;
        private readonly string _scriptDir;
        private readonly string _projectsFile;
        private List<ProjectEntry> _savedProjects = new();
        private readonly List<string> _attachedImages = new();
        private readonly string _imageDir;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        private ICollectionView? _activeView;
        private ICollectionView? _historyView;

        private int _historyRetentionHours = 24;
        private readonly string _settingsFile;

        // File locking
        private readonly Dictionary<string, FileLock> _fileLocks = new();
        private readonly ObservableCollection<FileLock> _fileLocksView = new();
        private readonly Dictionary<string, HashSet<string>> _taskLockedFiles = new();
        private readonly Dictionary<string, QueuedTaskInfo> _queuedTaskInfo = new();
        private readonly Dictionary<string, StreamingToolState> _streamingToolState = new();

        // Terminal
        private TerminalTabManager? _terminalManager;

        public MainWindow()
        {
            InitializeComponent();

            SystemPrompt = DefaultSystemPrompt;

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityAgent");
            Directory.CreateDirectory(appDataDir);

            _historyFile = Path.Combine(appDataDir, "task_history.json");
            _projectsFile = Path.Combine(appDataDir, "projects.json");
            _settingsFile = Path.Combine(appDataDir, "settings.json");
            _scriptDir = Path.Combine(appDataDir, "scripts");
            _imageDir = Path.Combine(appDataDir, "images");
            Directory.CreateDirectory(_scriptDir);
            Directory.CreateDirectory(_imageDir);

            LoadSettings();
            LoadProjects();
            var restored = _lastSelectedProject != null && _savedProjects.Any(p => p.Path == _lastSelectedProject);
            _projectPath = restored ? _lastSelectedProject! :
                           _savedProjects.Count > 0 ? _savedProjects[0].Path : Directory.GetCurrentDirectory();

            _activeView = CollectionViewSource.GetDefaultView(_activeTasks);
            _historyView = CollectionViewSource.GetDefaultView(_historyTasks);
            ActiveTasksList.ItemsSource = _activeView;
            HistoryTasksList.ItemsSource = _historyView;

            FileLocksListView.ItemsSource = _fileLocksView;

            LoadHistory();
            RefreshFilterCombos();
            RefreshProjectList();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, _) =>
            {
                foreach (var t in _activeTasks)
                    t.OnPropertyChanged(nameof(t.TimeInfo));
                CleanupOldHistory();
                UpdateStatus();
            };
            _statusTimer.Start();

            Closing += OnWindowClosing;
            UpdateStatus();

            _terminalManager = new TerminalTabManager(
                TerminalTabBar, TerminalOutput, TerminalInput,
                TerminalSendBtn, TerminalInterruptBtn, TerminalRootPath,
                Dispatcher, _projectPath);
            _terminalManager.AddTerminal();
        }

        // ── Project Management ───────────────────────────────────────

        private static readonly JsonSerializerOptions _projectJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private void LoadProjects()
        {
            try
            {
                if (File.Exists(_projectsFile))
                {
                    var json = File.ReadAllText(_projectsFile);
                    // Try new format (List<ProjectEntry>) first
                    try
                    {
                        _savedProjects = JsonSerializer.Deserialize<List<ProjectEntry>>(json, _projectJsonOptions) ?? new();
                    }
                    catch
                    {
                        // Migrate from old format (List<string>)
                        var oldList = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                        _savedProjects = oldList.Select(p => new ProjectEntry { Path = p }).ToList();
                        SaveProjects();
                    }
                }
            }
            catch { _savedProjects = new(); }

            RefreshProjectCombo();
            UpdateMcpToggleForProject();
        }

        private void SaveProjects()
        {
            try
            {
                File.WriteAllText(_projectsFile,
                    JsonSerializer.Serialize(_savedProjects, _projectJsonOptions));
            }
            catch { }
        }

        private void RefreshProjectCombo()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            ActiveProjectLabel.Text = proj?.DisplayName ?? System.IO.Path.GetFileName(_projectPath);
            ActiveProjectLabel.ToolTip = _projectPath;
            RefreshDescriptionBoxes();
        }

        private string _addProjectSelectedPath = "";

        private void AddProjectPath_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a project folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _addProjectSelectedPath = dialog.SelectedPath;
                AddProjectPath.Text = dialog.SelectedPath;
                AddProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8E8E8"));
            }
        }

        private void AddProject_Click(object sender, RoutedEventArgs e)
        {
            var path = _addProjectSelectedPath;

            if (string.IsNullOrEmpty(path))
            {
                ShowDarkAlert("Please select a project folder.", "No Folder Selected");
                return;
            }
            if (!Directory.Exists(path))
            {
                ShowDarkAlert("The selected path does not exist or is invalid.", "Invalid Path");
                _addProjectSelectedPath = "";
                AddProjectPath.Text = "Click to select project folder...";
                AddProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));
                return;
            }
            if (_savedProjects.Any(p => p.Path == path))
            {
                ShowDarkAlert("This project path is already added.", "Duplicate");
                return;
            }

            var name = System.IO.Path.GetFileName(path);
            var entry = new ProjectEntry { Name = name, Path = path, IsInitializing = true };
            _savedProjects.Add(entry);
            SaveProjects();
            _projectPath = path;
            _terminalManager?.UpdateWorkingDirectory(path);
            SaveSettings();
            SyncSettingsForProject();

            _addProjectSelectedPath = "";
            AddProjectPath.Text = "Click to select project folder...";
            AddProjectPath.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));

            // Generate project description in background
            _ = GenerateProjectDescriptionInBackground(entry);
        }

        private async System.Threading.Tasks.Task GenerateProjectDescriptionInBackground(ProjectEntry entry)
        {
            try
            {
                var (shortDesc, longDesc) = await TaskLauncher.GenerateProjectDescriptionAsync(entry.Path);
                Dispatcher.Invoke(() =>
                {
                    entry.ShortDescription = shortDesc;
                    entry.LongDescription = longDesc;
                    entry.IsInitializing = false;
                    SaveProjects();
                    RefreshProjectCombo();
                    RefreshProjectList();
                });
            }
            catch
            {
                Dispatcher.Invoke(() =>
                {
                    entry.IsInitializing = false;
                    RefreshProjectList();
                });
            }
        }

        private void RemoveProject_Click(string projectPath)
        {
            if (!ShowDarkConfirm($"Remove this project from the list?\n\n{projectPath}", "Remove Project"))
                return;

            _savedProjects.RemoveAll(p => p.Path == projectPath);
            SaveProjects();
            _projectPath = _savedProjects.Count > 0 ? _savedProjects[0].Path : Directory.GetCurrentDirectory();
            _terminalManager?.UpdateWorkingDirectory(_projectPath);
            SaveSettings();
            SyncSettingsForProject();
        }

        private void ProjectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Legacy handler kept for compatibility - no longer used
        }

        private void UpdateMcpToggleForProject()
        {
            var proj = _savedProjects.Find(p => p.Path == _projectPath);
            UseMcpToggle.IsChecked = proj?.McpStatus == McpStatus.Enabled;
        }

        // ── Execute ────────────────────────────────────────────────

        private void TaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Execute_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Check for image on clipboard before TextBox handles it as text
                if (Clipboard.ContainsImage())
                {
                    try
                    {
                        var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            SaveClipboardImage(image);
                            e.Handled = true;
                            return;
                        }
                    }
                    catch { }
                }

                // Also check for image files on clipboard (e.g. copied from Explorer)
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    var added = false;
                    foreach (string? file in files)
                    {
                        if (file != null && IsImageFile(file))
                        {
                            _attachedImages.Add(file);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        UpdateImageIndicator();
                        e.Handled = true;
                        return;
                    }
                }
                // Otherwise let default text paste happen
            }
        }

        private void SaveClipboardImage(BitmapSource image)
        {
            var fileName = $"paste_{DateTime.Now:yyyyMMdd_HHmmss}_{_attachedImages.Count}.png";
            var filePath = Path.Combine(_imageDir, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            _attachedImages.Add(filePath);
            UpdateImageIndicator();
        }

        private void TaskInput_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Any(IsImageFile))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                    return;
                }
            }
            // Allow normal text drag-drop
        }

        private void TaskInput_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    var added = false;
                    foreach (var file in files)
                    {
                        if (IsImageFile(file))
                        {
                            _attachedImages.Add(file);
                            added = true;
                        }
                    }
                    if (added)
                    {
                        UpdateImageIndicator();
                        e.Handled = true;
                    }
                }
            }
        }

        private static bool IsImageFile(string path) => TaskLauncher.IsImageFile(path);

        private void UpdateImageIndicator()
        {
            var count = _attachedImages.Count;
            ImageIndicator.Text = count > 0 ? $"[{count} image(s) attached]" : "";
            ImageIndicator.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearImagesBtn.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearImages_Click(object sender, RoutedEventArgs e)
        {
            foreach (var path in _attachedImages)
            {
                try { File.Delete(path); } catch { }
            }
            _attachedImages.Clear();
            UpdateImageIndicator();
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            var desc = TaskInput.Text?.Trim();
            if (!TaskLauncher.ValidateTaskInput(desc)) return;

            var task = TaskLauncher.CreateTask(
                desc!,
                _projectPath,
                SkipPermissionsToggle.IsChecked == true,
                RemoteSessionToggle.IsChecked == true,
                HeadlessToggle.IsChecked == true,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                _attachedImages.Count > 0 ? new List<string>(_attachedImages) : null);
            _attachedImages.Clear();
            UpdateImageIndicator();
            TaskInput.Clear();

            if (task.Headless)
            {
                LaunchHeadless(task);
                return;
            }

            // Create tab immediately with placeholder
            task.Summary = "Processing Task...";
            _activeTasks.Add(task);
            CreateTab(task);
            StartProcess(task);

            // Generate summary asynchronously in background
            _ = GenerateSummaryInBackground(task, desc!);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task GenerateSummaryInBackground(AgentTask task, string description)
        {
            try
            {
                var summary = await TaskLauncher.GenerateSummaryAsync(description);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    task.Summary = summary;
                    UpdateTabHeader(task);
                }
            }
            catch { }
        }

        // ── Tab Management ─────────────────────────────────────────

        private void CreateTab(AgentTask task)
        {
            // --- Output area ---
            var outputBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };
            _outputBoxes[task.Id] = outputBox;

            // --- Input bar at bottom ---
            var inputBox = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var sendBtn = new Button
            {
                Content = "Send",
                Background = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Immediately cancel this task (Escape)"
            };

            // Send handler: writes to stdin
            sendBtn.Click += (_, _) => SendInput(task, inputBox);
            inputBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    SendInput(task, inputBox);
                    ke.Handled = true;
                }
            };

            // Cancel handler: immediately kills the task with no confirmation
            cancelBtn.Click += (_, _) => CancelTaskImmediate(task);

            var inputPanel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(cancelBtn, Dock.Right);
            DockPanel.SetDock(sendBtn, Dock.Right);
            inputPanel.Children.Add(cancelBtn);
            inputPanel.Children.Add(sendBtn);
            inputPanel.Children.Add(inputBox);

            // --- Assemble tab content ---
            var content = new DockPanel();
            DockPanel.SetDock(inputPanel, Dock.Bottom);
            content.Children.Add(inputPanel);
            content.Children.Add(outputBox); // fills remaining space

            // --- Tab header with close button ---
            var header = CreateTabHeader(task);

            var tabItem = new TabItem
            {
                Header = header,
                Content = content,
                Tag = task.Id
            };

            // Middle-click to close
            tabItem.PreviewMouseDown += (_, me) =>
            {
                if (me.MiddleButton == MouseButtonState.Pressed)
                {
                    CloseTab(task);
                    me.Handled = true;
                }
            };

            _tabs[task.Id] = tabItem;
            OutputTabs.Items.Add(tabItem);
            OutputTabs.SelectedItem = tabItem;
            UpdateOutputTabWidths();

        }

        private StackPanel CreateTabHeader(AgentTask task)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xD4, 0x4D)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = task.ShortDescription,
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0))
            };

            var closeBtn = new Button
            {
                Content = "\uE8BB",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Hover template: monochrome highlight
            var bdFactory = new FrameworkElementFactory(typeof(Border));
            bdFactory.Name = "Bd";
            bdFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bdFactory.SetValue(FrameworkElement.WidthProperty, 18.0);
            bdFactory.SetValue(FrameworkElement.HeightProperty, 18.0);
            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdFactory.AppendChild(cpFactory);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), "Bd"));
            hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))));

            var closeBtnTemplate = new ControlTemplate(typeof(Button)) { VisualTree = bdFactory };
            closeBtnTemplate.Triggers.Add(hoverTrigger);
            closeBtn.Template = closeBtnTemplate;
            closeBtn.Click += (_, _) => CloseTab(task);

            panel.Children.Add(dot);
            panel.Children.Add(label);
            panel.Children.Add(closeBtn);
            return panel;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing || task.Status == AgentTaskStatus.Queued)
            {
                // If the process has already exited, treat as completed — no warning needed
                var processAlreadyDone = task.Process == null || task.Process.HasExited;
                if (processAlreadyDone)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime ??= DateTime.Now;
                }
                else
                {
                    if (!ShowDarkConfirm("This task is still running. Closing will terminate it.\n\nAre you sure?", "Task Running"))
                        return;

                    task.Status = AgentTaskStatus.Cancelled;
                    task.EndTime = DateTime.Now;
                    KillProcess(task);
                }

                ReleaseTaskLocks(task.Id);
                _queuedTaskInfo.Remove(task.Id);
                _streamingToolState.Remove(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                SaveHistory();
                CheckQueuedTasks();
            }
            else if (_activeTasks.Contains(task))
            {
                // Task is done/failed but still in active list — move to history without warning
                task.EndTime ??= DateTime.Now;
                ReleaseTaskLocks(task.Id);
                _queuedTaskInfo.Remove(task.Id);
                _streamingToolState.Remove(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                SaveHistory();
            }

            if (_tabs.TryGetValue(task.Id, out var tab))
            {
                OutputTabs.Items.Remove(tab);
                _tabs.Remove(task.Id);
            }
            _outputBoxes.Remove(task.Id);
            UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void UpdateTabHeader(AgentTask task)
        {
            if (!_tabs.TryGetValue(task.Id, out var tab)) return;
            if (tab.Header is StackPanel sp && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
            {
                var color = task.Status switch
                {
                    AgentTaskStatus.Running => Color.FromRgb(0xE8, 0xD4, 0x4D),
                    AgentTaskStatus.Completed => Color.FromRgb(0x2E, 0x7D, 0x32),
                    AgentTaskStatus.Cancelled => Color.FromRgb(0xE0, 0xA0, 0x30),
                    AgentTaskStatus.Failed => Color.FromRgb(0xE0, 0x55, 0x55),
                    AgentTaskStatus.Queued => Color.FromRgb(0xCC, 0x88, 0x00),
                    AgentTaskStatus.Ongoing => Color.FromRgb(0xE8, 0xD4, 0x4D),
                    _ => Color.FromRgb(0x55, 0x55, 0x55)
                };
                dot.Fill = new SolidColorBrush(color);

                // Update label text with summary if available
                if (sp.Children.Count > 1 && sp.Children[1] is TextBlock label)
                    label.Text = task.ShortDescription;

                // Update close button: checkmark for done/failed, X otherwise
                if (sp.Children.Count > 2 && sp.Children[2] is Button closeBtn)
                {
                    var isDone = task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;
                    closeBtn.Content = isDone ? "\uE73E" : "\uE8BB"; // CheckMark vs ChromeClose
                    closeBtn.Foreground = isDone
                        ? new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C))
                        : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                }
            }
        }

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOutputTabWidths();

        private void EnsureOverflowButton()
        {
            if (_overflowBtn != null) return;
            OutputTabs.ApplyTemplate();
            _overflowBtn = OutputTabs.Template.FindName("PART_OverflowButton", OutputTabs) as Button;
            if (_overflowBtn != null)
                _overflowBtn.Click += TabOverflow_Click;
        }

        private void UpdateOutputTabWidths()
        {
            EnsureOverflowButton();

            const double maxWidth = 200.0;
            const double minWidth = 60.0;
            // Overhead inside each tab: border padding (8+8) + dot (8+5) + close btn (18+6) + margin slack
            const double headerOverhead = 55.0;
            const double overflowBtnWidth = 28.0;

            int count = OutputTabs.Items.Count;
            if (count == 0)
            {
                if (_overflowBtn != null) _overflowBtn.Visibility = Visibility.Collapsed;
                return;
            }

            double available = OutputTabs.ActualWidth;
            if (available <= 0) available = 500;

            bool overflow = count * minWidth > available;
            if (_overflowBtn != null)
                _overflowBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            double tabAvailable = overflow ? available - overflowBtnWidth : available;
            double tabWidth = Math.Max(minWidth, Math.Min(maxWidth, tabAvailable / count));
            double labelMax = Math.Max(20, tabWidth - headerOverhead);

            foreach (var item in OutputTabs.Items)
            {
                if (item is TabItem tab)
                {
                    tab.Width = tabWidth;
                    // Update the label MaxWidth inside the header so text truncates properly
                    if (tab.Header is StackPanel sp)
                    {
                        foreach (var child in sp.Children)
                        {
                            if (child is TextBlock tb)
                            {
                                tb.MaxWidth = labelMax;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void TabOverflow_Click(object sender, RoutedEventArgs e)
        {
            var popup = new Popup
            {
                PlacementTarget = _overflowBtn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 150,
                MaxHeight = 300
            };

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var stack = new StackPanel();

            foreach (var item in OutputTabs.Items)
            {
                if (item is TabItem tab)
                {
                    string text = "Tab";
                    if (tab.Header is StackPanel sp)
                    {
                        foreach (var child in sp.Children)
                        {
                            if (child is TextBlock tb)
                            {
                                text = tb.Text;
                                break;
                            }
                        }
                    }

                    bool isSelected = tab == OutputTabs.SelectedItem;
                    var itemBorder = new Border
                    {
                        Background = isSelected
                            ? new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35))
                            : Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 1, 0, 1),
                        Cursor = Cursors.Hand
                    };

                    var textBlock = new TextBlock
                    {
                        Text = text,
                        Foreground = isSelected
                            ? new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56))
                            : new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 200
                    };

                    itemBorder.Child = textBlock;

                    var capturedTab = tab;
                    var capturedBorder = itemBorder;
                    var capturedSelected = isSelected;

                    itemBorder.MouseEnter += (_, _) =>
                    {
                        if (!capturedSelected)
                            capturedBorder.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
                    };
                    itemBorder.MouseLeave += (_, _) =>
                    {
                        if (!capturedSelected)
                            capturedBorder.Background = Brushes.Transparent;
                    };
                    itemBorder.MouseLeftButtonDown += (_, _) =>
                    {
                        OutputTabs.SelectedItem = capturedTab;
                        capturedTab.BringIntoView();
                        popup.IsOpen = false;
                    };

                    stack.Children.Add(itemBorder);
                }
            }

            scroll.Content = stack;
            border.Child = scroll;
            popup.Child = border;
            popup.IsOpen = true;
        }

        // ── Process Management ─────────────────────────────────────

        private string GetProjectDescription(AgentTask task)
        {
            var entry = _savedProjects.FirstOrDefault(p => p.Path == task.ProjectPath);
            if (entry == null) return "";
            return task.ExtendedPlanning
                ? (string.IsNullOrWhiteSpace(entry.LongDescription) ? entry.ShortDescription : entry.LongDescription)
                : entry.ShortDescription;
        }

        private void LaunchHeadless(AgentTask task)
        {
            var fullPrompt = TaskLauncher.BuildFullPrompt(SystemPrompt, task, GetProjectDescription(task));
            var projectPath = task.ProjectPath;

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"headless_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildHeadlessPowerShellScript(projectPath, promptFile, task.SkipPermissions, task.RemoteSession, task.SpawnTeam),
                Encoding.UTF8);

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: true);
            psi.WorkingDirectory = projectPath;
            try
            {
                Process.Start(psi);
                UpdateStatus();
            }
            catch (Exception ex)
            {
                ShowDarkConfirm($"Failed to launch terminal:\n{ex.Message}", "Launch Error");
            }
        }

        private static string BuildPromptWithImages(string basePrompt, List<string> imagePaths)
            => TaskLauncher.BuildPromptWithImages(basePrompt, imagePaths);

        private void StartProcess(AgentTask task)
        {
            // Capture git HEAD before the task runs so we can diff at completion
            task.GitStartHash = TaskLauncher.CaptureGitHead(task.ProjectPath);

            if (task.IsOvernight)
                TaskLauncher.PrepareTaskForOvernightStart(task);

            var fullPrompt = TaskLauncher.BuildFullPrompt(SystemPrompt, task, GetProjectDescription(task));
            var projectPath = task.ProjectPath;

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            var claudeCmd = TaskLauncher.BuildClaudeCommand(task.SkipPermissions, task.RemoteSession, task.SpawnTeam);

            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(projectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            AppendOutput(task.Id, $"[UnityAgent] Task #{task.Id} starting...\n");
            if (!string.IsNullOrWhiteSpace(task.Summary))
                AppendOutput(task.Id, $"[UnityAgent] Summary: {task.Summary}\n");
            AppendOutput(task.Id, $"[UnityAgent] Project: {projectPath}\n");
            AppendOutput(task.Id, $"[UnityAgent] Skip permissions: {task.SkipPermissions}\n");
            AppendOutput(task.Id, $"[UnityAgent] Remote session: {task.RemoteSession}\n");
            if (task.ExtendedPlanning)
                AppendOutput(task.Id, $"[UnityAgent] Extended planning: ON\n");
            if (task.IsOvernight)
            {
                AppendOutput(task.Id, $"[UnityAgent] Overnight mode: ON (max {task.MaxIterations} iterations, 12h cap)\n");
                AppendOutput(task.Id, $"[UnityAgent] Safety: skip-permissions forced, git blocked, 30min iteration timeout\n");
            }
            AppendOutput(task.Id, $"[UnityAgent] Connecting to Claude...\n\n");

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: false);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = StripAnsi(e.Data).Trim();
                if (string.IsNullOrEmpty(line)) return;
                Dispatcher.BeginInvoke(() => ParseStreamJson(task.Id, line));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = StripAnsi(e.Data);
                if (!string.IsNullOrWhiteSpace(line))
                    Dispatcher.BeginInvoke(() => AppendOutput(task.Id, $"[stderr] {line}\n"));
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }

                    CleanupScripts(task.Id);
                    ReleaseTaskLocks(task.Id);

                    if (task.IsOvernight && task.Status == AgentTaskStatus.Running)
                    {
                        HandleOvernightIteration(task, exitCode);
                    }
                    else
                    {
                        task.Status = exitCode == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                        task.EndTime = DateTime.Now;
                        AppendCompletionSummary(task);
                        AppendOutput(task.Id, $"\n[UnityAgent] Process finished (exit code: {exitCode}). " +
                            "Use Done/Cancel to close, or send a follow-up.\n");
                        UpdateTabHeader(task);
                    }

                    CheckQueuedTasks();
                });
            };

            try
            {
                process.Start();
                task.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Per-iteration timeout for overnight first iteration
                if (task.IsOvernight)
                {
                    var iterationTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(OvernightIterationTimeoutMinutes)
                    };
                    task.OvernightIterationTimer = iterationTimer;
                    iterationTimer.Tick += (_, _) =>
                    {
                        iterationTimer.Stop();
                        task.OvernightIterationTimer = null;
                        if (task.Process is { HasExited: false })
                        {
                            AppendOutput(task.Id, $"\n[Overnight] Iteration timeout ({OvernightIterationTimeoutMinutes}min). Killing stuck process.\n");
                            try { task.Process.Kill(true); } catch { }
                        }
                    };
                    iterationTimer.Start();
                }
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[UnityAgent] ERROR starting process: {ex.Message}\n");
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                UpdateTabHeader(task);
                MoveToHistory(task);
            }
        }

        private void ParseStreamJson(string taskId, string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                switch (type)
                {
                    case "assistant":
                        // Claude is responding with text
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                                if (blockType == "text" && block.TryGetProperty("text", out var text))
                                {
                                    AppendOutput(taskId, text.GetString() + "\n");
                                }
                                else if (blockType == "tool_use")
                                {
                                    var toolName = block.TryGetProperty("name", out var tn) ? tn.GetString() : "unknown";
                                    AppendOutput(taskId, $"\n[Tool: {toolName}]\n");

                                    if (IsFileModifyTool(toolName) && block.TryGetProperty("input", out var input))
                                    {
                                        var fp = ExtractFilePath(input);
                                        if (!string.IsNullOrEmpty(fp) && !TryAcquireOrConflict(taskId, fp, toolName!))
                                            return;
                                        var inputStr = input.ToString();
                                        if (inputStr.Length > 200)
                                            inputStr = inputStr[..200] + "...";
                                        AppendOutput(taskId, $"  {inputStr}\n");
                                    }
                                    else if (block.TryGetProperty("input", out var inputOther))
                                    {
                                        var inputStr = inputOther.ToString();
                                        if (inputStr.Length > 200)
                                            inputStr = inputStr[..200] + "...";
                                        AppendOutput(taskId, $"  {inputStr}\n");
                                    }
                                }
                            }
                        }
                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var cb))
                        {
                            var cbType = cb.TryGetProperty("type", out var cbt) ? cbt.GetString() : null;
                            if (cbType == "tool_use")
                            {
                                var toolName = cb.TryGetProperty("name", out var tn) ? tn.GetString() : "tool";
                                AppendOutput(taskId, $"\n[Using tool: {toolName}]\n");

                                // Track streaming tool state for file-modify tools
                                _streamingToolState[taskId] = new StreamingToolState
                                {
                                    CurrentToolName = toolName,
                                    IsFileModifyTool = IsFileModifyTool(toolName),
                                    JsonAccumulator = new StringBuilder()
                                };
                            }
                            else if (cbType == "thinking")
                            {
                                AppendOutput(taskId, "[Thinking...]\n");
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                            if (deltaType == "text_delta" && delta.TryGetProperty("text", out var deltaText))
                            {
                                AppendOutput(taskId, deltaText.GetString() ?? "");
                            }
                            else if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinking))
                            {
                                var t = thinking.GetString() ?? "";
                                if (t.Length > 0)
                                    AppendOutput(taskId, t);
                            }
                            else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var partialJson))
                            {
                                // Accumulate JSON for file-modify tool detection
                                if (_streamingToolState.TryGetValue(taskId, out var state) && state.IsFileModifyTool && !state.FilePathChecked)
                                {
                                    state.JsonAccumulator.Append(partialJson.GetString() ?? "");
                                    var fp = TryExtractFilePathFromPartial(state.JsonAccumulator.ToString());
                                    if (fp != null)
                                    {
                                        state.FilePathChecked = true;
                                        if (!TryAcquireOrConflict(taskId, fp, state.CurrentToolName!))
                                            return;
                                    }
                                }
                            }
                        }
                        break;

                    case "content_block_stop":
                        // Final fallback: if streaming state exists and file_path wasn't checked yet
                        if (_streamingToolState.TryGetValue(taskId, out var stopState) && stopState.IsFileModifyTool && !stopState.FilePathChecked)
                        {
                            var accumulated = stopState.JsonAccumulator.ToString();
                            if (!string.IsNullOrEmpty(accumulated))
                            {
                                try
                                {
                                    using var inputDoc = JsonDocument.Parse("{" + accumulated + "}");
                                    var fp = ExtractFilePath(inputDoc.RootElement);
                                    if (!string.IsNullOrEmpty(fp))
                                    {
                                        stopState.FilePathChecked = true;
                                        if (!TryAcquireOrConflict(taskId, fp, stopState.CurrentToolName!))
                                        {
                                            _streamingToolState.Remove(taskId);
                                            return;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        _streamingToolState.Remove(taskId);
                        AppendOutput(taskId, "\n");
                        break;

                    case "result":
                        // Final result
                        if (root.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.String)
                        {
                            AppendOutput(taskId, $"\n{result.GetString()}\n");
                        }
                        else if (root.TryGetProperty("subtype", out var subtype))
                        {
                            AppendOutput(taskId, $"\n[Result: {subtype.GetString()}]\n");
                        }
                        break;

                    case "error":
                        var errMsg = root.TryGetProperty("error", out var err)
                            ? (err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString())
                            : "Unknown error";
                        AppendOutput(taskId, $"\n[Error] {errMsg}\n");
                        break;

                    default:
                        // Show other event types as-is for visibility
                        if (type != null && type != "ping" && type != "message_start" && type != "message_stop")
                            AppendOutput(taskId, $"[{type}]\n");
                        break;
                }
            }
            catch
            {
                // Not valid JSON — show raw line (fallback for non-stream output)
                if (!string.IsNullOrWhiteSpace(line))
                    AppendOutput(taskId, line + "\n");
            }
        }

        private void SendInput(AgentTask task, TextBox inputBox)
        {
            var text = inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            inputBox.Clear();

            // If task is still running and has stdin, write to it
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing && task.Process is { HasExited: false })
            {
                try
                {
                    AppendOutput(task.Id, $"\n> {text}\n");
                    task.Process.StandardInput.WriteLine(text);
                    return;
                }
                catch { /* stdin not available, fall through to --continue */ }
            }

            // If task finished, start a follow-up with --continue
            task.Status = AgentTaskStatus.Ongoing;
            task.EndTime = null;
            UpdateTabHeader(task);
            AppendOutput(task.Id, $"\n> {text}\n[UnityAgent] Sending follow-up with --continue...\n\n");

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var followUpFile = Path.Combine(_scriptDir, $"followup_{task.Id}_{DateTime.Now.Ticks}.txt");
            File.WriteAllText(followUpFile, text, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"followup_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{followUpFile}'\n" +
                $"claude -p{skipFlag} --continue $prompt\n",
                Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{ps1File}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Dispatcher.BeginInvoke(() => AppendOutput(task.Id, StripAnsi(e.Data) + "\n"));
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Dispatcher.BeginInvoke(() => AppendOutput(task.Id, StripAnsi(e.Data) + "\n"));
            };
            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var followUpExit = -1;
                    try { followUpExit = process.ExitCode; } catch { }
                    task.Status = followUpExit == 0 ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    AppendCompletionSummary(task);
                    AppendOutput(task.Id, "\n[UnityAgent] Follow-up complete.\n");
                    UpdateTabHeader(task);
                });
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[UnityAgent] Follow-up error: {ex.Message}\n");
            }
        }

        // ── Overnight Task Logic ─────────────────────────────────

        private const int OvernightMaxRuntimeHours = 12;
        private const int OvernightIterationTimeoutMinutes = 30;
        private const int OvernightMaxConsecutiveFailures = 3;
        private const int OvernightOutputCapChars = 100_000;

        private void HandleOvernightIteration(AgentTask task, int exitCode)
        {
            // Stop the per-iteration timeout timer
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }

            // Check if task was cancelled while running
            if (task.Status != AgentTaskStatus.Running) return;

            // Get only the current iteration's output for status/error checks
            var fullOutput = task.OutputBuilder.ToString();
            var iterationOutput = task.LastIterationOutputStart < fullOutput.Length
                ? fullOutput[task.LastIterationOutputStart..]
                : fullOutput;

            // Safety: total runtime cap (12 hours)
            var totalRuntime = DateTime.Now - task.StartTime;
            if (totalRuntime.TotalHours >= OvernightMaxRuntimeHours)
            {
                AppendOutput(task.Id, $"\n[Overnight] Total runtime cap ({OvernightMaxRuntimeHours}h) reached. Stopping.\n");
                FinishOvernightTask(task, AgentTaskStatus.Completed);
                return;
            }

            // Check for STATUS: COMPLETE
            if (CheckOvernightComplete(iterationOutput))
            {
                AppendOutput(task.Id, $"\n[Overnight] STATUS: COMPLETE detected at iteration {task.CurrentIteration}. Task finished.\n");
                FinishOvernightTask(task, AgentTaskStatus.Completed);
                return;
            }

            // Check if max iterations reached
            if (task.CurrentIteration >= task.MaxIterations)
            {
                AppendOutput(task.Id, $"\n[Overnight] Max iterations ({task.MaxIterations}) reached. Stopping.\n");
                FinishOvernightTask(task, AgentTaskStatus.Completed);
                return;
            }

            // Safety: consecutive failure detection (crash loop protection)
            if (exitCode != 0 && !IsTokenLimitError(iterationOutput))
            {
                task.ConsecutiveFailures++;
                AppendOutput(task.Id, $"\n[Overnight] Iteration exited with code {exitCode} (failure {task.ConsecutiveFailures}/{OvernightMaxConsecutiveFailures})\n");
                if (task.ConsecutiveFailures >= OvernightMaxConsecutiveFailures)
                {
                    AppendOutput(task.Id, $"\n[Overnight] {OvernightMaxConsecutiveFailures} consecutive failures detected (crash loop). Stopping.\n");
                    FinishOvernightTask(task, AgentTaskStatus.Failed);
                    return;
                }
            }
            else
            {
                // Reset on success
                task.ConsecutiveFailures = 0;
            }

            // Check for token/rate limit errors (only in current iteration output)
            if (IsTokenLimitError(iterationOutput))
            {
                AppendOutput(task.Id, "\n[Overnight] Token limit hit. Retrying in 30 minutes...\n");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
                task.OvernightRetryTimer = timer;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    task.OvernightRetryTimer = null;
                    if (task.Status != AgentTaskStatus.Running) return;
                    // Re-check runtime cap before retrying
                    if ((DateTime.Now - task.StartTime).TotalHours >= OvernightMaxRuntimeHours)
                    {
                        AppendOutput(task.Id, $"\n[Overnight] Runtime cap reached during retry wait. Stopping.\n");
                        FinishOvernightTask(task, AgentTaskStatus.Completed);
                        return;
                    }
                    AppendOutput(task.Id, "[Overnight] Retrying...\n");
                    StartOvernightContinuation(task);
                };
                timer.Start();
                return;
            }

            // Trim OutputBuilder to prevent memory bloat (keep last N chars)
            if (task.OutputBuilder.Length > OvernightOutputCapChars)
            {
                var trimmed = task.OutputBuilder.ToString(
                    task.OutputBuilder.Length - OvernightOutputCapChars, OvernightOutputCapChars);
                task.OutputBuilder.Clear();
                task.OutputBuilder.Append(trimmed);
                task.LastIterationOutputStart = 0;
            }

            // Normal continuation — increment and continue
            task.CurrentIteration++;
            task.LastIterationOutputStart = task.OutputBuilder.Length;
            AppendOutput(task.Id, $"\n[Overnight] Starting iteration {task.CurrentIteration}/{task.MaxIterations}...\n\n");
            StartOvernightContinuation(task);
        }

        private void FinishOvernightTask(AgentTask task, AgentTaskStatus status)
        {
            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            task.Status = status;
            task.EndTime = DateTime.Now;
            var duration = task.EndTime.Value - task.StartTime;
            AppendOutput(task.Id, $"[Overnight] Total runtime: {(int)duration.TotalHours}h {duration.Minutes}m across {task.CurrentIteration} iteration(s).\n");
            AppendCompletionSummary(task);
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private static bool IsTokenLimitError(string output) => TaskLauncher.IsTokenLimitError(output);

        private static bool CheckOvernightComplete(string output) => TaskLauncher.CheckOvernightComplete(output);

        private void StartOvernightContinuation(AgentTask task)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            // Mark where this iteration's output starts (for scoped error detection)
            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var continuationPrompt = TaskLauncher.BuildOvernightContinuationPrompt(task.CurrentIteration, task.MaxIterations);

            var promptFile = Path.Combine(_scriptDir, $"overnight_{task.Id}_{task.CurrentIteration}.txt");
            File.WriteAllText(promptFile, continuationPrompt, Encoding.UTF8);

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag} --verbose --continue --output-format stream-json $prompt";

            var ps1File = Path.Combine(_scriptDir, $"overnight_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                TaskLauncher.BuildPowerShellScript(task.ProjectPath, promptFile, claudeCmd),
                Encoding.UTF8);

            var psi = TaskLauncher.BuildProcessStartInfo(ps1File, headless: false);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = StripAnsi(e.Data).Trim();
                if (string.IsNullOrEmpty(line)) return;
                Dispatcher.BeginInvoke(() => ParseStreamJson(task.Id, line));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = StripAnsi(e.Data);
                if (!string.IsNullOrWhiteSpace(line))
                    Dispatcher.BeginInvoke(() => AppendOutput(task.Id, $"[stderr] {line}\n"));
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }
                    CleanupScripts(task.Id);
                    HandleOvernightIteration(task, exitCode);
                });
            };

            try
            {
                process.Start();
                task.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Per-iteration timeout: kill if stuck for too long
                var iterationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMinutes(OvernightIterationTimeoutMinutes)
                };
                task.OvernightIterationTimer = iterationTimer;
                iterationTimer.Tick += (_, _) =>
                {
                    iterationTimer.Stop();
                    task.OvernightIterationTimer = null;
                    if (task.Process is { HasExited: false })
                    {
                        AppendOutput(task.Id, $"\n[Overnight] Iteration timeout ({OvernightIterationTimeoutMinutes}min). Killing stuck process.\n");
                        try { task.Process.Kill(true); } catch { }
                    }
                };
                iterationTimer.Start();
            }
            catch (Exception ex)
            {
                AppendOutput(task.Id, $"[Overnight] ERROR starting continuation: {ex.Message}\n");
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                UpdateTabHeader(task);
                MoveToHistory(task);
            }
        }

        // ── Output ──────────────────────────────────────────────────

        private void AppendOutput(string taskId, string text)
        {
            if (!_outputBoxes.TryGetValue(taskId, out var box)) return;
            box.AppendText(text);
            box.ScrollToEnd();

            // Also append to OutputBuilder for overnight completion detection
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId)
                    ?? _historyTasks.FirstOrDefault(t => t.Id == taskId);
            task?.OutputBuilder.Append(text);
        }

        private void AppendCompletionSummary(AgentTask task)
        {
            var duration = (task.EndTime ?? DateTime.Now) - task.StartTime;
            var summary = TaskLauncher.GenerateCompletionSummary(
                task.ProjectPath, task.GitStartHash, task.Status, duration);
            task.CompletionSummary = summary;
            AppendOutput(task.Id, summary);
        }

        private void CleanupScripts(string taskId)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_scriptDir, $"*_{taskId}*"))
                    File.Delete(f);
            }
            catch { }
        }

        // ── Task Actions ───────────────────────────────────────────

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            // If task is queued, force-start it instead
            if (task.Status == AgentTaskStatus.Queued)
            {
                ForceStartQueuedTask(task);
                return;
            }

            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            KillProcess(task);
            AppendCompletionSummary(task);
            AppendOutput(task.Id, "\n[UnityAgent] Task marked as completed.\n");
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            CancelTask(task);
        }

        private void TaskCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (e.ChangedButton == MouseButton.Middle)
            {
                CancelTask(task);
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Left && _tabs.TryGetValue(task.Id, out var tab))
            {
    
                OutputTabs.SelectedItem = tab;
            }
        }

        private void CancelTask(AgentTask task)
        {
            // Finished tasks: just move to history without changing status
            if (task.IsFinished)
            {
                UpdateTabHeader(task);
                MoveToHistory(task);
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing)
            {
                if (!ShowDarkConfirm(
                    $"Task #{task.Id} is still running.\nAre you sure you want to cancel it?",
                    "Cancel Running Task"))
                    return;
            }
            // Stop overnight timers if active
            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            KillProcess(task);
            ReleaseTaskLocks(task.Id);
            _queuedTaskInfo.Remove(task.Id);
            _streamingToolState.Remove(task.Id);
            AppendOutput(task.Id, "\n[UnityAgent] Task cancelled.\n");
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void CancelTaskImmediate(AgentTask task)
        {
            if (task.IsFinished) return;

            if (task.OvernightRetryTimer != null)
            {
                task.OvernightRetryTimer.Stop();
                task.OvernightRetryTimer = null;
            }
            if (task.OvernightIterationTimer != null)
            {
                task.OvernightIterationTimer.Stop();
                task.OvernightIterationTimer = null;
            }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            KillProcess(task);
            ReleaseTaskLocks(task.Id);
            _queuedTaskInfo.Remove(task.Id);
            _streamingToolState.Remove(task.Id);
            AppendOutput(task.Id, "\n[UnityAgent] Task cancelled.\n");
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (_tabs.TryGetValue(task.Id, out var tab))
            {
                OutputTabs.Items.Remove(tab);
                _tabs.Remove(task.Id);
            }
            _outputBoxes.Remove(task.Id);
            _historyTasks.Remove(task);
            SaveHistory();
            RefreshFilterCombos();
            UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void ClearFinished_Click(object sender, RoutedEventArgs e)
        {
            var finished = _activeTasks.Where(t => t.IsFinished).ToList();
            if (finished.Count == 0) return;

            if (!ShowDarkConfirm(
                $"Are you sure you want to clear {finished.Count} finished task(s) from the active list?",
                "Clear Finished")) return;

            foreach (var task in finished)
            {
                if (_tabs.TryGetValue(task.Id, out var tab))
                {
                    OutputTabs.Items.Remove(tab);
                    _tabs.Remove(task.Id);
                }
                _outputBoxes.Remove(task.Id);
                _activeTasks.Remove(task);
            }
            RefreshFilterCombos();
            UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowDarkConfirm(
                $"Are you sure you want to clear all {_historyTasks.Count} history entries? This cannot be undone.",
                "Clear History")) return;

            foreach (var task in _historyTasks.ToList())
            {
                if (_tabs.TryGetValue(task.Id, out var tab))
                {
                    OutputTabs.Items.Remove(tab);
                    _tabs.Remove(task.Id);
                }
                _outputBoxes.Remove(task.Id);
            }
            _historyTasks.Clear();
            SaveHistory();
            RefreshFilterCombos();
            UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            // If tab already exists, just switch to it
            if (_tabs.TryGetValue(task.Id, out var existingTab))
            {
                OutputTabs.SelectedItem = existingTab;
                return;
            }

            // Create a tab with the history context, ready for follow-up input
            CreateTab(task);
            AppendOutput(task.Id, $"[UnityAgent] Resumed session\n");
            AppendOutput(task.Id, $"[UnityAgent] Original task: {task.Description}\n");
            AppendOutput(task.Id, $"[UnityAgent] Project: {task.ProjectPath}\n");
            AppendOutput(task.Id, $"[UnityAgent] Status: {task.StatusText}\n");
            AppendOutput(task.Id, $"\n[UnityAgent] Type a follow-up message below. It will be sent with --continue.\n");

            // Move back to active so follow-up input works
            _historyTasks.Remove(task);
            _activeTasks.Add(task);
            UpdateStatus();
        }


        private void MoveToHistory(AgentTask task)
        {
            ReleaseTaskLocks(task.Id);
            _activeTasks.Remove(task);
            _historyTasks.Insert(0, task);

            if (_tabs.TryGetValue(task.Id, out var tab))
            {
                OutputTabs.Items.Remove(tab);
                _tabs.Remove(task.Id);
            }
            _outputBoxes.Remove(task.Id);
            UpdateOutputTabWidths();

            SaveHistory();
            RefreshFilterCombos();
            UpdateStatus();
            CheckQueuedTasks();
        }

        private static void KillProcess(AgentTask task)
        {
            try
            {
                if (task.Process is { HasExited: false })
                    task.Process.Kill(true);
            }
            catch { }
        }

        // ── Selection Sync ─────────────────────────────────────────

        private void ActiveTask_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (ActiveTasksList.SelectedItem is AgentTask task && _tabs.TryGetValue(task.Id, out var tab))
            {
    
                OutputTabs.SelectedItem = tab;
            }
        }

        private void HistoryTask_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryTasksList.SelectedItem is AgentTask task && _tabs.TryGetValue(task.Id, out var tab))
            {
    
                OutputTabs.SelectedItem = tab;
            }
        }

        // ── Window Close ───────────────────────────────────────────

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_activeTasks.Count > 0)
            {
                if (!ShowDarkConfirm(
                    $"There are {_activeTasks.Count} active task(s) still running.\n\n" +
                    "Closing will terminate all of them. Continue?",
                    "Active Tasks Running"))
                {
                    e.Cancel = true;
                    return;
                }
            }

            // Persist projects and settings before exit
            SaveProjects();
            SaveSettings();
            SaveHistory();

            foreach (var task in _activeTasks)
                KillProcess(task);

            _fileLocks.Clear();
            _fileLocksView.Clear();
            _taskLockedFiles.Clear();
            _queuedTaskInfo.Clear();
            _streamingToolState.Clear();

            _terminalManager?.DisposeAll();
        }

        // ── History Persistence ────────────────────────────────────

        private void SaveHistory()
        {
            try
            {
                var dir = Path.GetDirectoryName(_historyFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var entries = _historyTasks.Select(t => new TaskHistoryEntry
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

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyFile)) return;
                var entries = JsonSerializer.Deserialize<List<TaskHistoryEntry>>(
                    File.ReadAllText(_historyFile));
                if (entries == null) return;

                var cutoff = DateTime.Now.AddHours(-_historyRetentionHours);
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

                    _historyTasks.Add(task);
                }
            }
            catch { }
        }

        private void CleanupOldHistory()
        {
            var cutoff = DateTime.Now.AddHours(-_historyRetentionHours);
            var stale = _historyTasks.Where(t => t.StartTime < cutoff).ToList();
            foreach (var task in stale)
            {
                _historyTasks.Remove(task);
                if (_tabs.TryGetValue(task.Id, out var tab))
                {
                    OutputTabs.Items.Remove(tab);
                    _tabs.Remove(task.Id);
                }
                _outputBoxes.Remove(task.Id);
            }
            if (stale.Count > 0)
            {
                SaveHistory();
                UpdateOutputTabWidths();
            }
        }

        // ── Terminal ───────────────────────────────────────────────

        private void TerminalSend_Click(object sender, RoutedEventArgs e)
        {
            _terminalManager?.SendCommand();
        }

        private void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            _terminalManager?.HandleKeyDown(e);
        }

        private void TerminalInterrupt_Click(object sender, RoutedEventArgs e)
        {
            _terminalManager?.SendInterrupt();
        }

        // ── Projects Tab ──────────────────────────────────────────

        private void RefreshProjectList()
        {
            if (ProjectListPanel == null) return;
            ProjectListPanel.Children.Clear();

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

                // Left: project info
                var infoPanel = new StackPanel();

                // Project name + MCP status dot
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                var nameBlock = new TextBlock
                {
                    Text = proj.DisplayName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
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
                        RefreshProjectList();
                    }

                    editBox.KeyDown += (_, ke) =>
                    {
                        if (ke.Key == Key.Enter) CommitRename();
                        else if (ke.Key == Key.Escape) RefreshProjectList();
                    };

                    // Defer LostFocus registration so it doesn't fire during the
                    // same input event chain that created the TextBox.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        editBox.LostFocus += (_, _) => CommitRename();
                    }), System.Windows.Threading.DispatcherPriority.Input);

                    editBox.SelectAll();
                    editBox.Focus();
                }
                nameBlock.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
                editIcon.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
                nameRow.Children.Add(nameBlock);
                nameRow.Children.Add(editIcon);

                // MCP status dot
                var mcpColor = proj.McpStatus switch
                {
                    McpStatus.Disabled => Color.FromRgb(0x66, 0x66, 0x66),
                    McpStatus.Initialized => Color.FromRgb(0xE0, 0xA0, 0x30),
                    McpStatus.Investigating => Color.FromRgb(0xE0, 0x80, 0x30),
                    McpStatus.Enabled => Color.FromRgb(0x4C, 0xAF, 0x50),
                    _ => Color.FromRgb(0x66, 0x66, 0x66)
                };
                nameRow.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(mcpColor),
                    Margin = new Thickness(8, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                nameRow.Children.Add(new TextBlock
                {
                    Text = proj.McpStatus.ToString(),
                    Foreground = new SolidColorBrush(mcpColor),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                });
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

                // Short description or initializing indicator
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

                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

                // Right: X button (top) + MCP button
                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, 0, 0, 0)
                };

                // X close button
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
                    if (s is Button b && b.Tag is string path)
                        RemoveProject_Click(path);
                };
                btnPanel.Children.Add(closeBtn);

                if (proj.McpStatus == McpStatus.Investigating)
                {
                    var cancelMcpBtn = new Button
                    {
                        Content = "Cancel MCP",
                        Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        Padding = new Thickness(10, 4, 10, 4),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 4, 0, 0),
                        Tag = proj.Path
                    };
                    cancelMcpBtn.Click += (s, ev) => { ev.Handled = true; CancelMcp_Click(s, ev); };
                    btnPanel.Children.Add(cancelMcpBtn);
                }
                else
                {
                    var connectBtn = new Button
                    {
                        Content = proj.McpStatus == McpStatus.Enabled ? "MCP OK" : "Connect MCP",
                        Background = new SolidColorBrush(proj.McpStatus == McpStatus.Enabled
                            ? Color.FromRgb(0x3A, 0x7D, 0x3A)
                            : Color.FromRgb(0x8B, 0x4A, 0x35)),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        Padding = new Thickness(10, 4, 10, 4),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 4, 0, 0),
                        Tag = proj.Path
                    };
                    connectBtn.Click += (s, ev) => { ev.Handled = true; ConnectMcp_Click(s, ev); };
                    btnPanel.Children.Add(connectBtn);
                }

                Grid.SetColumn(btnPanel, 1);
                grid.Children.Add(btnPanel);

                card.Child = grid;

                // Click card to select as active project
                var projPath = proj.Path;
                card.MouseLeftButtonUp += (_, e) =>
                {
                    // Don't switch project when a rename TextBox is active
                    if (e.OriginalSource is TextBox) return;
                    if (projPath == _projectPath) return;
                    if (!ShowDarkConfirm("Are you sure you want to change project?", "Change Project"))
                        return;
                    _projectPath = projPath;
                    _terminalManager?.UpdateWorkingDirectory(projPath);
                    SaveSettings();
                    SyncSettingsForProject();
                };

                ProjectListPanel.Children.Add(card);
            }
        }

        // ── MCP Connection ───────────────────────────────────────────

        private async void ConnectMcp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string projectPath) return;

            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            btn.IsEnabled = false;
            btn.Content = "Checking...";

            var mcpUrl = "http://127.0.0.1:8080/mcp";
            var mcpJsonPath = System.IO.Path.Combine(projectPath, ".mcp.json");
            var mcpJsonContent = "{\n  \"mcpServers\": {\n    \"mcp-for-unity-server\": {\n      \"type\": \"http\",\n      \"url\": \"" + mcpUrl + "\"\n    }\n  }\n}";

            // Step 1: Health check
            var reachable = await CheckMcpHealth(mcpUrl);

            if (reachable)
            {
                // Write .mcp.json if not present
                if (!File.Exists(mcpJsonPath))
                    File.WriteAllText(mcpJsonPath, mcpJsonContent);

                entry.McpStatus = McpStatus.Enabled;
                SaveProjects();
                RefreshProjectList();
                UpdateMcpToggleForProject();
                return;
            }

            // Not reachable — write .mcp.json if missing
            if (!File.Exists(mcpJsonPath))
            {
                File.WriteAllText(mcpJsonPath, mcpJsonContent);
                entry.McpStatus = McpStatus.Initialized;
                SaveProjects();
                RefreshProjectList();

                // Retry health check
                reachable = await CheckMcpHealth(mcpUrl);
                if (reachable)
                {
                    entry.McpStatus = McpStatus.Enabled;
                    SaveProjects();
                    RefreshProjectList();
                    UpdateMcpToggleForProject();
                    return;
                }
            }

            // Still not reachable — launch investigation agent
            entry.McpStatus = McpStatus.Investigating;
            SaveProjects();
            RefreshProjectList();

            var investigateTask = new AgentTask
            {
                Description = "The MCP server mcp-for-unity-server at http://127.0.0.1:8080/mcp is not responding. " +
                    "Diagnose and fix the connection. Check if the Unity Editor is running with the MCP plugin installed and enabled.",
                SkipPermissions = SkipPermissionsToggle.IsChecked == true,
                ProjectPath = projectPath
            };
            _activeTasks.Add(investigateTask);
            CreateTab(investigateTask);
            StartProcess(investigateTask);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void CancelMcp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string projectPath) return;

            var entry = _savedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry == null) return;

            // Find and cancel the investigation task for this project
            var investigateTask = _activeTasks.FirstOrDefault(t =>
                t.ProjectPath == projectPath &&
                t.Description.Contains("MCP server") &&
                t.Description.Contains("not responding"));

            if (investigateTask != null)
            {
                KillProcess(investigateTask);
                investigateTask.Status = AgentTaskStatus.Cancelled;
                investigateTask.EndTime = DateTime.Now;
                AppendOutput(investigateTask.Id, "\n[UnityAgent] MCP investigation cancelled.\n");
                UpdateTabHeader(investigateTask);
                MoveToHistory(investigateTask);
            }

            entry.McpStatus = McpStatus.Disabled;
            SaveProjects();
            RefreshProjectList();
            UpdateMcpToggleForProject();
        }

        private async System.Threading.Tasks.Task<bool> CheckMcpHealth(string url)
        {
            try
            {
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ── Filters ──────────────────────────────────────────────────

        private void RefreshFilterCombos()
        {
            if (ActiveFilterCombo == null || HistoryFilterCombo == null) return;

            // Collect unique project paths from all sources
            var allPaths = new HashSet<string>();
            foreach (var p in _savedProjects)
                allPaths.Add(p.Path);
            foreach (var t in _activeTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);
            foreach (var t in _historyTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);

            var projectNames = allPaths
                .Select(p => new { Path = p, Name = System.IO.Path.GetFileName(p) })
                .OrderBy(x => x.Name)
                .ToList();

            // Save current selections
            var activeSelection = ActiveFilterCombo.SelectedItem as ComboBoxItem;
            var activeTag = activeSelection?.Tag as string;
            var historySelection = HistoryFilterCombo.SelectedItem as ComboBoxItem;
            var historyTag = historySelection?.Tag as string;

            // Rebuild active filter
            ActiveFilterCombo.SelectionChanged -= ActiveFilter_Changed;
            ActiveFilterCombo.Items.Clear();
            ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            // Restore selection
            var found = false;
            if (!string.IsNullOrEmpty(activeTag))
            {
                foreach (ComboBoxItem item in ActiveFilterCombo.Items)
                {
                    if (item.Tag as string == activeTag)
                    {
                        ActiveFilterCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
            }
            if (!found) ActiveFilterCombo.SelectedIndex = 0;
            ActiveFilterCombo.SelectionChanged += ActiveFilter_Changed;

            // Rebuild history filter
            HistoryFilterCombo.SelectionChanged -= HistoryFilter_Changed;
            HistoryFilterCombo.Items.Clear();
            HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            // Restore selection
            found = false;
            if (!string.IsNullOrEmpty(historyTag))
            {
                foreach (ComboBoxItem item in HistoryFilterCombo.Items)
                {
                    if (item.Tag as string == historyTag)
                    {
                        HistoryFilterCombo.SelectedItem = item;
                        found = true;
                        break;
                    }
                }
            }
            if (!found) HistoryFilterCombo.SelectedIndex = 0;
            HistoryFilterCombo.SelectionChanged += HistoryFilter_Changed;
        }

        private void ActiveFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_activeView == null) return;
            var tag = (ActiveFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(tag))
                _activeView.Filter = null;
            else
                _activeView.Filter = obj => obj is AgentTask t && t.ProjectPath == tag;
        }

        private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_historyView == null) return;
            var tag = (HistoryFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrEmpty(tag))
                _historyView.Filter = null;
            else
                _historyView.Filter = obj => obj is AgentTask t && t.ProjectPath == tag;
        }

        // ── File Locking ──────────────────────────────────────────

        private static string NormalizePath(string path, string? basePath = null)
            => TaskLauncher.NormalizePath(path, basePath);

        private static bool IsFileModifyTool(string? toolName)
            => TaskLauncher.IsFileModifyTool(toolName);

        private static string? TryExtractFilePathFromPartial(string partialJson)
        {
            var match = Regex.Match(partialJson, @"""file_path""\s*:\s*""([^""]+)""");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static string? ExtractFilePath(JsonElement input)
        {
            if (input.TryGetProperty("file_path", out var fp))
                return fp.GetString();
            if (input.TryGetProperty("path", out var p))
                return p.GetString();
            return null;
        }

        private bool TryAcquireOrConflict(string taskId, string filePath, string toolName)
        {
            // If the task has IgnoreFileLocks, always succeed (still acquire the lock if possible)
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task?.IgnoreFileLocks == true)
            {
                TryAcquireFileLock(taskId, filePath, toolName, isIgnored: true); // best-effort, ignore result
                return true;
            }
            if (!TryAcquireFileLock(taskId, filePath, toolName))
            {
                HandleFileLockConflict(taskId, filePath, toolName);
                return false;
            }
            return true;
        }

        private bool TryAcquireFileLock(string taskId, string filePath, string toolName, bool isIgnored = false)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            var basePath = task?.ProjectPath;
            var normalized = NormalizePath(filePath, basePath);

            if (_fileLocks.TryGetValue(normalized, out var existing))
            {
                if (existing.OwnerTaskId == taskId)
                {
                    existing.ToolName = toolName;
                    existing.AcquiredAt = DateTime.Now;
                    existing.IsIgnored = isIgnored;
                    existing.OnPropertyChanged(nameof(FileLock.ToolName));
                    existing.OnPropertyChanged(nameof(FileLock.TimeText));
                    existing.OnPropertyChanged(nameof(FileLock.StatusText));
                    return true;
                }
                return false; // locked by another task
            }

            var fileLock = new FileLock
            {
                NormalizedPath = normalized,
                OriginalPath = filePath,
                OwnerTaskId = taskId,
                ToolName = toolName,
                AcquiredAt = DateTime.Now,
                IsIgnored = isIgnored
            };
            _fileLocks[normalized] = fileLock;
            _fileLocksView.Add(fileLock);

            if (!_taskLockedFiles.TryGetValue(taskId, out var files))
            {
                files = new HashSet<string>();
                _taskLockedFiles[taskId] = files;
            }
            files.Add(normalized);

            UpdateFileLockBadge();
            return true;
        }

        private void ReleaseTaskLocks(string taskId)
        {
            if (!_taskLockedFiles.TryGetValue(taskId, out var files)) return;

            foreach (var path in files)
            {
                if (_fileLocks.TryGetValue(path, out var fl))
                {
                    _fileLocks.Remove(path);
                    _fileLocksView.Remove(fl);
                }
            }
            _taskLockedFiles.Remove(taskId);
            UpdateFileLockBadge();
        }

        private void HandleFileLockConflict(string taskId, string filePath, string toolName)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;

            var normalized = NormalizePath(filePath, task.ProjectPath);
            var blockingLock = _fileLocks.GetValueOrDefault(normalized);
            var blockingTaskId = blockingLock?.OwnerTaskId ?? "unknown";

            AppendOutput(taskId, $"\n[UnityAgent] FILE LOCK CONFLICT: {Path.GetFileName(filePath)} is locked by task #{blockingTaskId} ({toolName})\n");
            AppendOutput(taskId, $"[UnityAgent] Killing task #{taskId} and queuing for auto-resume...\n");

            // Kill the process
            KillProcess(task);

            // Release any locks this task held
            ReleaseTaskLocks(taskId);

            // Set status to Queued
            task.Status = AgentTaskStatus.Queued;
            task.QueuedReason = $"File locked: {Path.GetFileName(filePath)} by #{blockingTaskId}";
            task.BlockedByTaskId = blockingTaskId;
            UpdateTabHeader(task);

            // Store queue info
            var blockedByIds = new HashSet<string> { blockingTaskId };
            _queuedTaskInfo[taskId] = new QueuedTaskInfo
            {
                Task = task,
                ConflictingFilePath = normalized,
                BlockingTaskId = blockingTaskId,
                BlockedByTaskIds = blockedByIds
            };

            // Clean up streaming state
            _streamingToolState.Remove(taskId);

            UpdateStatus();
        }

        private void CheckQueuedTasks()
        {
            var toResume = new List<string>();

            foreach (var kvp in _queuedTaskInfo)
            {
                var qi = kvp.Value;
                // Check if all blocking tasks are no longer running
                var allClear = true;
                foreach (var blockerId in qi.BlockedByTaskIds)
                {
                    var blocker = _activeTasks.FirstOrDefault(t => t.Id == blockerId);
                    if (blocker != null && blocker.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing)
                    {
                        allClear = false;
                        break;
                    }
                }

                // Also check if conflicting file is no longer locked
                if (allClear && _fileLocks.ContainsKey(qi.ConflictingFilePath))
                    allClear = false;

                if (allClear)
                    toResume.Add(kvp.Key);
            }

            foreach (var taskId in toResume)
                ResumeQueuedTask(taskId);
        }

        private void ResumeQueuedTask(string taskId)
        {
            if (!_queuedTaskInfo.TryGetValue(taskId, out var qi)) return;
            _queuedTaskInfo.Remove(taskId);

            var task = qi.Task;
            task.Status = AgentTaskStatus.Running;
            task.QueuedReason = null;
            task.BlockedByTaskId = null;
            task.StartTime = DateTime.Now;

            AppendOutput(taskId, $"\n[UnityAgent] Resuming task #{taskId} (blocking task finished)...\n\n");
            UpdateTabHeader(task);
            StartProcess(task);
            UpdateStatus();
        }

        private void ForceStartQueuedTask(AgentTask task)
        {
            _queuedTaskInfo.Remove(task.Id);
            task.Status = AgentTaskStatus.Running;
            task.QueuedReason = null;
            task.BlockedByTaskId = null;
            task.StartTime = DateTime.Now;

            AppendOutput(task.Id, $"\n[UnityAgent] Force-starting queued task #{task.Id}...\n\n");
            UpdateTabHeader(task);
            StartProcess(task);
            UpdateStatus();
        }

        private void UpdateFileLockBadge()
        {
            if (FileLockBadge != null)
            {
                var count = _fileLocks.Count;
                FileLockBadge.Text = count.ToString();
                FileLockBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ── Helpers ────────────────────────────────────────────────

        private void UpdateStatus()
        {
            var running = _activeTasks.Count(t => t.Status == AgentTaskStatus.Running);
            var ongoing = _activeTasks.Count(t => t.Status == AgentTaskStatus.Ongoing);
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLocks.Count;
            var projectName = Path.GetFileName(_projectPath);
            StatusText.Text = $"{projectName}  |  Running: {running}  |  Ongoing: {ongoing}  |  Queued: {queued}  |  " +
                              $"Completed: {completed}  |  Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                              $"Locks: {locks}  |  {_projectPath}";
        }

        private static string StripAnsi(string text) => TaskLauncher.StripAnsi(text);

        private class TaskHistoryEntry
        {
            public string Description { get; set; } = "";
            public string Status { get; set; } = "";
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public bool SkipPermissions { get; set; }
            public bool RemoteSession { get; set; }
            public string? ProjectPath { get; set; }
            public bool IsOvernight { get; set; }
            public int MaxIterations { get; set; }
            public int CurrentIteration { get; set; }
            public string CompletionSummary { get; set; } = "";
        }

        private class StreamingToolState
        {
            public string? CurrentToolName { get; set; }
            public StringBuilder JsonAccumulator { get; set; } = new();
            public bool IsFileModifyTool { get; set; }
            public bool FilePathChecked { get; set; }
        }

        private class QueuedTaskInfo
        {
            public AgentTask Task { get; set; } = null!;
            public string ConflictingFilePath { get; set; } = "";
            public string BlockingTaskId { get; set; } = "";
            public HashSet<string> BlockedByTaskIds { get; set; } = new();
        }
    }
}
