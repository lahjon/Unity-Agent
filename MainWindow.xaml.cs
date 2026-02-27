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

        public string FolderName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
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

        // ── System Prompt Persistence ────────────────────────────────

        private void LoadSystemPrompt()
        {
            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UnityAgent");
                var file = Path.Combine(appDataDir, "system_prompt.txt");
                if (File.Exists(file))
                {
                    SystemPrompt = File.ReadAllText(file);
                    SystemPromptBox.Text = SystemPrompt;
                }
            }
            catch { }
        }

        private void SaveSystemPrompt()
        {
            try
            {
                var appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UnityAgent");
                Directory.CreateDirectory(appDataDir);
                var file = Path.Combine(appDataDir, "system_prompt.txt");
                File.WriteAllText(file, SystemPrompt);
            }
            catch { }
        }

        private void SystemPromptBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (SystemPromptBox == null) return;
            SystemPrompt = SystemPromptBox.Text;
            SaveSystemPrompt();
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
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var dict = new Dictionary<string, object>
                {
                    ["historyRetentionHours"] = _historyRetentionHours
                };
                File.WriteAllText(_settingsFile,
                    JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private const string DefaultSystemPrompt =
            "# STRICT RULE " +
            "Never access, read, write, or reference any files outside the project root directory. " +
            "All operations must stay within ./ " +
            "# MCP VERIFICATION " +
            "Before starting the task, verify the MCP server is running and responding. " +
            "The MCP server is mcp-for-unity-server on http://127.0.0.1:8080/mcp. " +
            "Confirm it is reachable before proceeding. " +
            "# MCP USAGE " +
            "Use the MCP server when the task requires interacting with Unity directly. " +
            "This includes modifying prefabs, accessing scenes, taking screenshots, " +
            "inspecting GameObjects, and any other Unity Editor operations that cannot be " +
            "done through file edits alone. " +
            "# TASK: ";

        private const string OvernightInitialTemplate =
            "You are running as an OVERNIGHT AUTONOMOUS TASK. This means you will be called repeatedly " +
            "to iterate on the work until it is complete. Follow these instructions carefully:\n\n" +
            "## CRITICAL RESTRICTIONS\n" +
            "These rules are ABSOLUTE and must NEVER be violated:\n" +
            "- **NO GIT COMMANDS.** Do not run git init, git add, git commit, git push, git checkout, " +
            "git branch, git merge, git rebase, git stash, git reset, git clone, or ANY other git command. " +
            "Git operations are forbidden in overnight mode.\n" +
            "- **NO OS-LEVEL MODIFICATIONS.** Do not modify system files, registry, environment variables, " +
            "PATH, system services, scheduled tasks, firewall rules, or anything outside the project directory. " +
            "Do not install, uninstall, or update system-wide packages, tools, or software.\n" +
            "- **STAY INSIDE THE PROJECT.** All file reads, writes, and edits must be within the project root. " +
            "Never access, create, or modify files outside of ./ under any circumstances.\n" +
            "- **NO DESTRUCTIVE OPERATIONS.** Do not delete directories recursively, reformat drives, " +
            "kill system processes, or perform any action that cannot be easily undone.\n\n" +
            "## Step 1: Create .overnight_log.md\n" +
            "Create a file called `.overnight_log.md` in the project root with:\n" +
            "- A **Checklist** of all sub-tasks needed to complete the work\n" +
            "- **Exit Criteria** that define when the task is truly done\n" +
            "- A **Progress Log** section where you append a dated entry each iteration\n\n" +
            "## Step 2: Implement\n" +
            "Work through the checklist step by step. Do as much as you can this iteration.\n\n" +
            "## Step 3: Investigate for flaws\n" +
            "After implementing, review your work critically. Look for bugs, edge cases, " +
            "missing error handling, incomplete features, and anything that doesn't match the requirements.\n\n" +
            "## Step 4: Verify checklist\n" +
            "Go through each checklist item and verify it is truly complete. " +
            "Update `.overnight_log.md` with your findings.\n\n" +
            "## Step 5: Status\n" +
            "End your response with EXACTLY one of these markers on its own line:\n" +
            "STATUS: COMPLETE\n" +
            "STATUS: NEEDS_MORE_WORK\n\n" +
            "Use COMPLETE only when ALL checklist items are done AND all exit criteria are met.\n\n" +
            "## THE TASK:\n";

        private const string OvernightContinuationTemplate =
            "You are continuing an OVERNIGHT AUTONOMOUS TASK (iteration {0}/{1}).\n\n" +
            "## CRITICAL RESTRICTIONS (REMINDER)\n" +
            "- **NO GIT COMMANDS** — git is completely forbidden in overnight mode.\n" +
            "- **NO OS-LEVEL MODIFICATIONS** — do not touch system files, registry, environment variables, " +
            "PATH, services, or install/uninstall any system-wide software.\n" +
            "- **STAY INSIDE THE PROJECT** — all operations must remain within the project root directory.\n" +
            "- **NO DESTRUCTIVE OPERATIONS** — no recursive deletes, no killing processes, nothing irreversible.\n\n" +
            "## Step 1: Read context\n" +
            "Read `.overnight_log.md` to understand what has been done and what remains.\n\n" +
            "## Step 2: Investigate\n" +
            "Review the current implementation for flaws, bugs, incomplete work, " +
            "and anything that doesn't match the original requirements.\n\n" +
            "## Step 3: Fix and improve\n" +
            "Address the issues you found. Continue working through unchecked items.\n\n" +
            "## Step 4: Verify all checklist items\n" +
            "Go through every checklist item and exit criterion. " +
            "Update `.overnight_log.md` with your progress.\n\n" +
            "## Step 5: Status\n" +
            "End your response with EXACTLY one of these markers on its own line:\n" +
            "STATUS: COMPLETE\n" +
            "STATUS: NEEDS_MORE_WORK\n\n" +
            "Use COMPLETE only when ALL checklist items are done AND all exit criteria are met.\n\n" +
            "Continue working on the task now.";

        private string SystemPrompt;

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private readonly Dictionary<string, TabItem> _tabs = new();
        private readonly Dictionary<string, TextBox> _outputBoxes = new();
        private readonly DispatcherTimer _statusTimer;
        private string _projectPath;
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
        private Process? _terminalProcess;
        private readonly List<string> _terminalHistory = new();
        private int _terminalHistoryIndex = -1;
        private string _terminalCwd = "";
        private const string CwdMarker = "__CWDM__";

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
            _projectPath = _savedProjects.Count > 0 ? _savedProjects[0].Path : Directory.GetCurrentDirectory();

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
            StartTerminal();
        }

        // ── Project Management ───────────────────────────────────────

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
                        _savedProjects = JsonSerializer.Deserialize<List<ProjectEntry>>(json) ?? new();
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
        }

        private void SaveProjects()
        {
            try
            {
                File.WriteAllText(_projectsFile,
                    JsonSerializer.Serialize(_savedProjects, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    }));
            }
            catch { }
        }

        private void RefreshProjectCombo()
        {
            ProjectCombo.SelectionChanged -= ProjectCombo_Changed;
            ProjectCombo.Items.Clear();
            foreach (var p in _savedProjects)
            {
                ProjectCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{p.DisplayName}  \u2014  {p.Path}",
                    Tag = p.Path
                });
            }

            var idx = _savedProjects.FindIndex(p => p.Path == _projectPath);
            if (idx >= 0)
                ProjectCombo.SelectedIndex = idx;
            else if (_savedProjects.Count > 0)
                ProjectCombo.SelectedIndex = 0;

            ProjectCombo.SelectionChanged += ProjectCombo_Changed;
        }

        private void BrowseProjectPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a project folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                AddProjectPath.Text = dialog.SelectedPath;
        }

        private void AddProject_Click(object sender, RoutedEventArgs e)
        {
            var name = AddProjectName.Text.Trim();
            var path = AddProjectPath.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a project name.", "Missing Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show("Please enter a project path.", "Missing Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(path))
            {
                MessageBox.Show("The specified path does not exist.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_savedProjects.Any(p => p.Path == path))
            {
                MessageBox.Show("This project path is already added.", "Duplicate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _savedProjects.Add(new ProjectEntry { Name = name, Path = path });
            SaveProjects();
            _projectPath = path;
            RefreshProjectCombo();
            RefreshProjectList();
            RefreshFilterCombos();
            UpdateStatus();

            AddProjectName.Text = "";
            AddProjectPath.Text = "";
        }

        private void RemoveProject_Click(string projectPath)
        {
            if (!ShowDarkConfirm($"Remove this project from the list?\n\n{projectPath}", "Remove Project"))
                return;

            _savedProjects.RemoveAll(p => p.Path == projectPath);
            SaveProjects();
            _projectPath = _savedProjects.Count > 0 ? _savedProjects[0].Path : Directory.GetCurrentDirectory();
            RefreshProjectCombo();
            RefreshProjectList();
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void ProjectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectCombo.SelectedItem is ComboBoxItem item && item.Tag is string selected)
            {
                _projectPath = selected;
                RefreshProjectList();
                UpdateStatus();
            }
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

        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        }

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
            if (string.IsNullOrEmpty(desc)) return;

            var task = new AgentTask
            {
                Description = desc,
                SkipPermissions = SkipPermissionsToggle.IsChecked == true,
                RemoteSession = RemoteSessionToggle.IsChecked == true,
                Headless = HeadlessToggle.IsChecked == true,
                IsOvernight = OvernightToggle.IsChecked == true,
                IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true,
                MaxIterations = 50,
                ProjectPath = _projectPath
            };
            task.ImagePaths.AddRange(_attachedImages);
            _attachedImages.Clear();
            UpdateImageIndicator();

            if (task.Headless)
            {
                LaunchHeadless(task);
                TaskInput.Clear();
                return;
            }

            _activeTasks.Add(task);
            CreateTab(task);
            StartProcess(task);
            TaskInput.Clear();
            RefreshFilterCombos();
            UpdateStatus();
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

            var inputPanel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(sendBtn, Dock.Right);
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

        }

        private StackPanel CreateTabHeader(AgentTask task)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var idLabel = new TextBlock
            {
                Text = $"#{task.Id}",
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Margin = new Thickness(0, 0, 5, 0)
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
                Content = "\u00D7",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(4, 0, 2, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (_, _) => CloseTab(task);

            panel.Children.Add(dot);
            panel.Children.Add(idLabel);
            panel.Children.Add(label);
            panel.Children.Add(closeBtn);
            return panel;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status == AgentTaskStatus.Running || task.Status == AgentTaskStatus.Queued)
            {
                if (!ShowDarkConfirm("This task is still running. Closing will terminate it.\n\nAre you sure?", "Task Running"))
                    return;

                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                KillProcess(task);
                ReleaseTaskLocks(task.Id);
                _queuedTaskInfo.Remove(task.Id);
                _streamingToolState.Remove(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                SaveHistory();
                CheckQueuedTasks();
            }

            if (_tabs.TryGetValue(task.Id, out var tab))
            {
                OutputTabs.Items.Remove(tab);
                _tabs.Remove(task.Id);
            }
            _outputBoxes.Remove(task.Id);
            UpdateStatus();
        }

        private void UpdateTabHeader(AgentTask task)
        {
            if (!_tabs.TryGetValue(task.Id, out var tab)) return;
            if (tab.Header is StackPanel sp && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
            {
                var color = task.Status switch
                {
                    AgentTaskStatus.Running => Color.FromRgb(0x4C, 0xAF, 0x50),
                    AgentTaskStatus.Completed => Color.FromRgb(0x2E, 0x7D, 0x32),
                    AgentTaskStatus.Cancelled => Color.FromRgb(0xE0, 0xA0, 0x30),
                    AgentTaskStatus.Failed => Color.FromRgb(0xE0, 0x55, 0x55),
                    AgentTaskStatus.Queued => Color.FromRgb(0xCC, 0x88, 0x00),
                    _ => Color.FromRgb(0x55, 0x55, 0x55)
                };
                dot.Fill = new SolidColorBrush(color);
            }
        }

        // ── Process Management ─────────────────────────────────────

        private void LaunchHeadless(AgentTask task)
        {
            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var fullPrompt = BuildPromptWithImages(SystemPrompt + task.Description, task.ImagePaths);
            var projectPath = task.ProjectPath;

            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            // Write a script that the terminal will run
            var ps1File = Path.Combine(_scriptDir, $"headless_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{projectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{promptFile}'\n" +
                $"claude -p{skipFlag}{remoteFlag} $prompt\n" +
                "Write-Host \"`n[UnityAgent] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
                "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')\n",
                Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoExit -File \"{ps1File}\"",
                UseShellExecute = true,
                WorkingDirectory = projectPath
            };
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
        {
            if (imagePaths.Count == 0)
                return basePrompt;

            var sb = new StringBuilder(basePrompt);
            sb.Append("\n\n# ATTACHED IMAGES\n");
            sb.Append("The user has attached the following image(s). ");
            sb.Append("Use the Read tool to view each image file before proceeding with the task.\n");
            foreach (var img in imagePaths)
                sb.Append($"- {img}\n");
            return sb.ToString();
        }

        private void StartProcess(AgentTask task)
        {
            // Overnight mode forces skip-permissions — nobody is there to approve prompts
            if (task.IsOvernight)
                task.SkipPermissions = true;

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var basePrompt = SystemPrompt + task.Description;
            if (task.IsOvernight)
            {
                task.CurrentIteration = 1;
                task.ConsecutiveFailures = 0;
                task.LastIterationOutputStart = 0;
                basePrompt = OvernightInitialTemplate + task.Description;
            }
            var fullPrompt = BuildPromptWithImages(basePrompt, task.ImagePaths);
            var projectPath = task.ProjectPath;

            // Write prompt to a temp file to avoid all escaping headaches
            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            // Build the claude command with stream-json for real-time output
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var claudeCmd = $"claude -p{skipFlag}{remoteFlag} --verbose --output-format stream-json $prompt";

            // PowerShell script reads prompt from file, passes to claude -p
            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{projectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{promptFile}'\n" +
                $"{claudeCmd}\n",
                Encoding.UTF8);

            AppendOutput(task.Id, $"[UnityAgent] Task #{task.Id} starting...\n");
            AppendOutput(task.Id, $"[UnityAgent] Project: {projectPath}\n");
            AppendOutput(task.Id, $"[UnityAgent] Skip permissions: {task.SkipPermissions}\n");
            AppendOutput(task.Id, $"[UnityAgent] Remote session: {task.RemoteSession}\n");
            if (task.IsOvernight)
            {
                AppendOutput(task.Id, $"[UnityAgent] Overnight mode: ON (max {task.MaxIterations} iterations, 12h cap)\n");
                AppendOutput(task.Id, $"[UnityAgent] Safety: skip-permissions forced, git blocked, 30min iteration timeout\n");
            }
            AppendOutput(task.Id, $"[UnityAgent] Connecting to Claude...\n\n");

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
            if (task.Status == AgentTaskStatus.Running && task.Process is { HasExited: false })
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
                    AppendOutput(task.Id, "\n[UnityAgent] Follow-up complete.\n"));
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
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private static bool IsTokenLimitError(string output)
        {
            // Check the last portion of output for common token/rate limit indicators
            var tail = output.Length > 3000 ? output[^3000..] : output;
            var lower = tail.ToLowerInvariant();
            return lower.Contains("rate limit") ||
                   lower.Contains("token limit") ||
                   lower.Contains("overloaded") ||
                   lower.Contains("529") ||
                   lower.Contains("capacity") ||
                   lower.Contains("too many requests");
        }

        private static bool CheckOvernightComplete(string output)
        {
            // Scan last ~50 lines for STATUS markers
            var lines = output.Split('\n');
            var start = Math.Max(0, lines.Length - 50);
            for (var i = lines.Length - 1; i >= start; i--)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == "STATUS: COMPLETE") return true;
                // If we find NEEDS_MORE_WORK, that's explicit — not complete
                if (trimmed == "STATUS: NEEDS_MORE_WORK") return false;
            }
            return false;
        }

        private void StartOvernightContinuation(AgentTask task)
        {
            if (task.Status != AgentTaskStatus.Running) return;

            // Mark where this iteration's output starts (for scoped error detection)
            task.LastIterationOutputStart = task.OutputBuilder.Length;

            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = task.RemoteSession ? " --remote" : "";
            var continuationPrompt = string.Format(OvernightContinuationTemplate, task.CurrentIteration, task.MaxIterations);

            var promptFile = Path.Combine(_scriptDir, $"overnight_{task.Id}_{task.CurrentIteration}.txt");
            File.WriteAllText(promptFile, continuationPrompt, Encoding.UTF8);

            var ps1File = Path.Combine(_scriptDir, $"overnight_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                "$env:CLAUDECODE = $null\n" +
                $"Set-Location -LiteralPath '{task.ProjectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{promptFile}'\n" +
                $"claude -p{skipFlag}{remoteFlag} --verbose --continue --output-format stream-json $prompt\n",
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
            if (task.Status == AgentTaskStatus.Running)
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
            UpdateStatus();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
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

            foreach (var task in _activeTasks)
                KillProcess(task);

            _fileLocks.Clear();
            _fileLocksView.Clear();
            _taskLockedFiles.Clear();
            _queuedTaskInfo.Clear();
            _streamingToolState.Clear();

            try { _terminalProcess?.Kill(true); } catch { }
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
                    CurrentIteration = t.CurrentIteration
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
                        CurrentIteration = entry.CurrentIteration
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
            if (stale.Count > 0) SaveHistory();
        }

        // ── Terminal ───────────────────────────────────────────────

        private void StartTerminal()
        {
            try
            {
                _terminalProcess?.Kill(true);
            }
            catch { }

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/Q",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _projectPath,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment.Remove("CLAUDECODE");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                // Intercept CWD marker lines
                if (e.Data.Contains(CwdMarker))
                {
                    var start = e.Data.IndexOf(CwdMarker) + CwdMarker.Length;
                    var end = e.Data.IndexOf(CwdMarker, start);
                    if (end > start)
                    {
                        var cwd = e.Data[start..end];
                        Dispatcher.BeginInvoke(() => UpdateTerminalCwd(cwd));
                    }
                    return; // suppress marker from output
                }
                Dispatcher.BeginInvoke(() =>
                {
                    TerminalOutput.AppendText(e.Data + "\n");
                    TerminalOutput.ScrollToEnd();
                });
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    TerminalOutput.AppendText(e.Data + "\n");
                    TerminalOutput.ScrollToEnd();
                });
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    TerminalOutput.AppendText("\n[Terminal closed]\n");
                    TerminalOutput.ScrollToEnd();
                });
            };

            try
            {
                process.Start();
                _terminalProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // Set initial CWD display
                UpdateTerminalCwd(_projectPath);
                // Query actual CWD via marker
                _terminalProcess.StandardInput.WriteLine($"@echo {CwdMarker}%CD%{CwdMarker}");
            }
            catch (Exception ex)
            {
                TerminalOutput.AppendText($"[Failed to start terminal: {ex.Message}]\n");
            }
        }

        private void TerminalSend_Click(object sender, RoutedEventArgs e)
        {
            SendTerminalCommand();
        }

        private void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendTerminalCommand();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (_terminalHistory.Count > 0)
                {
                    if (_terminalHistoryIndex < _terminalHistory.Count - 1)
                        _terminalHistoryIndex++;
                    TerminalInput.Text = _terminalHistory[_terminalHistory.Count - 1 - _terminalHistoryIndex];
                    TerminalInput.CaretIndex = TerminalInput.Text.Length;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (_terminalHistoryIndex > 0)
                {
                    _terminalHistoryIndex--;
                    TerminalInput.Text = _terminalHistory[_terminalHistory.Count - 1 - _terminalHistoryIndex];
                    TerminalInput.CaretIndex = TerminalInput.Text.Length;
                }
                else
                {
                    _terminalHistoryIndex = -1;
                    TerminalInput.Clear();
                }
                e.Handled = true;
            }
        }

        private void SendTerminalCommand()
        {
            var cmd = TerminalInput.Text;
            if (cmd == null) return;
            TerminalInput.Clear();
            _terminalHistoryIndex = -1;

            if (!string.IsNullOrWhiteSpace(cmd))
                _terminalHistory.Add(cmd);

            // Handle cls locally
            if (cmd.Trim().Equals("cls", StringComparison.OrdinalIgnoreCase) ||
                cmd.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                TerminalOutput.Clear();
            }

            if (_terminalProcess is not { HasExited: false })
            {
                TerminalOutput.AppendText("[Terminal not running, restarting...]\n");
                StartTerminal();
                return;
            }

            try
            {
                // Echo the command like a real terminal
                TerminalOutput.AppendText($"> {cmd}\n");
                TerminalOutput.ScrollToEnd();
                // Send the actual command, then query CWD via marker
                _terminalProcess!.StandardInput.WriteLine(cmd);
                _terminalProcess.StandardInput.WriteLine($"@echo {CwdMarker}%CD%{CwdMarker}");
            }
            catch
            {
                TerminalOutput.AppendText("[Failed to send command, restarting terminal...]\n");
                StartTerminal();
            }
        }

        private void UpdateTerminalCwd(string cwd)
        {
            _terminalCwd = cwd;
            TerminalCwd.Text = cwd;
            TerminalCwdTooltip.Text = cwd;
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
                nameRow.Children.Add(new TextBlock
                {
                    Text = proj.DisplayName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                });

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
                    Margin = new Thickness(0, 2, 0, 0)
                });

                Grid.SetColumn(infoPanel, 0);
                grid.Children.Add(infoPanel);

                // Right: buttons
                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };

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
                        Margin = new Thickness(0, 0, 0, 4),
                        Tag = proj.Path
                    };
                    cancelMcpBtn.Click += CancelMcp_Click;
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
                        Margin = new Thickness(0, 0, 0, 4),
                        Tag = proj.Path
                    };
                    connectBtn.Click += ConnectMcp_Click;
                    btnPanel.Children.Add(connectBtn);
                }

                var removeBtn = new Button
                {
                    Content = "Remove",
                    Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = proj.Path
                };
                removeBtn.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is string path)
                        RemoveProject_Click(path);
                };
                btnPanel.Children.Add(removeBtn);

                Grid.SetColumn(btnPanel, 1);
                grid.Children.Add(btnPanel);

                card.Child = grid;

                // Click card to select as active project
                var projPath = proj.Path;
                card.MouseLeftButtonUp += (_, _) =>
                {
                    _projectPath = projPath;
                    RefreshProjectCombo();
                    RefreshProjectList();
                    UpdateStatus();
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
        {
            path = path.Replace('/', '\\');
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(basePath))
                path = Path.Combine(basePath, path);
            try { path = Path.GetFullPath(path); } catch { }
            return path.ToLowerInvariant();
        }

        private static bool IsFileModifyTool(string? toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            return toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase);
        }

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
                TryAcquireFileLock(taskId, filePath, toolName); // best-effort, ignore result
                return true;
            }
            if (!TryAcquireFileLock(taskId, filePath, toolName))
            {
                HandleFileLockConflict(taskId, filePath, toolName);
                return false;
            }
            return true;
        }

        private bool TryAcquireFileLock(string taskId, string filePath, string toolName)
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
                    existing.OnPropertyChanged(nameof(FileLock.ToolName));
                    existing.OnPropertyChanged(nameof(FileLock.TimeText));
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
                AcquiredAt = DateTime.Now
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
                    if (blocker != null && blocker.Status == AgentTaskStatus.Running)
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
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLocks.Count;
            StatusText.Text = $"Running: {running}  |  Queued: {queued}  |  Completed: {completed}  |  " +
                              $"Cancelled: {cancelled}  |  Failed: {failed}  |  Locks: {locks}  |  " +
                              $"Project: {_projectPath}";
        }

        private static string StripAnsi(string text)
        {
            return Regex.Replace(text, @"\x1B(?:\[[0-9;]*[a-zA-Z]|\].*?(?:\x07|\x1B\\))", "");
        }

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
