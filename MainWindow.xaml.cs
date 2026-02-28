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
using System.Windows.Media.Animation;
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
        private readonly ObservableCollection<AgentTask> _storedTasks = new();
        private ICollectionView? _activeView;
        private ICollectionView? _historyView;
        private ICollectionView? _storedView;
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
        private HelperManager _helperManager = null!;

        // Task numbering (1–9999, resets on app restart)
        private int _nextTaskNumber = 1;

        // Saved prompts
        private readonly List<SavedPromptEntry> _savedPrompts = new();

        // Unviewed stored task count
        private int _unviewedStoredCount;

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
            _outputTabManager.TabStoreRequested += OnTabStoreRequested;
            _outputTabManager.InputSent += OnTabInputSent;

            // ProjectManager needs many UI refs — initialize after InitializeComponent
            _projectManager = new ProjectManager(
                appDataDir,
                DetermineInitialProjectPath(),
                PromptProjectLabel, AddProjectPath, ProjectListPanel,
                UseMcpToggle, ShortDescBox, LongDescBox,
                EditShortDescToggle, EditLongDescToggle,
                ShortDescEditButtons, LongDescEditButtons,
                RegenerateDescBtn, Dispatcher);
            _projectManager.LoadProjects();
            _projectManager.McpInvestigationRequested += OnMcpInvestigationRequested;

            _imageManager = new ImageAttachmentManager(appDataDir, ImageIndicator, ClearImagesBtn);
            _geminiService = new GeminiService(appDataDir);
            _helperManager = new HelperManager(appDataDir, _projectManager.ProjectPath);
            _helperManager.SetActiveTaskSource(() =>
                _activeTasks.Where(t => !t.IsFinished).Select(t => t.Description));
            _helperManager.GenerationStarted += OnHelperGenerationStarted;
            _helperManager.GenerationCompleted += OnHelperGenerationCompleted;
            _helperManager.GenerationFailed += OnHelperGenerationFailed;

            _taskExecutionManager = new TaskExecutionManager(
                scriptDir, _fileLockManager, _outputTabManager,
                () => SystemPrompt,
                task => _projectManager.GetProjectDescription(task),
                Dispatcher);
            _taskExecutionManager.TaskCompleted += OnTaskProcessCompleted;

            // Set up collections
            _activeView = CollectionViewSource.GetDefaultView(_activeTasks);
            _historyView = CollectionViewSource.GetDefaultView(_historyTasks);
            _storedView = CollectionViewSource.GetDefaultView(_storedTasks);
            ActiveTasksList.ItemsSource = _activeView;
            HistoryTasksList.ItemsSource = _historyView;
            StoredTasksList.ItemsSource = _storedView;
            FileLocksListView.ItemsSource = _fileLockManager.FileLocksView;
            SuggestionsListView.ItemsSource = _helperManager.Suggestions;

            _historyManager.LoadHistory(_historyTasks, _settingsManager.HistoryRetentionHours);
            _historyManager.LoadStoredTasks(_storedTasks);
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

            // Saved prompts
            LoadSavedPrompts();

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
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load projects for initial path", ex); }

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
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", $"DWM dark mode attribute failed: {ex.Message}"); }

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

            if (_settingsManager.SettingsPanelCollapsed)
                ApplySettingsPanelCollapsed(true);
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
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load system prompt", ex); }
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
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to save system prompt", ex); }
            EditSystemPromptToggle.IsChecked = false;
        }

        private void ResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(SystemPromptFile))
                    File.Delete(SystemPromptFile);
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to delete system prompt file", ex); }
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
                _helperManager.SwitchProject(_projectManager.ProjectPath);
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
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to sync settings for project", ex); }
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
        }

        private void OvernightToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ExecuteButton == null) return;
            UpdateExecuteButtonText();
        }

        private void UpdateExecuteButtonText()
        {
            if (PlanOnlyToggle.IsChecked == true)
                ExecuteButton.Content = "Plan Task";
            else if (OvernightToggle.IsChecked == true)
                ExecuteButton.Content = "Start Overnight Task";
            else
                ExecuteButton.Content = "Execute Task";
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

        private void ViewLogs_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.LogViewerDialog.Show();
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
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Gemini API link", ex); }
        }

        private void OpenGeminiImages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _geminiService.GetImageDirectory();
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Gemini images folder", ex); }
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
                true,
                RemoteSessionToggle.IsChecked == true,
                HeadlessToggle.IsChecked == true,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                PlanOnlyToggle.IsChecked == true,
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
            PlanOnlyToggle.IsChecked = false;

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
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            // Check if any dependencies are still active (not finished)
            var activeDeps = dependencies.Where(d => !d.IsFinished).ToList();
            if (activeDeps.Count > 0)
            {
                task.DependencyTaskIds = activeDeps.Select(d => d.Id).ToList();

                if (!task.PlanOnly)
                {
                    // Start in plan mode first, then queue when planning completes
                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[UnityAgent] Dependencies pending ({string.Join(", ", activeDeps.Select(d => $"#{d.Id}"))}) — starting in plan mode...\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else
                {
                    // User explicitly wants plan-only — queue as before
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Waiting for dependencies";
                    task.BlockedByTaskId = activeDeps[0].Id;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[UnityAgent] Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.Id}"))}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
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
            AddActiveTask(task);
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
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to generate summary for task {task.Id}: {ex.Message}"); }
        }

        // ── Tab Events ─────────────────────────────────────────────

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => _outputTabManager.UpdateOutputTabWidths();

        private void OnTabCloseRequested(AgentTask task) => CloseTab(task);

        private void OnTabStoreRequested(AgentTask task)
        {
            // If the task is still active, cancel it first
            if (task.IsRunning || task.IsPaused || task.IsQueued)
            {
                task.OvernightRetryTimer?.Stop();
                task.OvernightIterationTimer?.Stop();
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                TaskExecutionManager.KillProcess(task);
                _outputTabManager.AppendOutput(task.Id,
                    "\n[UnityAgent] Task cancelled and stored.\n", _activeTasks, _historyTasks);
            }

            // Create the stored task entry
            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                StoredPrompt = task.StoredPrompt ?? task.Description,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            _unviewedStoredCount++;
            UpdateStoredTabBadge();
            RefreshFilterCombos();

            // Clean up the active task and close the tab
            if (_activeTasks.Contains(task))
            {
                _fileLockManager.ReleaseTaskLocks(task.Id);
                _fileLockManager.RemoveQueuedInfo(task.Id);
                _taskExecutionManager.RemoveStreamingState(task.Id);
                _activeTasks.Remove(task);
                _historyTasks.Insert(0, task);
                _historyManager.SaveHistory(_historyTasks);
                _fileLockManager.CheckQueuedTasks(_activeTasks);
                CheckDependencyQueuedTasks(task.Id);
            }

            _outputTabManager.CloseTab(task);
            UpdateStatus();
        }

        private void OnTabInputSent(AgentTask task, TextBox inputBox) =>
            _taskExecutionManager.SendInput(task, inputBox, _activeTasks, _historyTasks);

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.Paused)
            {
                _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
            }
            else if (task.IsRunning)
            {
                _taskExecutionManager.PauseTask(task);
                _outputTabManager.AppendOutput(task.Id, "\n[UnityAgent] Task paused.\n", _activeTasks, _historyTasks);
            }
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Paused || task.Status == AgentTaskStatus.Queued)
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

                // Create stored task if this was a plan-only task that completed
                if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                    CreateStoredTaskFromPlan(task);

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
                // Create stored task if this was a plan-only task that completed
                if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                    CreateStoredTaskFromPlan(task);

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
            CancelTask(task, el);
        }

        private void TaskCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (e.ChangedButton == MouseButton.Middle)
            {
                CancelTask(task, el);
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Left && _outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            }
        }

        private void CancelTask(AgentTask task, FrameworkElement? sender = null)
        {
            if (task.IsFinished)
            {
                _outputTabManager.UpdateTabHeader(task);
                if (sender != null)
                {
                    _outputTabManager.AppendOutput(task.Id, "\n[UnityAgent] Task removed.\n", _activeTasks, _historyTasks);
                    AnimateRemoval(sender, () => MoveToHistory(task));
                }
                else
                {
                    MoveToHistory(task);
                }
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Paused)
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

        private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            _outputTabManager.AppendOutput(task.Id, "\n[UnityAgent] Task removed.\n", _activeTasks, _historyTasks);
            AnimateRemoval(el, () =>
            {
                _outputTabManager.CloseTab(task);
                _historyTasks.Remove(task);
                _historyManager.SaveHistory(_historyTasks);
                RefreshFilterCombos();
                _outputTabManager.UpdateOutputTabWidths();
                UpdateStatus();
            });
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
            _activeTasks.Insert(0, task);
            UpdateStatus();
        }

        // ── Stored Tasks ──────────────────────────────────────────

        private void PlanOnlyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ExecuteButton == null) return;
            UpdateExecuteButtonText();
        }

        private void RemoveStoredTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            AnimateRemoval(el, () =>
            {
                _storedTasks.Remove(task);
                _historyManager.SaveStoredTasks(_storedTasks);
                RefreshFilterCombos();
                UpdateStatus();
            });
        }

        private void ClearStoredTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_storedTasks.Count == 0) return;
            if (!DarkDialog.ShowConfirm(
                $"Are you sure you want to clear all {_storedTasks.Count} stored tasks? This cannot be undone.",
                "Clear Stored Tasks")) return;

            _storedTasks.Clear();
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void CopyStoredPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!string.IsNullOrEmpty(task.StoredPrompt))
                Clipboard.SetText(task.StoredPrompt);
        }

        private void ExecuteStoredTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (string.IsNullOrEmpty(task.StoredPrompt)) return;

            // Create a new task using the stored prompt as the description
            var newTask = TaskLauncher.CreateTask(
                task.StoredPrompt,
                task.ProjectPath,
                true,  // skipPermissions
                false, // remoteSession
                false, // headless
                false, // isOvernight
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true);
            newTask.ProjectColor = _projectManager.GetProjectColor(newTask.ProjectPath);
            newTask.Summary = $"Executing stored plan: {task.ShortDescription}";

            AddActiveTask(newTask);
            _outputTabManager.CreateTab(newTask);
            _outputTabManager.AppendOutput(newTask.Id,
                $"[UnityAgent] Executing stored plan from task #{task.Id}\n",
                _activeTasks, _historyTasks);
            _taskExecutionManager.StartProcess(newTask, _activeTasks, _historyTasks, MoveToHistory);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void ViewStoredTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            StoredTaskViewerDialog.Show(task);
        }

        private void StoredFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_storedView == null || StoredFilterCombo.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag as string ?? "";
            _storedView.Filter = string.IsNullOrEmpty(tag)
                ? null
                : new Predicate<object>(o => o is AgentTask t && t.ProjectPath == tag);
        }

        private void TaskListTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not TabControl tc) return;
            if (tc.SelectedItem is TabItem tab && tab == StoredTabItem)
            {
                _unviewedStoredCount = 0;
                UpdateStoredTabBadge();
            }
        }

        private void UpdateStoredTabBadge()
        {
            if (StoredTabBadge == null) return;
            if (_unviewedStoredCount > 0)
            {
                StoredTabBadge.Text = $" ({_unviewedStoredCount})";
                StoredTabBadge.Visibility = Visibility.Visible;
            }
            else
            {
                StoredTabBadge.Text = "";
                StoredTabBadge.Visibility = Visibility.Collapsed;
            }
        }

        private static string? ExtractExecutionPrompt(string output)
        {
            const string startMarker = "```EXECUTION_PROMPT";
            const string endMarker = "```";

            var startIdx = output.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (startIdx < 0) return null;

            // Move past the marker line
            startIdx = output.IndexOf('\n', startIdx);
            if (startIdx < 0) return null;
            startIdx++;

            var endIdx = output.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
            if (endIdx < 0) return null;

            return output[startIdx..endIdx].Trim();
        }

        private void CreateStoredTaskFromPlan(AgentTask planTask)
        {
            var output = planTask.OutputBuilder.ToString();
            var extractedPrompt = ExtractExecutionPrompt(output);

            // Fall back to using the completion summary or the original description
            var storedPrompt = extractedPrompt ?? planTask.CompletionSummary;
            if (string.IsNullOrWhiteSpace(storedPrompt))
                storedPrompt = planTask.Description;

            var storedTask = new AgentTask
            {
                Description = planTask.Description,
                ProjectPath = planTask.ProjectPath,
                ProjectColor = planTask.ProjectColor,
                StoredPrompt = storedPrompt,
                FullOutput = output,
                SkipPermissions = planTask.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(planTask.Summary)
                ? planTask.Summary : planTask.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            _unviewedStoredCount++;
            UpdateStoredTabBadge();
            RefreshFilterCombos();
        }

        // ── Orchestration ──────────────────────────────────────────

        private void AddActiveTask(AgentTask task)
        {
            task.TaskNumber = _nextTaskNumber;
            _nextTaskNumber = _nextTaskNumber >= 9999 ? 1 : _nextTaskNumber + 1;
            _activeTasks.Insert(0, task);
        }

        private void AnimateRemoval(FrameworkElement sender, Action onComplete)
        {
            // Walk up from the button to find the card Border (the DataTemplate root)
            FrameworkElement? card = sender;
            while (card != null && card is not ContentPresenter)
                card = VisualTreeHelper.GetParent(card) as FrameworkElement;
            if (card is ContentPresenter cp && VisualTreeHelper.GetChildrenCount(cp) > 0)
                card = VisualTreeHelper.GetChild(cp, 0) as FrameworkElement;

            if (card == null) { onComplete(); return; }

            card.IsHitTestVisible = false;

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            var scaleY = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(100),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            card.RenderTransformOrigin = new Point(0.5, 0);
            card.RenderTransform = new ScaleTransform(1, 1);

            scaleY.Completed += (_, _) => onComplete();

            card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }

        private void MoveToHistory(AgentTask task)
        {
            // If this was a plan-only task that completed successfully, create a stored task
            if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                CreateStoredTaskFromPlan(task);

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

                // Check if all dependency tasks are no longer active (running/queued)
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
            AddActiveTask(task);
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
                t.Status is AgentTaskStatus.Running or AgentTaskStatus.Paused);

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
            foreach (var t in _storedTasks)
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

            if (StoredFilterCombo != null)
            {
                var storedSelection = StoredFilterCombo.SelectedItem as ComboBoxItem;
                var storedTag = storedSelection?.Tag as string;

                StoredFilterCombo.SelectionChanged -= StoredFilter_Changed;
                StoredFilterCombo.Items.Clear();
                StoredFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
                foreach (var p in projectNames)
                    StoredFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
                found = false;
                if (!string.IsNullOrEmpty(storedTag))
                {
                    foreach (ComboBoxItem item in StoredFilterCombo.Items)
                    {
                        if (item.Tag as string == storedTag) { StoredFilterCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) StoredFilterCombo.SelectedIndex = 0;
                StoredFilterCombo.SelectionChanged += StoredFilter_Changed;
            }

            RefreshStatusFilterCombos();
        }

        private void RefreshStatusFilterCombos()
        {
            if (ActiveStatusFilterCombo == null || HistoryStatusFilterCombo == null) return;

            var statusOptions = new[] { "All Status", "Running", "Queued", "Completed", "Failed", "Cancelled" };

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

        // ── Settings Panel Collapse ───────────────────────────────

        private void ToggleSettingsPanel_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = SettingsExpandedPanel.Visibility == Visibility.Visible;
            ApplySettingsPanelCollapsed(collapse);
            _settingsManager.SettingsPanelCollapsed = collapse;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        private void ApplySettingsPanelCollapsed(bool collapsed)
        {
            if (collapsed)
            {
                SettingsExpandedPanel.Visibility = Visibility.Collapsed;
                RightSplitter.Visibility = Visibility.Collapsed;
                SettingsCollapsedStrip.Visibility = Visibility.Visible;
                RightPanelCol.Width = new GridLength(0, GridUnitType.Auto);
                RightPanelCol.MinWidth = 0;
            }
            else
            {
                SettingsCollapsedStrip.Visibility = Visibility.Collapsed;
                SettingsExpandedPanel.Visibility = Visibility.Visible;
                RightSplitter.Visibility = Visibility.Visible;
                RightPanelCol.Width = new GridLength(285);
                RightPanelCol.MinWidth = 150;
            }
        }

        // ── Helper ─────────────────────────────────────────────────

        private async void GenerateSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (_helperManager.IsGenerating) return;

            var category = SuggestionCategory.General;
            if (HelperCategoryCombo.SelectedItem is ComboBoxItem catItem)
            {
                var tag = catItem.Tag?.ToString() ?? "General";
                Enum.TryParse(tag, out category);
            }

            await _helperManager.GenerateSuggestionsAsync(_projectManager.ProjectPath, category);
        }

        private void ClearSuggestions_Click(object sender, RoutedEventArgs e)
        {
            _helperManager.ClearSuggestions();
            HelperStatusText.Text = "";
        }

        private void RunSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;

            var desc = $"{suggestion.Title}\n\n{suggestion.Description}";

            var task = TaskLauncher.CreateTask(
                desc,
                _projectManager.ProjectPath,
                true,
                false,
                false,
                false,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                noGitWrite: DefaultNoGitWriteToggle.IsChecked == true);
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.Summary = suggestion.Title;

            AddActiveTask(task);
            _outputTabManager.CreateTab(task);
            _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);

            RefreshFilterCombos();
            UpdateStatus();
        }

        private void RemoveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            _helperManager.RemoveSuggestion(suggestion);
        }

        private void CopySuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            var text = $"{suggestion.Title}\n\n{suggestion.Description}";
            Clipboard.SetText(text);
        }

        private void SaveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            var text = $"{suggestion.Title}\n\n{suggestion.Description}";
            var entry = new SavedPromptEntry
            {
                PromptText = text,
                DisplayName = suggestion.Title.Length > 40 ? suggestion.Title.Substring(0, 40) + "..." : suggestion.Title,
            };
            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            RenderSavedPrompts();
        }

        private void OnHelperGenerationStarted()
        {
            Dispatcher.Invoke(() =>
            {
                GenerateSuggestionsBtn.IsEnabled = false;
                GenerateSuggestionsBtn.Content = "Generating...";
                HelperStatusText.Text = "Analyzing project...";
            });
        }

        private void OnHelperGenerationCompleted()
        {
            Dispatcher.Invoke(() =>
            {
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Add Suggestions";
                HelperStatusText.Text = $"{_helperManager.Suggestions.Count} suggestions generated";
            });
        }

        private void OnHelperGenerationFailed(string error)
        {
            Dispatcher.Invoke(() =>
            {
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Add Suggestions";
                HelperStatusText.Text = error;
            });
        }

        // ── Saved Prompts ─────────────────────────────────────────

        private string SavedPromptsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityAgent", "saved_prompts.json");

        private void LoadSavedPrompts()
        {
            try
            {
                if (File.Exists(SavedPromptsFile))
                {
                    var json = File.ReadAllText(SavedPromptsFile);
                    var entries = System.Text.Json.JsonSerializer.Deserialize<List<SavedPromptEntry>>(json);
                    if (entries != null)
                    {
                        _savedPrompts.Clear();
                        _savedPrompts.AddRange(entries);
                    }
                }
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load saved prompts", ex); }
            RenderSavedPrompts();
        }

        private void PersistSavedPrompts()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavedPromptsFile)!);
                var json = System.Text.Json.JsonSerializer.Serialize(_savedPrompts,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SavedPromptsFile, json);
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to persist saved prompts", ex); }
        }

        private void SavePromptEntry_Click(object sender, RoutedEventArgs e)
        {
            var text = TaskInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var modelTag = "ClaudeCode";
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
                modelTag = modelItem.Tag?.ToString() ?? "ClaudeCode";

            var entry = new SavedPromptEntry
            {
                PromptText = text,
                DisplayName = text.Length > 40 ? text.Substring(0, 40) + "..." : text,
                Model = modelTag,
                RemoteSession = RemoteSessionToggle.IsChecked == true,
                Headless = HeadlessToggle.IsChecked == true,
                SpawnTeam = SpawnTeamToggle.IsChecked == true,
                Overnight = OvernightToggle.IsChecked == true,
                ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true,
                PlanOnly = PlanOnlyToggle.IsChecked == true,
                IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true,
                UseMcp = UseMcpToggle.IsChecked == true,
                NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true,
            };

            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            RenderSavedPrompts();
        }

        private void RenderSavedPrompts()
        {
            SavedPromptsPanel.Children.Clear();
            foreach (var entry in _savedPrompts)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 5, 6, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand,
                    Tag = entry.Id,
                };

                card.MouseEnter += (s, _) => card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E2E2E"));
                card.MouseLeave += (s, _) => card.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525"));

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textBlock = new TextBlock
                {
                    Text = entry.DisplayName,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCC")),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = entry.PromptText,
                };
                Grid.SetColumn(textBlock, 0);

                var deleteBtn = new Button
                {
                    Content = "X",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888")),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = entry.Id,
                };
                deleteBtn.MouseEnter += (s, _) => deleteBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E57373"));
                deleteBtn.MouseLeave += (s, _) => deleteBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888"));
                deleteBtn.Click += DeleteSavedPrompt_Click;
                Grid.SetColumn(deleteBtn, 1);

                grid.Children.Add(textBlock);
                grid.Children.Add(deleteBtn);
                card.Child = grid;

                card.MouseLeftButtonDown += LoadSavedPrompt_Click;

                var contextMenu = new ContextMenu();
                var copyItem = new MenuItem { Header = "Copy Prompt" };
                var promptText = entry.PromptText;
                copyItem.Click += (s, _) => Clipboard.SetText(promptText);
                contextMenu.Items.Add(copyItem);
                card.ContextMenu = contextMenu;

                SavedPromptsPanel.Children.Add(card);
            }
        }

        private void LoadSavedPrompt_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Border card || card.Tag is not string id) return;
            var entry = _savedPrompts.FirstOrDefault(p => p.Id == id);
            if (entry == null) return;

            TaskInput.Text = entry.PromptText;

            // Restore model selection
            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (ModelCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == entry.Model)
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            // Restore toggles
            RemoteSessionToggle.IsChecked = entry.RemoteSession;
            HeadlessToggle.IsChecked = entry.Headless;
            SpawnTeamToggle.IsChecked = entry.SpawnTeam;
            OvernightToggle.IsChecked = entry.Overnight;
            ExtendedPlanningToggle.IsChecked = entry.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = entry.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = entry.IgnoreFileLocks;
            UseMcpToggle.IsChecked = entry.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = entry.NoGitWrite;
        }

        private void DeleteSavedPrompt_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button btn || btn.Tag is not string id) return;
            _savedPrompts.RemoveAll(p => p.Id == id);
            PersistSavedPrompts();
            RenderSavedPrompts();
        }

        // ── Status ─────────────────────────────────────────────────

        private void UpdateStatus()
        {
            var running = _activeTasks.Count(t => t.Status == AgentTaskStatus.Running);
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLockManager.LockCount;
            var projectName = Path.GetFileName(_projectManager.ProjectPath);
            StatusText.Text = $"{projectName}  |  Running: {running}  |  Queued: {queued}  |  " +
                              $"Completed: {completed}  |  Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                              $"Locks: {locks}  |  {_projectManager.ProjectPath}";
        }
    }
}
