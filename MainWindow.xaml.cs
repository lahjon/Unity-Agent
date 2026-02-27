using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UnityAgent.Dialogs;
using UnityAgent.Games;
using UnityAgent.Managers;
using UnityAgent.Models;

namespace UnityAgent
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const string DefaultSystemPrompt = TaskLauncher.DefaultSystemPrompt;
        private string SystemPrompt;

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private ICollectionView? _activeView;
        private ICollectionView? _historyView;
        private readonly DispatcherTimer _statusTimer;

        // Managers
        private readonly SettingsManager _settingsManager;
        private readonly HistoryManager _historyManager;
        private ImageAttachmentManager _imageManager = null!;
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private ProjectManager _projectManager = null!;
        private TaskExecutionManager _taskExecutionManager = null!;
        private TerminalTabManager? _terminalManager;
        private GeminiService _geminiService = null!;

        // Terminal collapse state
        private bool _terminalCollapsed;
        private GridLength _terminalExpandedHeight = new(120);

        // Games
        private readonly List<IMinigame> _availableGames = new();
        private IMinigame? _activeGame;

        public MainWindow()
        {
            InitializeComponent();

            SystemPrompt = DefaultSystemPrompt;

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityAgent");
            Directory.CreateDirectory(appDataDir);

            var scriptDir = Path.Combine(appDataDir, "scripts");
            Directory.CreateDirectory(scriptDir);

            // Initialize managers
            _settingsManager = new SettingsManager(appDataDir);
            _settingsManager.LoadSettings();

            _historyManager = new HistoryManager(appDataDir);

            _fileLockManager = new FileLockManager(FileLockBadge, Dispatcher);
            _fileLockManager.QueuedTaskResumed += OnQueuedTaskResumed;

            _outputTabManager = new OutputTabManager(OutputTabs, Dispatcher);
            _outputTabManager.TabCloseRequested += OnTabCloseRequested;
            _outputTabManager.InputSent += OnTabInputSent;
            _outputTabManager.CancelClicked += OnTabCancelClicked;

            // ProjectManager needs many UI refs — initialize after InitializeComponent
            _projectManager = new ProjectManager(
                appDataDir,
                DetermineInitialProjectPath(),
                PromptProjectLabel, AddProjectPath, ProjectListPanel,
                UseMcpToggle, ShortDescBox, LongDescBox,
                EditShortDescToggle, EditLongDescToggle,
                ShortDescEditButtons, LongDescEditButtons,
                RegenerateDescBtn, SkipPermissionsToggle, Dispatcher);
            _projectManager.LoadProjects();
            _projectManager.McpInvestigationRequested += OnMcpInvestigationRequested;

            _imageManager = new ImageAttachmentManager(appDataDir, ImageIndicator, ClearImagesBtn);
            _geminiService = new GeminiService(appDataDir);

            _taskExecutionManager = new TaskExecutionManager(
                scriptDir, _fileLockManager, _outputTabManager,
                () => SystemPrompt,
                task => _projectManager.GetProjectDescription(task),
                Dispatcher);
            _taskExecutionManager.TaskCompleted += OnTaskProcessCompleted;

            // Set up collections
            _activeView = CollectionViewSource.GetDefaultView(_activeTasks);
            _historyView = CollectionViewSource.GetDefaultView(_historyTasks);
            ActiveTasksList.ItemsSource = _activeView;
            HistoryTasksList.ItemsSource = _historyView;
            FileLocksListView.ItemsSource = _fileLockManager.FileLocksView;

            _historyManager.LoadHistory(_historyTasks, _settingsManager.HistoryRetentionHours);
            RefreshFilterCombos();
            _projectManager.RefreshProjectList(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, _) =>
            {
                foreach (var t in _activeTasks)
                    t.OnPropertyChanged(nameof(t.TimeInfo));
                _historyManager.CleanupOldHistory(_historyTasks, _outputTabManager.Tabs, OutputTabs, _outputTabManager.OutputBoxes, _settingsManager.HistoryRetentionHours);
                _outputTabManager.UpdateOutputTabWidths();
                UpdateStatus();
            };
            _statusTimer.Start();

            Closing += OnWindowClosing;
            UpdateStatus();

            _terminalManager = new TerminalTabManager(
                TerminalTabBar, TerminalOutput, TerminalInput,
                TerminalSendBtn, TerminalInterruptBtn, TerminalRootPath,
                Dispatcher, _projectManager.ProjectPath);
            _terminalManager.AddTerminal();

            // Games
            InitializeGames();
            MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        }

        private string DetermineInitialProjectPath()
        {
            _settingsManager.LoadSettings();
            // We need to peek at saved projects to determine the initial path
            // This is called before _projectManager.LoadProjects(), so we do a quick check
            var projectsFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityAgent", "projects.json");
            var savedProjects = new List<ProjectEntry>();
            try
            {
                if (File.Exists(projectsFile))
                {
                    var json = File.ReadAllText(projectsFile);
                    savedProjects = System.Text.Json.JsonSerializer.Deserialize<List<ProjectEntry>>(json,
                        new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }) ?? new();
                }
            }
            catch { }

            var restored = _settingsManager.LastSelectedProject != null && savedProjects.Any(p => p.Path == _settingsManager.LastSelectedProject);
            return restored ? _settingsManager.LastSelectedProject! :
                   savedProjects.Count > 0 ? savedProjects[0].Path : Directory.GetCurrentDirectory();
        }

        // ── Window Loaded ──────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
            catch { }

            LoadSystemPrompt();
            SystemPromptBox.Text = SystemPrompt;

            // Initialize Gemini API key display
            if (_geminiService.IsConfigured)
            {
                GeminiApiKeyBox.Text = _geminiService.GetMaskedApiKey();
                GeminiKeyStatus.Text = "API key configured";
                GeminiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C));
            }

            // Initialize Gemini model dropdown
            foreach (var model in Managers.GeminiService.AvailableModels)
                GeminiModelCombo.Items.Add(model);
            GeminiModelCombo.SelectedItem = _geminiService.SelectedModel;

            foreach (ComboBoxItem item in HistoryRetentionCombo.Items)
            {
                if (int.TryParse(item.Tag?.ToString(), out var h) && h == _settingsManager.HistoryRetentionHours)
                {
                    HistoryRetentionCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // ── System Prompt ──────────────────────────────────────────

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

        // ── Settings Sync ──────────────────────────────────────────

        private void SyncSettingsForProject()
        {
            try
            {
                _projectManager.RefreshProjectCombo();
                _projectManager.RefreshProjectList(
                    p => _terminalManager?.UpdateWorkingDirectory(p),
                    () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                    SyncSettingsForProject);
                _projectManager.UpdateMcpToggleForProject();
                RefreshFilterCombos();
                ActiveFilter_Changed(ActiveFilterCombo, null!);
                HistoryFilter_Changed(HistoryFilterCombo, null!);

                if (EditSystemPromptToggle.IsChecked == true)
                {
                    EditSystemPromptToggle.IsChecked = false;
                    SystemPromptBox.Text = SystemPrompt;
                }

                UpdateStatus();
            }
            catch { }
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
            combo.SelectedIndex = 0;
            combo.SelectionChanged += handler;
            handler(combo, null!);
        }

        // ── Project Description Editing ────────────────────────────

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

        private void SaveShortDesc_Click(object sender, RoutedEventArgs e) => _projectManager.SaveShortDesc();

        private void SaveLongDesc_Click(object sender, RoutedEventArgs e) => _projectManager.SaveLongDesc();

        private void RegenerateDescriptions_Click(object sender, RoutedEventArgs e) => _projectManager.RegenerateDescriptions();

        // ── Toggle Handlers ────────────────────────────────────────

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
            if (_settingsManager == null || _projectManager == null) return;
            if (HistoryRetentionCombo?.SelectedItem is not ComboBoxItem item) return;
            if (int.TryParse(item.Tag?.ToString(), out var hours))
            {
                _settingsManager.HistoryRetentionHours = hours;
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
        }

        // ── Gemini Settings ─────────────────────────────────────────

        private void SaveGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            var key = GeminiApiKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key) || key.Contains('*'))
            {
                GeminiKeyStatus.Text = "Enter a valid API key";
                GeminiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55));
                return;
            }

            try
            {
                _geminiService.SaveApiKey(key);
                GeminiApiKeyBox.Text = _geminiService.GetMaskedApiKey();
                GeminiKeyStatus.Text = "API key saved successfully";
                GeminiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0xB8, 0x5C));
            }
            catch (Exception ex)
            {
                GeminiKeyStatus.Text = $"Error saving key: {ex.Message}";
                GeminiKeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x55));
            }
        }

        private void GeminiApiLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true });
            }
            catch { }
        }

        private void OpenGeminiImages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _geminiService.GetImageDirectory();
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void GeminiModelCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (GeminiModelCombo?.SelectedItem is not string model) return;
            _geminiService.SelectedModel = model;
        }

        // ── Project Events ─────────────────────────────────────────

        private void AddProjectPath_Click(object sender, MouseButtonEventArgs e) => _projectManager.HandleAddProjectPathClick();

        private void AddProject_Click(object sender, RoutedEventArgs e) =>
            _projectManager.AddProject(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);

        private void CreateProject_Click(object sender, RoutedEventArgs e) =>
            _projectManager.CreateProject(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);

        private void ProjectCombo_Changed(object sender, SelectionChangedEventArgs e) { }

        private void ModelCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ModelCombo?.SelectedItem is not ComboBoxItem item) return;
            var isGemini = item.Tag?.ToString() == "Gemini";
            // Disable advanced options that don't apply to Gemini
            if (SkipPermissionsToggle != null) SkipPermissionsToggle.IsEnabled = !isGemini;
            if (RemoteSessionToggle != null) RemoteSessionToggle.IsEnabled = !isGemini;
            if (HeadlessToggle != null) HeadlessToggle.IsEnabled = !isGemini;
            if (OvernightToggle != null) OvernightToggle.IsEnabled = !isGemini;
            if (SpawnTeamToggle != null) SpawnTeamToggle.IsEnabled = !isGemini;
            if (ExtendedPlanningToggle != null) ExtendedPlanningToggle.IsEnabled = !isGemini;
        }

        // ── Input Events ───────────────────────────────────────────

        private void TaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Execute_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_imageManager.HandlePasteImage())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void TaskInput_DragOver(object sender, DragEventArgs e) => _imageManager.HandleDragOver(e);

        private void TaskInput_Drop(object sender, DragEventArgs e) => _imageManager.HandleDrop(e);

        private void ClearImages_Click(object sender, RoutedEventArgs e) => _imageManager.ClearImages();

        // ── Dependencies ────────────────────────────────────────────

        private readonly List<AgentTask> _pendingDependencies = new();

        private void TaskCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.IsFinished) return;

            var data = new DataObject("AgentTask", task);
            DragDrop.DoDragDrop(el, data, DragDropEffects.Link);
        }

        private void DependencyZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("AgentTask"))
            {
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void DependencyZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("AgentTask")) return;
            if (e.Data.GetData("AgentTask") is not AgentTask task) return;
            if (task.IsFinished) return;
            if (_pendingDependencies.Any(t => t.Id == task.Id)) return;

            _pendingDependencies.Add(task);
            RefreshDependencyChips();
            e.Handled = true;
        }

        private void RemoveDependency_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not string taskId) return;
            _pendingDependencies.RemoveAll(t => t.Id == taskId);
            RefreshDependencyChips();
        }

        private void RefreshDependencyChips()
        {
            DependencyChips.Items.Clear();

            if (_pendingDependencies.Count == 0)
            {
                DependencyPlaceholder.Visibility = Visibility.Visible;
                DependencyChips.Visibility = Visibility.Collapsed;
                return;
            }

            DependencyPlaceholder.Visibility = Visibility.Collapsed;
            DependencyChips.Visibility = Visibility.Visible;

            foreach (var dep in _pendingDependencies)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A2A")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 4, 2),
                    Margin = new Thickness(0, 0, 4, 2)
                };

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                panel.Children.Add(new TextBlock
                {
                    Text = $"#{dep.Id}",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD600")),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });

                panel.Children.Add(new TextBlock
                {
                    Text = dep.ShortDescription,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AAA")),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 120,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var removeBtn = new Button
                {
                    Content = "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 8,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888")),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(2),
                    Margin = new Thickness(2, 0, 0, 0),
                    Tag = dep.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeBtn.Click += RemoveDependency_Click;
                panel.Children.Add(removeBtn);

                chip.Child = panel;
                DependencyChips.Items.Add(chip);
            }
        }

        private void ClearPendingDependencies()
        {
            _pendingDependencies.Clear();
            RefreshDependencyChips();
        }

        // ── Execute ────────────────────────────────────────────────

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            var desc = TaskInput.Text?.Trim();
            if (!TaskLauncher.ValidateTaskInput(desc)) return;

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem && modelItem.Tag?.ToString() == "Gemini")
                selectedModel = ModelType.Gemini;

            var task = TaskLauncher.CreateTask(
                desc!,
                _projectManager.ProjectPath,
                SkipPermissionsToggle.IsChecked == true,
                RemoteSessionToggle.IsChecked == true,
                HeadlessToggle.IsChecked == true,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                _imageManager.DetachImages(),
                selectedModel);
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);

            // Capture dependencies before clearing
            var dependencies = _pendingDependencies.ToList();
            ClearPendingDependencies();
            TaskInput.Clear();

            // Reset per-task toggles
            RemoteSessionToggle.IsChecked = false;
            HeadlessToggle.IsChecked = false;
            SpawnTeamToggle.IsChecked = false;
            OvernightToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;

            if (selectedModel == ModelType.Gemini)
            {
                ExecuteGeminiTask(task);
                return;
            }

            if (task.Headless)
            {
                _taskExecutionManager.LaunchHeadless(task);
                UpdateStatus();
                return;
            }

            task.Summary = "Processing Task...";
            _activeTasks.Add(task);
            _outputTabManager.CreateTab(task);

            // Check if any dependencies are still active (not finished)
            var activeDeps = dependencies.Where(d => !d.IsFinished).ToList();
            if (activeDeps.Count > 0)
            {
                task.DependencyTaskIds = activeDeps.Select(d => d.Id).ToList();
                task.Status = AgentTaskStatus.Queued;
                task.QueuedReason = "Waiting for dependencies";
                task.BlockedByTaskId = activeDeps[0].Id;
                _outputTabManager.AppendOutput(task.Id,
                    $"[UnityAgent] Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.Id}"))}\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            _ = GenerateSummaryInBackground(task, desc!);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void ExecuteGeminiTask(AgentTask task)
        {
            if (!_geminiService.IsConfigured)
            {
                Dialogs.DarkDialog.ShowConfirm(
                    "Gemini API key not configured.\n\nGo to Settings > Gemini tab to set your API key.\n" +
                    "Get one free at https://aistudio.google.com/apikey",
                    "Gemini Not Configured");
                return;
            }

            task.Summary = "Generating Image...";
            _activeTasks.Add(task);
            _outputTabManager.CreateGeminiTab(task, _geminiService.GetImageDirectory());

            _ = RunGeminiImageGeneration(task);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task RunGeminiImageGeneration(AgentTask task)
        {
            var progress = new Progress<string>(msg =>
            {
                _outputTabManager.AppendOutput(task.Id, msg, _activeTasks, _historyTasks);
            });

            _outputTabManager.AppendOutput(task.Id,
                $"[Gemini] Task #{task.Id} — Image generation\n" +
                $"[Gemini] Prompt: {task.Description}\n\n",
                _activeTasks, _historyTasks);

            try
            {
                var result = await _geminiService.GenerateImageAsync(task.Description, task.Id, progress);

                if (result.Success)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime = DateTime.Now;
                    task.GeneratedImagePaths.AddRange(result.ImagePaths);

                    foreach (var path in result.ImagePaths)
                        _outputTabManager.AddGeminiImage(task.Id, path);

                    if (!string.IsNullOrEmpty(result.TextResponse))
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[Gemini] {result.TextResponse}\n", _activeTasks, _historyTasks);

                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[Gemini] Done — {result.ImagePaths.Count} image(s) generated.\n",
                        _activeTasks, _historyTasks);
                    task.Summary = $"Image: {(task.Description.Length > 30 ? task.Description[..30] + "..." : task.Description)}";
                }
                else
                {
                    task.Status = AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n{result.ErrorMessage}\n", _activeTasks, _historyTasks);
                }
            }
            catch (Exception ex)
            {
                task.Status = AgentTaskStatus.Failed;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    $"\n[Gemini] Unexpected error: {ex.Message}\n", _activeTasks, _historyTasks);
            }

            _outputTabManager.UpdateTabHeader(task);
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
                    _outputTabManager.UpdateTabHeader(task);
                }
            }
            catch { }
        }

        // ── Tab Events ─────────────────────────────────────────────

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => _outputTabManager.UpdateOutputTabWidths();

        private void OnTabCloseRequested(AgentTask task) => CloseTab(task);

        private void OnTabInputSent(AgentTask task, TextBox inputBox) =>
            _taskExecutionManager.SendInput(task, inputBox, _activeTasks, _historyTasks);

        private void OnTabCancelClicked(AgentTask task) => CancelTaskImmediate(task);

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing || task.Status == AgentTaskStatus.Queued)
            {
                var processAlreadyDone = task.Process == null || task.Process.HasExited;
                if (processAlreadyDone)
                {
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime ??= DateTime.Now;
                }
                else
                {
                    if (!DarkDialog.ShowConfirm("This task is still running. Closing will terminate it.\n\nAre you sure?", "Task Running"))
                        return;

                    task.Status = AgentTaskStatus.Cancelled;
                    task.EndTime = DateTime.Now;
                    TaskExecutionManager.KillProcess(task);
                }

                _fileLockManager.ReleaseTaskLocks(task.Id);
                _fileLockManager.RemoveQueuedInfo(task.Id);
                _taskExecutionManager.RemoveStreamingState(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                _historyManager.SaveHistory(_historyTasks);
                _fileLockManager.CheckQueuedTasks(_activeTasks);
                CheckDependencyQueuedTasks(task.Id);
            }
            else if (_activeTasks.Contains(task))
            {
                task.EndTime ??= DateTime.Now;
                _fileLockManager.ReleaseTaskLocks(task.Id);
                _fileLockManager.RemoveQueuedInfo(task.Id);
                _taskExecutionManager.RemoveStreamingState(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                _historyManager.SaveHistory(_historyTasks);
                CheckDependencyQueuedTasks(task.Id);
            }

            _outputTabManager.CloseTab(task);
            UpdateStatus();
        }

        // ── Task Actions ───────────────────────────────────────────

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.Queued)
            {
                if (task.DependencyTaskIds.Count > 0)
                {
                    // Force-start a dependency-queued task
                    task.Status = AgentTaskStatus.Running;
                    task.QueuedReason = null;
                    task.BlockedByTaskId = null;
                    task.DependencyTaskIds.Clear();
                    task.StartTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[UnityAgent] Force-starting task #{task.Id} (dependencies skipped)...\n\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                    UpdateStatus();
                }
                else
                {
                    _fileLockManager.ForceStartQueuedTask(task);
                }
                return;
            }

            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;
            TaskExecutionManager.KillProcess(task);
            _outputTabManager.UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void CopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!string.IsNullOrEmpty(task.Description))
                Clipboard.SetText(task.Description);
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
            else if (e.ChangedButton == MouseButton.Left && _outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            }
        }

        private void CancelTask(AgentTask task)
        {
            if (task.IsFinished)
            {
                _outputTabManager.UpdateTabHeader(task);
                MoveToHistory(task);
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing)
            {
                if (!DarkDialog.ShowConfirm(
                    $"Task #{task.Id} is still running.\nAre you sure you want to cancel it?",
                    "Cancel Running Task"))
                    return;
            }

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
            TaskExecutionManager.KillProcess(task);
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);
            _outputTabManager.AppendOutput(task.Id, "\n[UnityAgent] Task cancelled.\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void CancelTaskImmediate(AgentTask task)
        {
            if (task.IsFinished) return;

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing)
            {
                if (!DarkDialog.ShowConfirm(
                    $"Task #{task.Id} is still running.\nAre you sure you want to cancel it?",
                    "Cancel Running Task"))
                    return;
            }

            _taskExecutionManager.CancelTaskImmediate(task);
            _outputTabManager.AppendOutput(task.Id, "\n[UnityAgent] Task cancelled.\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            _outputTabManager.CloseTab(task);
            _historyTasks.Remove(task);
            _historyManager.SaveHistory(_historyTasks);
            RefreshFilterCombos();
            _outputTabManager.UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void ClearFinished_Click(object sender, RoutedEventArgs e)
        {
            var finished = _activeTasks.Where(t => t.IsFinished).ToList();
            if (finished.Count == 0) return;

            foreach (var task in finished)
                MoveToHistory(task);

            _outputTabManager.UpdateOutputTabWidths();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!DarkDialog.ShowConfirm(
                $"Are you sure you want to clear all {_historyTasks.Count} history entries? This cannot be undone.",
                "Clear History")) return;

            foreach (var task in _historyTasks.ToList())
                _outputTabManager.CloseTab(task);
            _historyTasks.Clear();
            _historyManager.SaveHistory(_historyTasks);
            RefreshFilterCombos();
            _outputTabManager.UpdateOutputTabWidths();
            UpdateStatus();
        }

        private void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (_outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
                return;
            }

            _outputTabManager.CreateTab(task);
            _outputTabManager.AppendOutput(task.Id, $"[UnityAgent] Resumed session\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[UnityAgent] Original task: {task.Description}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[UnityAgent] Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[UnityAgent] Status: {task.StatusText}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"\n[UnityAgent] Type a follow-up message below. It will be sent with --continue.\n", _activeTasks, _historyTasks);

            _historyTasks.Remove(task);
            _activeTasks.Add(task);
            UpdateStatus();
        }

        // ── Orchestration ──────────────────────────────────────────

        private void MoveToHistory(AgentTask task)
        {
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _activeTasks.Remove(task);
            _historyTasks.Insert(0, task);
            _outputTabManager.CloseTab(task);
            _historyManager.SaveHistory(_historyTasks);
            RefreshFilterCombos();
            UpdateStatus();
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            CheckDependencyQueuedTasks(task.Id);
        }

        private void CheckDependencyQueuedTasks(string completedTaskId)
        {
            var toResume = new List<AgentTask>();

            foreach (var task in _activeTasks)
            {
                if (task.Status != AgentTaskStatus.Queued || task.DependencyTaskIds.Count == 0) continue;
                if (!task.DependencyTaskIds.Contains(completedTaskId)) continue;

                // Check if all dependency tasks are no longer active (running/ongoing/queued)
                var allDepsResolved = task.DependencyTaskIds.All(depId =>
                {
                    var dep = _activeTasks.FirstOrDefault(t => t.Id == depId);
                    return dep == null || dep.IsFinished;
                });

                if (allDepsResolved)
                    toResume.Add(task);
            }

            foreach (var task in toResume)
            {
                task.Status = AgentTaskStatus.Running;
                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.DependencyTaskIds.Clear();
                task.StartTime = DateTime.Now;

                _outputTabManager.AppendOutput(task.Id,
                    $"\n[UnityAgent] All dependencies resolved — starting task #{task.Id}...\n\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
                _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                UpdateStatus();
            }
        }

        private void OnQueuedTaskResumed(string taskId)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            _outputTabManager.AppendOutput(taskId, $"\n[UnityAgent] Resuming task #{taskId} (blocking task finished)...\n\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            UpdateStatus();
        }

        private void OnTaskProcessCompleted(string taskId) => CheckDependencyQueuedTasks(taskId);

        private void OnMcpInvestigationRequested(AgentTask task)
        {
            _activeTasks.Add(task);
            _outputTabManager.CreateTab(task);
            _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            RefreshFilterCombos();
            UpdateStatus();
        }

        // ── Selection Sync ─────────────────────────────────────────

        private void ActiveTask_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (ActiveTasksList.SelectedItem is AgentTask task && _outputTabManager.HasTab(task.Id))
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
        }

        private void HistoryTask_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryTasksList.SelectedItem is AgentTask task && _outputTabManager.HasTab(task.Id))
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
        }

        // ── Window Close ───────────────────────────────────────────

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            var runningCount = _activeTasks.Count(t =>
                t.Status is AgentTaskStatus.Running or AgentTaskStatus.Ongoing);

            if (runningCount > 0)
            {
                if (!DarkDialog.ShowConfirm(
                    $"There are {runningCount} task(s) still running.\n\n" +
                    "Closing will terminate all of them. Continue?",
                    "Active Tasks Running"))
                {
                    e.Cancel = true;
                    return;
                }
            }

            _projectManager.SaveProjects();
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
            _historyManager.SaveHistory(_historyTasks);

            foreach (var task in _activeTasks)
                TaskExecutionManager.KillProcess(task);

            _fileLockManager.ClearAll();
            _taskExecutionManager.StreamingToolState.Clear();

            StopActiveGame();
            _terminalManager?.DisposeAll();
        }

        // ── Terminal ───────────────────────────────────────────────

        private void TerminalSend_Click(object sender, RoutedEventArgs e) => _terminalManager?.SendCommand();

        private void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e) => _terminalManager?.HandleKeyDown(e);

        private void TerminalInterrupt_Click(object sender, RoutedEventArgs e) => _terminalManager?.SendInterrupt();

        private void TerminalCollapse_Click(object sender, RoutedEventArgs e) => ToggleTerminalCollapse();

        private void TerminalHeader_MouseDown(object sender, MouseButtonEventArgs e) => ToggleTerminalCollapse();

        private void ToggleTerminalCollapse()
        {
            _terminalCollapsed = !_terminalCollapsed;

            if (_terminalCollapsed)
            {
                // Save current height before collapsing
                _terminalExpandedHeight = TerminalRow.Height;

                TerminalRow.MinHeight = 0;
                TerminalRow.Height = GridLength.Auto;
                TerminalSplitter.Visibility = Visibility.Collapsed;
                TerminalOutput.Visibility = Visibility.Collapsed;
                TerminalInputBar.Visibility = Visibility.Collapsed;
                TerminalRootBar.Visibility = Visibility.Collapsed;
                TerminalTabBar.Visibility = Visibility.Collapsed;

                TerminalCollapseBtn.Content = "\uE70E"; // ChevronUp
                TerminalCollapseBtn.ToolTip = "Expand terminal";
            }
            else
            {
                TerminalRow.MinHeight = 60;
                TerminalRow.Height = _terminalExpandedHeight;
                TerminalSplitter.Visibility = Visibility.Visible;
                TerminalOutput.Visibility = Visibility.Visible;
                TerminalInputBar.Visibility = Visibility.Visible;
                TerminalRootBar.Visibility = Visibility.Visible;
                TerminalTabBar.Visibility = Visibility.Visible;

                TerminalCollapseBtn.Content = "\uE70D"; // ChevronDown
                TerminalCollapseBtn.ToolTip = "Collapse terminal";
            }
        }

        // ── Filters ────────────────────────────────────────────────

        private void RefreshFilterCombos()
        {
            if (ActiveFilterCombo == null || HistoryFilterCombo == null) return;

            var allPaths = new HashSet<string>();
            foreach (var p in _projectManager.SavedProjects)
                allPaths.Add(p.Path);
            foreach (var t in _activeTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);
            foreach (var t in _historyTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);

            var projectNames = allPaths
                .Select(p => new { Path = p, Name = Path.GetFileName(p) })
                .OrderBy(x => x.Name)
                .ToList();

            var activeSelection = ActiveFilterCombo.SelectedItem as ComboBoxItem;
            var activeTag = activeSelection?.Tag as string;
            var historySelection = HistoryFilterCombo.SelectedItem as ComboBoxItem;
            var historyTag = historySelection?.Tag as string;

            ActiveFilterCombo.SelectionChanged -= ActiveFilter_Changed;
            ActiveFilterCombo.Items.Clear();
            ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            var found = false;
            if (!string.IsNullOrEmpty(activeTag))
            {
                foreach (ComboBoxItem item in ActiveFilterCombo.Items)
                {
                    if (item.Tag as string == activeTag) { ActiveFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) ActiveFilterCombo.SelectedIndex = 0;
            ActiveFilterCombo.SelectionChanged += ActiveFilter_Changed;

            HistoryFilterCombo.SelectionChanged -= HistoryFilter_Changed;
            HistoryFilterCombo.Items.Clear();
            HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            found = false;
            if (!string.IsNullOrEmpty(historyTag))
            {
                foreach (ComboBoxItem item in HistoryFilterCombo.Items)
                {
                    if (item.Tag as string == historyTag) { HistoryFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) HistoryFilterCombo.SelectedIndex = 0;
            HistoryFilterCombo.SelectionChanged += HistoryFilter_Changed;

            RefreshStatusFilterCombos();
        }

        private void RefreshStatusFilterCombos()
        {
            if (ActiveStatusFilterCombo == null || HistoryStatusFilterCombo == null) return;

            var statusOptions = new[] { "All Status", "Running", "Queued", "Completed", "Failed", "Cancelled", "Ongoing" };

            var activeStatusTag = (ActiveStatusFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            var historyStatusTag = (HistoryStatusFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            ActiveStatusFilterCombo.SelectionChanged -= ActiveStatusFilter_Changed;
            ActiveStatusFilterCombo.Items.Clear();
            foreach (var s in statusOptions)
                ActiveStatusFilterCombo.Items.Add(new ComboBoxItem { Content = s, Tag = s == "All Status" ? "" : s });
            var found = false;
            if (!string.IsNullOrEmpty(activeStatusTag))
            {
                foreach (ComboBoxItem item in ActiveStatusFilterCombo.Items)
                {
                    if (item.Tag as string == activeStatusTag) { ActiveStatusFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) ActiveStatusFilterCombo.SelectedIndex = 0;
            ActiveStatusFilterCombo.SelectionChanged += ActiveStatusFilter_Changed;

            HistoryStatusFilterCombo.SelectionChanged -= HistoryStatusFilter_Changed;
            HistoryStatusFilterCombo.Items.Clear();
            foreach (var s in statusOptions)
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = s, Tag = s == "All Status" ? "" : s });
            found = false;
            if (!string.IsNullOrEmpty(historyStatusTag))
            {
                foreach (ComboBoxItem item in HistoryStatusFilterCombo.Items)
                {
                    if (item.Tag as string == historyStatusTag) { HistoryStatusFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) HistoryStatusFilterCombo.SelectedIndex = 0;
            HistoryStatusFilterCombo.SelectionChanged += HistoryStatusFilter_Changed;
        }

        private void ApplyActiveFilters()
        {
            if (_activeView == null) return;
            var projectTag = (ActiveFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (ActiveStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);

            if (!hasProject && !hasStatus)
                _activeView.Filter = null;
            else
                _activeView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    return true;
                };
        }

        private void ApplyHistoryFilters()
        {
            if (_historyView == null) return;
            var projectTag = (HistoryFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (HistoryStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);

            if (!hasProject && !hasStatus)
                _historyView.Filter = null;
            else
                _historyView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    return true;
                };
        }

        private void ActiveFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        // ── Games ──────────────────────────────────────────────────

        private void InitializeGames()
        {
            _availableGames.Add(new ReactionTestGame());
            _availableGames.Add(new QuickMathGame());
            _availableGames.Add(new BirdHunterGame());
            RebuildGameSelector();
        }

        private void RebuildGameSelector()
        {
            GameIconsPanel.Children.Clear();
            foreach (var game in _availableGames)
            {
                var btn = new Button
                {
                    Width = 90, Height = 90,
                    Margin = new Thickness(6),
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                    BorderThickness = new Thickness(0),
                    ToolTip = game.GameDescription
                };
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = game.GameIcon,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                stack.Children.Add(new TextBlock
                {
                    Text = game.GameName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var template = new ControlTemplate(typeof(Button));
                var borderFactory = new FrameworkElementFactory(typeof(Border));
                borderFactory.Name = "Bd";
                borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)));
                borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                borderFactory.SetValue(Border.PaddingProperty, new Thickness(8));
                var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                borderFactory.AppendChild(contentFactory);
                template.VisualTree = borderFactory;

                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                    new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)), "Bd"));
                template.Triggers.Add(hoverTrigger);

                btn.Template = template;
                btn.Content = stack;

                var capturedGame = game;
                btn.Click += (_, _) => LaunchGame(capturedGame);
                GameIconsPanel.Children.Add(btn);
            }
        }

        private void LaunchGame(IMinigame game)
        {
            if (_activeGame != null)
            {
                _activeGame.QuitRequested -= OnGameQuitRequested;
                _activeGame.Stop();
            }
            _activeGame = game;
            game.QuitRequested += OnGameQuitRequested;
            GameSelectorPanel.Visibility = Visibility.Collapsed;
            GameHost.Content = game.View;
            GameHost.Visibility = Visibility.Visible;
            game.Start();
        }

        private void OnGameQuitRequested() => StopActiveGame();

        private void StopActiveGame()
        {
            if (_activeGame == null) return;
            _activeGame.QuitRequested -= OnGameQuitRequested;
            _activeGame.Stop();
            GameHost.Content = null;
            GameHost.Visibility = Visibility.Collapsed;
            GameSelectorPanel.Visibility = Visibility.Visible;
            _activeGame = null;
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabs) return;
            if (MainTabs.SelectedItem != GamesTabItem)
                StopActiveGame();
        }

        // ── Status ─────────────────────────────────────────────────

        private void UpdateStatus()
        {
            var running = _activeTasks.Count(t => t.Status == AgentTaskStatus.Running);
            var ongoing = _activeTasks.Count(t => t.Status == AgentTaskStatus.Ongoing);
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLockManager.LockCount;
            var projectName = Path.GetFileName(_projectManager.ProjectPath);
            StatusText.Text = $"{projectName}  |  Running: {running}  |  Ongoing: {ongoing}  |  Queued: {queued}  |  " +
                              $"Completed: {completed}  |  Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                              $"Locks: {locks}  |  {_projectManager.ProjectPath}";
        }
    }
}
