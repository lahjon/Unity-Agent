using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HappyEngine.Dialogs;
using HappyEngine.Managers;
using HappyEngine.Models;

namespace HappyEngine
{
    public partial class MainWindow : Window, IDisposable, IProjectPanelView
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static readonly System.Net.Http.HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private const string DefaultSystemPrompt = TaskLauncher.DefaultSystemPrompt;
        private string SystemPrompt;

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private readonly ObservableCollection<AgentTask> _storedTasks = new();
        private readonly object _activeTasksLock = new();
        private readonly object _historyTasksLock = new();
        private readonly object _storedTasksLock = new();
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
        private readonly MessageBusManager _messageBusManager;
        private TerminalTabManager? _terminalManager;
        private GeminiService _geminiService = null!;
        private ClaudeService _claudeService = null!;
        private HelperManager _helperManager = null!;
        private ActivityDashboardManager _activityDashboard = null!;
        private readonly TaskGroupTracker _taskGroupTracker;
        private readonly TaskOrchestrator _taskOrchestrator;
        private DispatcherTimer? _helperAnimTimer;
        private int _helperAnimTick;

        // Task numbering (1–9999, resets on app restart)
        private int _nextTaskNumber = 1;

        // Disposal guard
        private bool _disposed;

        // Chat
        private ChatManager _chatManager = null!;


        // Terminal collapse state
        private bool _terminalCollapsed = true;
        private GridLength _terminalExpandedHeight = new(120);


        // IProjectPanelView — expose named XAML controls to ProjectManager
        TextBlock IProjectPanelView.PromptProjectLabel => PromptProjectLabel;
        TextBlock IProjectPanelView.AddProjectPath => AddProjectPath;
        StackPanel IProjectPanelView.ProjectListPanel => ProjectListPanel;
        ToggleButton IProjectPanelView.UseMcpToggle => UseMcpToggle;
        TextBox IProjectPanelView.ShortDescBox => ShortDescBox;
        TextBox IProjectPanelView.LongDescBox => LongDescBox;
        TextBox IProjectPanelView.RuleInstructionBox => RuleInstructionBox;
        ToggleButton IProjectPanelView.EditShortDescToggle => EditShortDescToggle;
        ToggleButton IProjectPanelView.EditLongDescToggle => EditLongDescToggle;
        ToggleButton IProjectPanelView.EditRuleInstructionToggle => EditRuleInstructionToggle;
        ItemsControl IProjectPanelView.ProjectRulesList => ProjectRulesList;
        TextBox IProjectPanelView.CrashLogPathBox => CrashLogPathBox;
        TextBox IProjectPanelView.AppLogPathBox => AppLogPathBox;
        TextBox IProjectPanelView.HangLogPathBox => HangLogPathBox;
        ToggleButton IProjectPanelView.EditCrashLogPathsToggle => EditCrashLogPathsToggle;
        Button IProjectPanelView.RegenerateDescBtn => RegenerateDescBtn;
        Dispatcher IProjectPanelView.ViewDispatcher => Dispatcher;

        public MainWindow()
        {
            InitializeComponent();

            SystemPrompt = DefaultSystemPrompt;

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HappyEngine");
            Directory.CreateDirectory(appDataDir);

            var scriptDir = Path.Combine(appDataDir, "scripts");
            Directory.CreateDirectory(scriptDir);

            // Initialize managers (no file I/O here — async loading happens in Window_Loaded)
            _settingsManager = new SettingsManager(appDataDir);

            _historyManager = new HistoryManager(appDataDir, _historyTasksLock, _storedTasksLock);

            _fileLockManager = new FileLockManager(FileLockBadge, Dispatcher);
            _fileLockManager.QueuedTaskResumed += OnQueuedTaskResumed;

            _outputTabManager = new OutputTabManager(OutputTabs, Dispatcher);
            _outputTabManager.TabCloseRequested += OnTabCloseRequested;
            _outputTabManager.TabStoreRequested += OnTabStoreRequested;
            _outputTabManager.TabResumeRequested += OnTabResumeRequested;
            _outputTabManager.InputSent += OnTabInputSent;

            // ProjectManager needs many UI refs — initialize after InitializeComponent.
            // Use a quick sync peek at settings for the initial project path (tiny file);
            // full async load of all data happens in Window_Loaded.
            _projectManager = new ProjectManager(
                appDataDir,
                PeekInitialProjectPath(appDataDir),
                this);
            _projectManager.McpInvestigationRequested += OnMcpInvestigationRequested;
            _projectManager.ProjectSwapStarted += OnProjectSwapStarted;
            _projectManager.ProjectSwapCompleted += OnProjectSwapCompleted;
            _projectManager.ProjectRenamed += OnProjectRenamed;

            _imageManager = new ImageAttachmentManager(appDataDir, ImageIndicator, ClearImagesBtn);
            _geminiService = new GeminiService(appDataDir);
            _claudeService = new ClaudeService(appDataDir);
            _chatManager = new ChatManager(
                ChatMessagesPanel, ChatScrollViewer, ChatInput, ChatSendBtn,
                ChatModelCombo, _claudeService, _geminiService,
                ChatImagePreview, appDataDir);
            _helperManager = new HelperManager(appDataDir, _projectManager.ProjectPath);
            _helperManager.SetActiveTaskSource(() =>
            {
                lock (_activeTasksLock)
                    return _activeTasks.Where(t => !t.IsFinished).Select(t => t.Description).ToList();
            });
            _helperManager.GenerationStarted += OnHelperGenerationStarted;
            _helperManager.GenerationCompleted += OnHelperGenerationCompleted;
            _helperManager.GenerationFailed += OnHelperGenerationFailed;

            _messageBusManager = new MessageBusManager(Dispatcher);

            _taskExecutionManager = new TaskExecutionManager(
                scriptDir, _fileLockManager, _outputTabManager,
                () => SystemPrompt,
                task => _projectManager.GetProjectDescription(task),
                path => _projectManager.GetProjectRulesBlock(path),
                path => _projectManager.IsGameProject(path),
                _messageBusManager,
                Dispatcher,
                () => _settingsManager.TokenLimitRetryMinutes);
            _taskExecutionManager.TaskCompleted += OnTaskProcessCompleted;
            _taskExecutionManager.SubTaskSpawned += OnSubTaskSpawned;

            _taskOrchestrator = new TaskOrchestrator();
            _taskOrchestrator.TaskReady += OnOrchestratorTaskReady;

            _taskGroupTracker = new TaskGroupTracker();
            _taskGroupTracker.GroupCompleted += OnTaskGroupCompleted;

            _activityDashboard = new ActivityDashboardManager(_activeTasks, _historyTasks, _projectManager.SavedProjects);
            _projectManager.SetTaskCollections(_activeTasks, _historyTasks);

            // Set up collections with cross-thread synchronization
            BindingOperations.EnableCollectionSynchronization(_activeTasks, _activeTasksLock);
            BindingOperations.EnableCollectionSynchronization(_historyTasks, _historyTasksLock);
            BindingOperations.EnableCollectionSynchronization(_storedTasks, _storedTasksLock);
            _activeView = CollectionViewSource.GetDefaultView(_activeTasks);
            _historyView = CollectionViewSource.GetDefaultView(_historyTasks);
            _storedView = CollectionViewSource.GetDefaultView(_storedTasks);
            ActiveTasksList.ItemsSource = _activeView;
            HistoryTasksList.ItemsSource = _historyView;
            StoredTasksList.ItemsSource = _storedView;
            FileLocksListView.ItemsSource = _fileLockManager.FileLocksView;
            SuggestionsListView.ItemsSource = _helperManager.Suggestions;

            _activeTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            _historyTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            _storedTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            UpdateTabCounts();
            InitializeNodeGraphPanel();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Constants.AppConstants.StatusTimerIntervalSeconds) };
            _statusTimer.Tick += (_, _) =>
            {
                App.TraceUi("StatusTimer.Tick");
                // Batch-refresh the active tasks view once instead of firing
                // OnPropertyChanged(TimeInfo) on every individual task.
                // Refresh() triggers a single CollectionChanged-Reset which causes
                // WPF to re-read all bound properties (including TimeInfo) for
                // visible items in one layout pass.
                _activeView?.Refresh();
                App.TraceUi("StatusTimer.CleanupHistory");
                _historyManager.CleanupOldHistory(_historyTasks, _outputTabManager.Tabs, OutputTabs, _outputTabManager.OutputBoxes, _settingsManager.HistoryRetentionHours);
                App.TraceUi("StatusTimer.UpdateTabWidths");
                _outputTabManager.UpdateOutputTabWidths();
                App.TraceUi("StatusTimer.UpdateStatus");
                UpdateStatus();
            };

            Closing += OnWindowClosing;

            _terminalManager = new TerminalTabManager(
                TerminalTabBar, TerminalOutput, TerminalInput,
                TerminalSendBtn, TerminalInterruptBtn, TerminalRootPath,
                Dispatcher, _projectManager.ProjectPath);
            _terminalManager.AddTerminal();

            // Saved prompts loaded async in Window_Loaded

            MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        }

        /// <summary>
        /// Lightweight sync peek at settings + projects files to determine the initial project path.
        /// These files are tiny (a few KB). The full async load happens in Window_Loaded.
        /// </summary>
        private static string PeekInitialProjectPath(string appDataDir)
        {
            string? lastSelected = null;
            var settingsFile = Path.Combine(appDataDir, "settings.json");
            try
            {
                if (File.Exists(settingsFile))
                {
                    var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                        File.ReadAllText(settingsFile));
                    if (dict != null && dict.TryGetValue("selectedProject", out var sp))
                        lastSelected = sp.GetString();
                }
            }
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to read settings.json: {ex.Message}"); }

            var projectsFile = Path.Combine(appDataDir, "projects.json");
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
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to read projects.json: {ex.Message}"); }

            var restored = lastSelected != null && savedProjects.Any(p => p.Path == lastSelected);
            return restored ? lastSelected! :
                   savedProjects.Count > 0 ? savedProjects[0].Path : Directory.GetCurrentDirectory();
        }

        // ── Window Loaded ──────────────────────────────────────────

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", "DWM dark mode attribute failed", ex); }

            await LoadSystemPromptAsync();
            SystemPromptBox.Text = SystemPrompt;

            // Initialize Gemini API key display
            if (_geminiService.ApiKeyDecryptionFailed)
            {
                GeminiApiKeyBox.Text = "";
                GeminiKeyStatus.Text = "API key could not be decrypted — please re-enter";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
            else if (_geminiService.IsConfigured)
            {
                GeminiApiKeyBox.Text = _geminiService.GetMaskedApiKey();
                GeminiKeyStatus.Text = "API key configured";
                GeminiKeyStatus.Foreground = (Brush)FindResource("Success");
            }

            // Initialize Gemini model dropdown
            foreach (var model in Managers.GeminiService.AvailableModels)
                GeminiModelCombo.Items.Add(model);
            GeminiModelCombo.SelectedItem = _geminiService.SelectedModel;

            // Initialize Claude API key display
            if (_claudeService.ApiKeyDecryptionFailed)
            {
                ClaudeApiKeyBox.Text = "";
                ClaudeKeyStatus.Text = "API key could not be decrypted — please re-enter";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
            else if (_claudeService.IsConfigured)
            {
                ClaudeApiKeyBox.Text = _claudeService.GetMaskedApiKey();
                ClaudeKeyStatus.Text = "API key configured";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("Success");
            }

            // Initialize Chat model selector
            _chatManager.PopulateModelCombo();

            // Async data loading — settings, projects, history, stored tasks, saved prompts
            await LoadStartupDataAsync();
            await LoadSavedPromptsAsync();
            await _settingsManager.LoadTemplatesAsync();
            RenderTemplateCombo();

            foreach (ComboBoxItem item in HistoryRetentionCombo.Items)
            {
                if (int.TryParse(item.Tag?.ToString(), out var h) && h == _settingsManager.HistoryRetentionHours)
                {
                    HistoryRetentionCombo.SelectedItem = item;
                    break;
                }
            }

            MaxConcurrentTasksBox.Text = _settingsManager.MaxConcurrentTasks.ToString();
            TokenLimitRetryBox.Text = _settingsManager.TokenLimitRetryMinutes.ToString();

            if (_settingsManager.SettingsPanelCollapsed)
                ApplySettingsPanelCollapsed(true);

            SetupMainTabsOverflow();

            await CheckClaudeCliAsync();
        }

        private async System.Threading.Tasks.Task CheckClaudeCliAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    ShowClaudeNotFoundWarning();
                    return;
                }

                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    ShowClaudeNotFoundWarning();
            }
            catch (Exception)
            {
                ShowClaudeNotFoundWarning();
            }
        }

        private void ShowClaudeNotFoundWarning()
        {
            Dialogs.DarkDialog.ShowAlert(
                "The 'claude' CLI was not found on your system PATH.\n\n" +
                "Tasks will fail until it is installed. To fix this:\n" +
                "1. Install Claude Code:  npm install -g @anthropic-ai/claude-code\n" +
                "2. Ensure the install directory is on your PATH\n" +
                "3. Restart this application",
                "Claude CLI Not Found");
        }

        private async System.Threading.Tasks.Task LoadStartupDataAsync()
        {
            try
            {
                // Load settings, projects, history, and stored tasks off the UI thread
                var settingsTask = _settingsManager.LoadSettingsAsync();
                var projectsTask = _projectManager.LoadProjectsAsync();
                var historyTask = _historyManager.LoadHistoryAsync(_settingsManager.HistoryRetentionHours);
                var storedTask = _historyManager.LoadStoredTasksAsync();

                await System.Threading.Tasks.Task.WhenAll(settingsTask, projectsTask, historyTask, storedTask);

                // Populate collections on the UI thread — pin Row 0 to prevent layout jitter
                var historyItems = historyTask.Result;
                var storedItems = storedTask.Result;

                var topRow = RootGrid.RowDefinitions[0];
                if (topRow.ActualHeight > 0)
                    topRow.Height = new GridLength(topRow.ActualHeight);

                foreach (var item in historyItems)
                    _historyTasks.Add(item);
                foreach (var item in storedItems)
                    _storedTasks.Add(item);

                RestoreStarRow();
                RefreshFilterCombos();
                RefreshActivityDashboard();
                _projectManager.RefreshProjectList(
                    p => _terminalManager?.UpdateWorkingDirectory(p),
                    () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                    SyncSettingsForProject);

                _statusTimer.Start();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Warn("MainWindow", "Failed during async startup loading", ex);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        // ── System Prompt ──────────────────────────────────────────

        private string SystemPromptFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappyEngine", "system_prompt.txt");

        private async System.Threading.Tasks.Task LoadSystemPromptAsync()
        {
            try
            {
                if (File.Exists(SystemPromptFile))
                {
                    var text = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(SystemPromptFile));
                    if (text.Contains("# MCP VERIFICATION"))
                    {
                        text = text.Replace(TaskLauncher.McpPromptBlock, "");
                        var cleanedText = text;
                        var path = SystemPromptFile;
                        Managers.SafeFileWriter.WriteInBackground(path, cleanedText, "MainWindow");
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
            SystemPrompt = SystemPromptBox.Text;
            var content = SystemPrompt;
            var path = SystemPromptFile;
            Managers.SafeFileWriter.WriteInBackground(path, content, "MainWindow");
            EditSystemPromptToggle.IsChecked = false;
        }

        private void ResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            var path = SystemPromptFile;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to delete system prompt file", ex); }
            }, System.Threading.CancellationToken.None);
            SystemPrompt = DefaultSystemPrompt;
            SystemPromptBox.Text = SystemPrompt;
            EditSystemPromptToggle.IsChecked = false;
        }

        // ── Settings Sync ──────────────────────────────────────────

        private async void SyncSettingsForProject()
        {
            try
            {
                App.TraceUi("SyncSettingsForProject");

                // Pin Row 0 to prevent prompt panel resize jitter during layout changes
                var topRow = RootGrid.RowDefinitions[0];
                if (topRow.ActualHeight > 0)
                    topRow.Height = new GridLength(topRow.ActualHeight);

                _projectManager.RefreshProjectCombo();
                _projectManager.RefreshProjectList(
                    p => _terminalManager?.UpdateWorkingDirectory(p),
                    () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                    SyncSettingsForProject);
                _projectManager.UpdateMcpToggleForProject();
                var activeEntry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
                ProjectTypeGameToggle.IsChecked = activeEntry?.IsGame == true;
                UpdateMcpVisibility(activeEntry?.IsGame == true);
                SyncMcpSettingsFields();
                await _helperManager.SwitchProjectAsync(_projectManager.ProjectPath);
                RefreshFilterCombos();
                ActiveFilter_Changed(ActiveFilterCombo, null!);
                HistoryFilter_Changed(HistoryFilterCombo, null!);

                if (EditSystemPromptToggle.IsChecked == true)
                {
                    EditSystemPromptToggle.IsChecked = false;
                    SystemPromptBox.Text = SystemPrompt;
                }

                UpdateStatus();
                RestoreStarRow();
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

        private void EditRuleInstructionToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditRuleInstructionToggle.IsChecked == true;
            RuleInstructionBox.IsReadOnly = !editing;
            RuleInstructionBox.Opacity = editing ? 1.0 : 0.6;
            RuleInstructionEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveRuleInstruction_Click(object sender, RoutedEventArgs e) => _projectManager.SaveRuleInstruction();

        private void EditCrashLogPathsToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditCrashLogPathsToggle.IsChecked == true;
            CrashLogPathBox.IsReadOnly = !editing;
            CrashLogPathBox.Opacity = editing ? 1.0 : 0.6;
            AppLogPathBox.IsReadOnly = !editing;
            AppLogPathBox.Opacity = editing ? 1.0 : 0.6;
            HangLogPathBox.IsReadOnly = !editing;
            HangLogPathBox.Opacity = editing ? 1.0 : 0.6;
            CrashLogPathsEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveCrashLogPaths_Click(object sender, RoutedEventArgs e)
        {
            _projectManager.SaveCrashLogPaths(
                CrashLogPathBox.Text.Trim(),
                AppLogPathBox.Text.Trim(),
                HangLogPathBox.Text.Trim());
        }

        private void ResetCrashLogPaths_Click(object sender, RoutedEventArgs e)
        {
            CrashLogPathBox.Text = ProjectManager.GetDefaultCrashLogPath();
            AppLogPathBox.Text = ProjectManager.GetDefaultAppLogPath();
            HangLogPathBox.Text = ProjectManager.GetDefaultHangLogPath();
        }

        private void ProjectTypeGameToggle_Changed(object sender, RoutedEventArgs e)
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry != null)
            {
                entry.IsGame = ProjectTypeGameToggle.IsChecked == true;
                _projectManager.SaveProjects();
                UpdateMcpVisibility(entry.IsGame);
            }
        }

        private void UpdateMcpVisibility(bool isGame)
        {
            var vis = isGame ? Visibility.Visible : Visibility.Collapsed;
            McpTabItem.Visibility = vis;
            UseMcpToggle.Visibility = vis;
            if (!isGame)
                UseMcpToggle.IsChecked = false;
        }

        // ── MCP Settings ────────────────────────────────────────────

        private void SyncMcpSettingsFields()
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry != null)
            {
                McpServerNameBox.Text = entry.McpServerName;
                McpAddressBox.Text = entry.McpAddress;
                McpStartCommandBox.Text = entry.McpStartCommand;
                McpConnectionStatus.Text = entry.McpStatus switch
                {
                    Models.McpStatus.Enabled => "Connected",
                    Models.McpStatus.Initialized => "Initialized",
                    Models.McpStatus.Investigating => "Investigating...",
                    _ => "Disconnected"
                };
                McpConnectionStatus.Foreground = entry.McpStatus switch
                {
                    Models.McpStatus.Enabled => FindResource("Success") as System.Windows.Media.Brush,
                    Models.McpStatus.Investigating => FindResource("WarningOrange") as System.Windows.Media.Brush,
                    _ => FindResource("TextMuted") as System.Windows.Media.Brush
                } ?? FindResource("TextMuted") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
            }
        }

        private void McpSettings_Changed(object sender, RoutedEventArgs e)
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry == null) return;
            entry.McpServerName = McpServerNameBox.Text?.Trim() ?? "mcp-for-unity-server";
            entry.McpAddress = McpAddressBox.Text?.Trim() ?? "http://127.0.0.1:8080/mcp";
            entry.McpStartCommand = McpStartCommandBox.Text?.Trim() ?? "";
            _projectManager.SaveProjects();
        }

        private async void McpTestConnection_Click(object sender, RoutedEventArgs e)
        {
            McpConnectionStatus.Text = "Testing...";
            McpConnectionStatus.Foreground = FindResource("TextMuted") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Gray;
            McpTestConnectionBtn.IsEnabled = false;
            try
            {
                var address = McpAddressBox.Text?.Trim();
                if (string.IsNullOrEmpty(address)) { McpConnectionStatus.Text = "No address"; return; }
                var response = await SharedHttpClient.GetAsync(address);
                if (response.IsSuccessStatusCode)
                {
                    McpConnectionStatus.Text = "Connected";
                    McpConnectionStatus.Foreground = FindResource("Success") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Green;
                }
                else
                {
                    McpConnectionStatus.Text = $"Error: {(int)response.StatusCode}";
                    McpConnectionStatus.Foreground = FindResource("Danger") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Red;
                }
            }
            catch
            {
                McpConnectionStatus.Text = "Unreachable";
                McpConnectionStatus.Foreground = FindResource("Danger") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Red;
            }
            finally
            {
                McpTestConnectionBtn.IsEnabled = true;
            }
        }

        private void AddProjectRule_Click(object sender, RoutedEventArgs e)
        {
            var rule = NewRuleInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(rule)) return;
            _projectManager.AddProjectRule(rule);
            NewRuleInput.Clear();
        }

        private void NewRuleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
            {
                AddProjectRule_Click(sender, e);
                e.Handled = true;
            }
        }

        private void RemoveProjectRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string rule)
                _projectManager.RemoveProjectRule(rule);
        }

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

        private bool _advancedPanelOpen;
        private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
        {
            _advancedPanelOpen = !_advancedPanelOpen;
            AdvancedSidePanel.Visibility = _advancedPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            AdvancedArrow.Text = _advancedPanelOpen ? "\u25BE" : "\u25C2";
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

        private void MaxConcurrentTasks_Changed(object sender, RoutedEventArgs e)
        {
            ApplyMaxConcurrentTasks();
        }

        private void MaxConcurrentTasks_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) ApplyMaxConcurrentTasks();
        }

        private void ApplyMaxConcurrentTasks()
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (int.TryParse(MaxConcurrentTasksBox.Text?.Trim(), out var val) && val >= 1)
            {
                _settingsManager.MaxConcurrentTasks = val;
                MaxConcurrentTasksBox.Text = _settingsManager.MaxConcurrentTasks.ToString();
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
                DrainInitQueue();
            }
            else
            {
                MaxConcurrentTasksBox.Text = _settingsManager.MaxConcurrentTasks.ToString();
            }
        }

        private void TokenLimitRetry_Changed(object sender, RoutedEventArgs e)
        {
            ApplyTokenLimitRetry();
        }

        private void TokenLimitRetry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) ApplyTokenLimitRetry();
        }

        private void ApplyTokenLimitRetry()
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (int.TryParse(TokenLimitRetryBox.Text?.Trim(), out var val) && val >= 1)
            {
                _settingsManager.TokenLimitRetryMinutes = val;
                TokenLimitRetryBox.Text = _settingsManager.TokenLimitRetryMinutes.ToString();
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
            else
            {
                TokenLimitRetryBox.Text = _settingsManager.TokenLimitRetryMinutes.ToString();
            }
        }

        private void ViewLogs_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.LogViewerDialog.Show();
        }

        private void ViewActivity_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.ActivityDashboardDialog.Show(_activeTasks, _historyTasks, _projectManager.SavedProjects);
        }

        private void ClearIgnoredSuggestions_Click(object sender, RoutedEventArgs e)
        {
            var count = _helperManager.IgnoredCount;
            if (count == 0)
            {
                DarkDialog.ShowAlert("There are no ignored suggestions to clear.", "No Ignored Suggestions");
                return;
            }
            if (!DarkDialog.ShowConfirm(
                $"This will clear {count} ignored suggestion(s).\n\nPreviously ignored suggestions may reappear on the next generation. Continue?",
                "Clear Ignored Suggestions")) return;
            _helperManager.ClearIgnoredTitles();
        }

        private void InitializeNodeGraphPanel()
        {
            NodeGraphPanel.Initialize(_activeTasks, _fileLockManager);
            NodeGraphPanel.ShowOutputRequested += task =>
            {
                if (_outputTabManager.HasTab(task.Id))
                    OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
            };
            NodeGraphPanel.CancelRequested += task => CancelTask(task);
            NodeGraphPanel.PauseResumeRequested += task =>
            {
                if (task.Status == AgentTaskStatus.Paused)
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                else if (task.IsRunning)
                {
                    _taskExecutionManager.PauseTask(task);
                    _outputTabManager.AppendOutput(task.Id, "\n[HappyEngine] Task paused.\n", _activeTasks, _historyTasks);
                }
            };
            NodeGraphPanel.CopyPromptRequested += task =>
            {
                if (!string.IsNullOrEmpty(task.Description))
                    Clipboard.SetText(task.Description);
            };
            NodeGraphPanel.RevertRequested += task => RevertTaskFromGraph(task);
            NodeGraphPanel.ForceStartRequested += task =>
            {
                if (task.Status == AgentTaskStatus.InitQueued)
                {
                    task.Status = AgentTaskStatus.Running;
                    task.QueuedReason = null;
                    task.StartTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (limit bypassed)...\n\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                    UpdateStatus();
                }
                else if (task.Status == AgentTaskStatus.Queued)
                {
                    if (task.DependencyTaskIdCount > 0)
                    {
                        _taskOrchestrator.MarkResolved(task.Id);
                        task.QueuedReason = null;
                        task.BlockedByTaskId = null;
                        task.BlockedByTaskNumber = null;
                        task.ClearDependencyTaskIds();
                        task.DependencyTaskNumbers.Clear();

                        if (task.Process is { HasExited: false })
                        {
                            _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                            _outputTabManager.AppendOutput(task.Id,
                                $"\n[HappyEngine] Force-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                                _activeTasks, _historyTasks);
                        }
                        else
                        {
                            task.Status = AgentTaskStatus.Running;
                            task.StartTime = DateTime.Now;
                            _outputTabManager.AppendOutput(task.Id,
                                $"\n[HappyEngine] Force-starting task #{task.TaskNumber} (dependencies skipped)...\n\n",
                                _activeTasks, _historyTasks);
                            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                        }
                    }
                    else
                    {
                        _fileLockManager.ForceStartQueuedTask(task);
                    }
                    _outputTabManager.UpdateTabHeader(task);
                    UpdateStatus();
                }
            };
            NodeGraphPanel.DependencyCreated += (source, target) =>
            {
                // Inverted: the drag source (start node) becomes queued, target (end node) is the prerequisite
                var dependent = source;
                var prerequisite = target;

                if (dependent.ContainsDependencyTaskId(prerequisite.Id)) return;
                if (_taskOrchestrator.ContainsTask(dependent.Id) && _taskOrchestrator.ContainsTask(prerequisite.Id))
                {
                    if (_taskOrchestrator.DetectCycle(dependent.Id, prerequisite.Id)) return;
                }
                else if (WouldCreateCircularDependency(dependent, prerequisite)) return;

                dependent.AddDependencyTaskId(prerequisite.Id);
                dependent.DependencyTaskNumbers.Add(prerequisite.TaskNumber);
                _taskOrchestrator.AddTask(dependent, new List<string> { prerequisite.Id });
                dependent.BlockedByTaskId = prerequisite.Id;
                dependent.BlockedByTaskNumber = prerequisite.TaskNumber;
                dependent.QueuedReason = $"Waiting for #{prerequisite.TaskNumber} to complete";

                if (dependent.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning)
                {
                    _taskExecutionManager.PauseTask(dependent);
                    dependent.Status = AgentTaskStatus.Queued;
                }
                else if (dependent.Status is AgentTaskStatus.Paused)
                {
                    dependent.Status = AgentTaskStatus.Queued;
                }
                else if (dependent.Status is AgentTaskStatus.InitQueued)
                {
                    dependent.Status = AgentTaskStatus.Queued;
                }

                _outputTabManager.AppendOutput(dependent.Id,
                    $"\n[HappyEngine] Task #{dependent.TaskNumber} queued — waiting for #{prerequisite.TaskNumber} to complete.\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(dependent);
                UpdateStatus();
            };
            NodeGraphPanel.DependenciesRemoved += task =>
            {
                // Remove this task's own dependencies
                if (task.DependencyTaskIdCount > 0)
                {
                    _taskOrchestrator.MarkResolved(task.Id);
                    task.ClearDependencyTaskIds();
                    task.DependencyTaskNumbers.Clear();
                    task.BlockedByTaskId = null;
                    task.BlockedByTaskNumber = null;
                    task.QueuedReason = null;

                    if (task.Status == AgentTaskStatus.Queued)
                    {
                        if (task.Process is { HasExited: false })
                        {
                            _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                            _outputTabManager.AppendOutput(task.Id,
                                $"\n[HappyEngine] Dependencies removed — resuming task #{task.TaskNumber}.\n",
                                _activeTasks, _historyTasks);
                        }
                        else
                        {
                            task.Status = AgentTaskStatus.Running;
                            task.StartTime = DateTime.Now;
                            _outputTabManager.AppendOutput(task.Id,
                                $"\n[HappyEngine] Dependencies removed — starting task #{task.TaskNumber}...\n",
                                _activeTasks, _historyTasks);
                            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                        }
                    }
                }

                // Remove this task from other tasks' dependency lists
                foreach (var other in _activeTasks)
                {
                    if (other.Id == task.Id) continue;
                    if (other.RemoveDependencyTaskId(task.Id))
                    {
                        other.DependencyTaskNumbers.Remove(task.TaskNumber);
                        if (other.BlockedByTaskId == task.Id)
                        {
                            other.BlockedByTaskId = null;
                            other.BlockedByTaskNumber = null;
                        }
                        if (other.DependencyTaskIdCount == 0)
                        {
                            other.QueuedReason = null;
                        }
                    }
                }

                _outputTabManager.UpdateTabHeader(task);
                UpdateStatus();
            };
        }

        private async void RevertTaskFromGraph(AgentTask task)
        {
            if (string.IsNullOrEmpty(task.GitStartHash))
            {
                Dialogs.DarkDialog.ShowAlert("No git snapshot was captured when this task started.\nRevert is not available.", "Revert Unavailable");
                return;
            }
            if (!Directory.Exists(task.ProjectPath))
            {
                Dialogs.DarkDialog.ShowAlert("The project path for this task no longer exists.", "Revert Unavailable");
                return;
            }
            var shortHash = task.GitStartHash[..Math.Min(8, task.GitStartHash.Length)];
            if (!Dialogs.DarkDialog.ShowConfirm(
                $"This will run 'git reset --hard {shortHash}' in:\n{task.ProjectPath}\n\nAll uncommitted changes will be lost. Continue?",
                "Revert Task Changes"))
                return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", $"reset --hard {task.GitStartHash}")
                {
                    WorkingDirectory = task.ProjectPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode == 0)
                    {
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[HappyEngine] Reverted to commit {shortHash}.\n", _activeTasks, _historyTasks);
                        Dialogs.DarkDialog.ShowAlert($"Successfully reverted to commit {shortHash}.", "Revert Complete");
                    }
                    else
                    {
                        Dialogs.DarkDialog.ShowAlert("Git reset failed. The commit may no longer exist or the repository state may have changed.", "Revert Failed");
                    }
                }
            }
            catch (Exception ex)
            {
                Dialogs.DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
        }

        // ── Gemini Settings ─────────────────────────────────────────

        private void SaveGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            var key = GeminiApiKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key) || key.Contains('*'))
            {
                GeminiKeyStatus.Text = "Enter a valid API key";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }
            if (!Managers.GeminiService.IsValidApiKeyFormat(key))
            {
                GeminiKeyStatus.Text = "Invalid key format — expected 39 chars starting with 'AIza'";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }

            try
            {
                _geminiService.SaveApiKey(key);
                GeminiApiKeyBox.Text = _geminiService.GetMaskedApiKey();
                GeminiKeyStatus.Text = "API key saved successfully";
                GeminiKeyStatus.Foreground = (Brush)FindResource("Success");
                _chatManager.PopulateModelCombo();
            }
            catch (Exception ex)
            {
                GeminiKeyStatus.Text = $"Error saving key: {ex.Message}";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
        }

        private void GeminiApiLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ai.google.dev/gemini-api/docs/api-key") { UseShellExecute = true });
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

        // ── Claude Settings ─────────────────────────────────────────

        private void SaveClaudeKey_Click(object sender, RoutedEventArgs e)
        {
            var key = ClaudeApiKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key) || key.Contains('*'))
            {
                ClaudeKeyStatus.Text = "Enter a valid API key";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }

            try
            {
                _claudeService.SaveApiKey(key);
                ClaudeApiKeyBox.Text = _claudeService.GetMaskedApiKey();
                ClaudeKeyStatus.Text = "API key saved successfully";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("Success");
                _chatManager.PopulateModelCombo();
            }
            catch (Exception ex)
            {
                ClaudeKeyStatus.Text = $"Error saving key: {ex.Message}";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
        }

        private void ClaudeApiLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true });
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Claude API link", ex); }
        }

        // ── Project Events ─────────────────────────────────────────

        private void AddProjectPath_Click(object sender, MouseButtonEventArgs e) =>
            _projectManager.HandleAddProjectPathClick(
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
            var tag = item.Tag?.ToString();
            var isGemini = tag == "Gemini" || tag == "GeminiGameArt";
            var isGameArt = tag == "GeminiGameArt";
            // Disable advanced options that don't apply to Gemini
            if (RemoteSessionToggle != null) RemoteSessionToggle.IsEnabled = !isGemini;
            if (OvernightToggle != null) OvernightToggle.IsEnabled = !isGemini;
            if (SpawnTeamToggle != null) SpawnTeamToggle.IsEnabled = !isGemini;
            if (ExtendedPlanningToggle != null) ExtendedPlanningToggle.IsEnabled = !isGemini;
            if (AutoDecomposeToggle != null) AutoDecomposeToggle.IsEnabled = !isGemini;
            // Show/hide asset type selector for Game Art mode
            if (AssetTypeLabel != null) AssetTypeLabel.Visibility = isGameArt ? Visibility.Visible : Visibility.Collapsed;
            if (AssetTypeCombo != null) AssetTypeCombo.Visibility = isGameArt ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Input Events ───────────────────────────────────────────

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SavePromptEntry_Click(sender, e);
                e.Handled = true;
            }
        }

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

        private void ClearImages_Click(object sender, RoutedEventArgs e)
        {
            var failures = _imageManager.ClearImages();
            if (failures > 0)
                Managers.AppLogger.Warn("MainWindow", $"{failures} image file(s) could not be deleted from disk");
        }

        private void ClearPrompt_Click(object sender, RoutedEventArgs e)
        {
            TaskInput.Clear();
            AdditionalInstructionsInput.Clear();
        }

        // ── Dependencies ────────────────────────────────────────────

        private readonly List<AgentTask> _pendingDependencies = new();
        private Point? _dragStartPoint;

        private void TaskCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void TaskCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragStartPoint == null) return;
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.IsFinished) return;

            var pos = e.GetPosition(null);
            var diff = pos - _dragStartPoint.Value;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragStartPoint = null;
            var data = new DataObject("AgentTask", task);
            DragDrop.DoDragDrop(el, data, DragDropEffects.Link | DragDropEffects.Move);
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
                    Background = (Brush)FindResource("BgCard"),
                    BorderBrush = (Brush)FindResource("SeparatorDark"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 4, 2),
                    Margin = new Thickness(0, 0, 4, 2)
                };

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                panel.Children.Add(new TextBlock
                {
                    Text = $"#{dep.Id}",
                    Foreground = (Brush)FindResource("WarningYellow"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });

                panel.Children.Add(new TextBlock
                {
                    Text = dep.ShortDescription,
                    Foreground = (Brush)FindResource("TextTabHeader"),
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
                    Foreground = (Brush)FindResource("TextSubdued"),
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

        // ── Task-to-Task Drag & Drop (add dependency) ─────────────

        private void TaskCard_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("AgentTask") ||
                sender is not FrameworkElement { DataContext: AgentTask target } ||
                e.Data.GetData("AgentTask") is not AgentTask dragged ||
                dragged.Id == target.Id || target.IsFinished || dragged.IsFinished)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Reorder mode when both tasks are queued
            e.Effects = IsReorderablePair(dragged, target) ? DragDropEffects.Move : DragDropEffects.Link;
            e.Handled = true;
        }

        private static bool IsReorderablePair(AgentTask a, AgentTask b) =>
            (a.IsQueued || a.IsInitQueued) && (b.IsQueued || b.IsInitQueued);

        private void TaskCard_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("AgentTask")) return;
            if (sender is not FrameworkElement { DataContext: AgentTask target }) return;
            if (e.Data.GetData("AgentTask") is not AgentTask dragged) return;
            if (dragged.Id == target.Id || target.IsFinished || dragged.IsFinished) return;

            // Reorder queued tasks instead of adding dependency
            if (IsReorderablePair(dragged, target))
            {
                ReorderQueuedTask(dragged, target);
                e.Handled = true;
                return;
            }

            // Prevent duplicate dependency
            if (dragged.ContainsDependencyTaskId(target.Id)) return;

            // Prevent circular dependency (use orchestrator if both tracked, else local check)
            if (_taskOrchestrator.ContainsTask(dragged.Id) && _taskOrchestrator.ContainsTask(target.Id))
            {
                if (_taskOrchestrator.DetectCycle(dragged.Id, target.Id)) return;
            }
            else if (WouldCreateCircularDependency(dragged, target)) return;

            // Add dependency: dragged waits for target — register with orchestrator
            dragged.AddDependencyTaskId(target.Id);
            dragged.DependencyTaskNumbers.Add(target.TaskNumber);
            _taskOrchestrator.AddTask(dragged, new List<string> { target.Id });
            dragged.BlockedByTaskId = target.Id;
            dragged.BlockedByTaskNumber = target.TaskNumber;
            dragged.QueuedReason = $"Waiting for #{target.TaskNumber} to complete";

            // Suspend the dragged task's process if it's currently running
            if (dragged.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning)
            {
                _taskExecutionManager.PauseTask(dragged);
                dragged.Status = AgentTaskStatus.Queued; // override Paused → Queued
            }
            else if (dragged.Status is AgentTaskStatus.Paused)
            {
                dragged.Status = AgentTaskStatus.Queued; // process already suspended
            }
            else if (dragged.Status is AgentTaskStatus.InitQueued)
            {
                dragged.Status = AgentTaskStatus.Queued;
            }
            // If already Queued, dependency was added above — status stays Queued

            _outputTabManager.AppendOutput(dragged.Id,
                $"\n[HappyEngine] Task #{dragged.TaskNumber} queued — waiting for #{target.TaskNumber} to complete.\n",
                _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(dragged);
            UpdateStatus();
            e.Handled = true;
        }

        /// <summary>
        /// Moves the dragged task to the target task's position in the active list,
        /// then recalculates Priority values so visual order matches execution order.
        /// </summary>
        private void ReorderQueuedTask(AgentTask dragged, AgentTask target)
        {
            var dragIdx = _activeTasks.IndexOf(dragged);
            var targetIdx = _activeTasks.IndexOf(target);
            if (dragIdx < 0 || targetIdx < 0 || dragIdx == targetIdx) return;

            _activeTasks.Move(dragIdx, targetIdx);
            RecalculateQueuePriorities();
        }

        /// <summary>
        /// Assigns descending Priority values to all Queued/InitQueued tasks.
        /// PriorityLevel is the primary sort key (Critical > High > Normal > Low),
        /// then position in the active list (earlier = higher priority).
        /// </summary>
        private void RecalculateQueuePriorities()
        {
            var queuedTasks = _activeTasks
                .Where(t => t.IsQueued || t.IsInitQueued)
                .ToList();

            // Sort by PriorityLevel descending (Critical=3, High=2, Normal=1, Low=0),
            // then by original list position (stable sort preserves relative order).
            var sorted = queuedTasks
                .Select((t, i) => (Task: t, Index: i))
                .OrderByDescending(x => (int)x.Task.PriorityLevel)
                .ThenBy(x => x.Index)
                .Select(x => x.Task)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].Priority = sorted.Count - i;
        }

        private bool WouldCreateCircularDependency(AgentTask dragged, AgentTask target)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(target.Id);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (id == dragged.Id) return true;
                if (!visited.Add(id)) continue;

                var t = _activeTasks.FirstOrDefault(x => x.Id == id);
                if (t == null) continue;
                foreach (var depId in t.DependencyTaskIds)
                    queue.Enqueue(depId);
            }

            return false;
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

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem && modelItem.Tag?.ToString() == "Gemini")
                selectedModel = ModelType.Gemini;

            // Create a new task using the stored prompt as the description
            var newTask = TaskLauncher.CreateTask(
                task.StoredPrompt,
                task.ProjectPath,
                true,
                RemoteSessionToggle.IsChecked == true,
                false,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                PlanOnlyToggle.IsChecked == true,
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel,
                autoDecompose: AutoDecomposeToggle.IsChecked == true);
            newTask.ProjectColor = _projectManager.GetProjectColor(newTask.ProjectPath);
            newTask.ProjectDisplayName = _projectManager.GetProjectDisplayName(newTask.ProjectPath);
            newTask.Summary = $"Executing stored plan: {task.ShortDescription}";

            // Remove the stored task from the list after extracting its data
            _storedTasks.Remove(task);
            _historyManager.SaveStoredTasks(_storedTasks);

            ResetPerTaskToggles();

            if (selectedModel == ModelType.Gemini)
            {
                ExecuteGeminiTask(newTask);
                RefreshFilterCombos();
                UpdateStatus();
                return;
            }

            if (newTask.Headless)
            {
                _taskExecutionManager.LaunchHeadless(newTask);
                UpdateStatus();
                return;
            }

            AddActiveTask(newTask);
            _outputTabManager.CreateTab(newTask);
            _outputTabManager.AppendOutput(newTask.Id,
                $"[HappyEngine] Executing stored plan from task #{task.TaskNumber}\n",
                _activeTasks, _historyTasks);
            _ = _taskExecutionManager.StartProcess(newTask, _activeTasks, _historyTasks, MoveToHistory);
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
        }

        private void UpdateTabCounts()
        {
            ActiveTabCount.Text = $" ({_activeTasks.Count})";
            HistoryTabCount.Text = $" ({_historyTasks.Count})";
            if (StoredTabBadge != null)
            {
                StoredTabBadge.Text = $" ({_storedTasks.Count})";
                StoredTabBadge.Visibility = Visibility.Visible;
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
                ProjectDisplayName = planTask.ProjectDisplayName,
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
            RefreshFilterCombos();
        }

        // ── Scroll Fix ────────────────────────────────────────────

        private void TaskList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            var sv = FindVisualChild<ScrollViewer>(listBox);
            if (sv == null) return;
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
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

        private void ActiveTaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ActiveTasksList.SelectedItem is AgentTask task)
                NodeGraphPanel.FocusOnTask(task.Id);
        }

        // ── Named handlers for anonymous lambdas (needed for cleanup) ──

        private void OnProjectSwapStarted() => LoadingOverlay.Visibility = Visibility.Visible;
        private void OnProjectSwapCompleted() => LoadingOverlay.Visibility = Visibility.Collapsed;

        private void OnCollectionChangedUpdateTabs(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateTabCounts();

        // ── Window Close ───────────────────────────────────────────

        /// <summary>
        /// Unsubscribes every event handler subscribed in the constructor / LoadStartupDataAsync,
        /// mirroring the subscription order to prevent memory leaks.
        /// </summary>
        private void UnsubscribeAllEvents()
        {
            // FileLockManager
            _fileLockManager.QueuedTaskResumed -= OnQueuedTaskResumed;

            // OutputTabManager
            _outputTabManager.TabCloseRequested -= OnTabCloseRequested;
            _outputTabManager.TabStoreRequested -= OnTabStoreRequested;
            _outputTabManager.TabResumeRequested -= OnTabResumeRequested;
            _outputTabManager.InputSent -= OnTabInputSent;

            // ProjectManager
            _projectManager.McpInvestigationRequested -= OnMcpInvestigationRequested;
            _projectManager.ProjectSwapStarted -= OnProjectSwapStarted;
            _projectManager.ProjectSwapCompleted -= OnProjectSwapCompleted;
            _projectManager.ProjectRenamed -= OnProjectRenamed;

            // HelperManager
            _helperManager.GenerationStarted -= OnHelperGenerationStarted;
            _helperManager.GenerationCompleted -= OnHelperGenerationCompleted;
            _helperManager.GenerationFailed -= OnHelperGenerationFailed;

            // TaskExecutionManager
            _taskExecutionManager.TaskCompleted -= OnTaskProcessCompleted;
            _taskExecutionManager.SubTaskSpawned -= OnSubTaskSpawned;

            // TaskOrchestrator
            _taskOrchestrator.TaskReady -= OnOrchestratorTaskReady;

            // TaskGroupTracker
            _taskGroupTracker.GroupCompleted -= OnTaskGroupCompleted;

            // CollectionChanged
            _activeTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;
            _historyTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;
            _storedTasks.CollectionChanged -= OnCollectionChangedUpdateTabs;

            // MainTabs
            MainTabs.SelectionChanged -= MainTabs_SelectionChanged;
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            var runningCount = _activeTasks.Count(t =>
                t.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused);

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

            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ── 1. Unsubscribe all event handlers to prevent leaks ──
            UnsubscribeAllEvents();

            // ── 2. Stop all timers so no callbacks fire during teardown ──
            _statusTimer.Stop();
            _helperAnimTimer?.Stop();

            // ── 3. Cancel in-flight async work ──
            _chatManager.CancelAndDispose();

            _helperManager?.Dispose();

            // ── 4. Persist state (queues background writes via SafeFileWriter) ──
            _projectManager.SaveProjects();
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
            _historyManager.SaveHistory(_historyTasks);
            PersistSavedPrompts();

            // ── 5. Wait for all background file writes to complete ──
            Managers.SafeFileWriter.FlushAll(timeoutMs: 5000);

            // ── 6. Cancel CTS, stop overnight timers, kill & dispose processes ──
            foreach (var task in _activeTasks)
            {
                TaskExecutionManager.KillProcess(task);
                task.Runtime.Dispose();
            }

            // ── 7. Clean up remaining state ──
            _messageBusManager.Dispose();
            _fileLockManager.ClearAll();
            _taskExecutionManager.StreamingToolState.Clear();

            _terminalManager?.Dispose();
        }

        // ── Splitter drag (Thumb-based, avoids GridSplitter star-sizing jitter) ──

        private void TopMiddleSplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            // Snapshot star row to pixel so both rows are pixel-based during drag
            var topRow = RootGrid.RowDefinitions[0];
            topRow.Height = new GridLength(topRow.ActualHeight);
        }

        private void TopMiddleSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var topRow = RootGrid.RowDefinitions[0];
            var bottomRow = RootGrid.RowDefinitions[2];

            double newTop = topRow.Height.Value + e.VerticalChange;
            double newBottom = bottomRow.Height.Value - e.VerticalChange;

            if (newTop < topRow.MinHeight || newBottom < bottomRow.MinHeight)
                return;

            topRow.Height = new GridLength(newTop);
            bottomRow.Height = new GridLength(newBottom);
        }

        private void TopMiddleSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            // Restore star sizing so Row 0 flexes with window resizes
            RootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        }

        // ── Right splitter drag (Thumb-based, avoids GridSplitter targeting collapsed Col 4) ──

        private void RightSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double newWidth = RightPanelCol.Width.Value - e.HorizontalChange;
            if (newWidth < RightPanelCol.MinWidth) newWidth = RightPanelCol.MinWidth;
            RightPanelCol.Width = new GridLength(newWidth);
        }

        // ── Chat splitter drag (Thumb-based, avoids GridSplitter star-sizing inversion) ──

        private void ChatSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double newChat = ChatPanelCol.Width.Value - e.HorizontalChange;
            if (newChat < 60) newChat = 60;
            ChatPanelCol.Width = new GridLength(newChat);
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

            // Group filter combo
            if (HistoryGroupFilterCombo != null)
            {
                var groupTag = (HistoryGroupFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

                HistoryGroupFilterCombo.SelectionChanged -= HistoryGroupFilter_Changed;
                HistoryGroupFilterCombo.Items.Clear();
                HistoryGroupFilterCombo.Items.Add(new ComboBoxItem { Content = "All Groups", Tag = "" });

                var groups = _historyTasks.Concat(_activeTasks)
                    .Where(t => !string.IsNullOrEmpty(t.GroupId))
                    .Select(t => new { Id = t.GroupId!, Name = t.GroupName ?? t.GroupId! })
                    .DistinctBy(g => g.Id)
                    .OrderBy(g => g.Name)
                    .ToList();
                foreach (var g in groups)
                    HistoryGroupFilterCombo.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Id });

                found = false;
                if (!string.IsNullOrEmpty(groupTag))
                {
                    foreach (ComboBoxItem item in HistoryGroupFilterCombo.Items)
                    {
                        if (item.Tag as string == groupTag) { HistoryGroupFilterCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) HistoryGroupFilterCombo.SelectedIndex = 0;
                HistoryGroupFilterCombo.SelectionChanged += HistoryGroupFilter_Changed;
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

        private static bool TaskMatchesSearch(AgentTask t, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            if (t.Description != null && t.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Summary != null && t.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void ApplyActiveFilters()
        {
            if (_activeView == null) return;
            var projectTag = (ActiveFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (ActiveStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);
            var searchText = ActiveSearchBox?.Text?.Trim() ?? "";
            var hasSearch = searchText.Length > 0;

            if (!hasProject && !hasStatus && !hasSearch)
                _activeView.Filter = null;
            else
                _activeView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    if (hasSearch && !TaskMatchesSearch(t, searchText)) return false;
                    return true;
                };
        }

        private void ApplyHistoryFilters()
        {
            if (_historyView == null) return;
            var projectTag = (HistoryFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (HistoryStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var groupTag = (HistoryGroupFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);
            var hasGroup = !string.IsNullOrEmpty(groupTag);
            var searchText = HistorySearchBox?.Text?.Trim() ?? "";
            var hasSearch = searchText.Length > 0;

            if (!hasProject && !hasStatus && !hasGroup && !hasSearch)
                _historyView.Filter = null;
            else
                _historyView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    if (hasGroup && t.GroupId != groupTag) return false;
                    if (hasSearch && !TaskMatchesSearch(t, searchText)) return false;
                    return true;
                };
        }

        private void ActiveFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void HistoryGroupFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyActiveFilters();

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilters();

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabs) return;
            if (MainTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent);
        }

        private void SetupMainTabsOverflow()
        {
            MainTabs.ApplyTemplate();
            var btn = MainTabs.Template.FindName("PART_OverflowButton", MainTabs) as Button;
            if (btn != null)
                btn.Click += MainTabsOverflow_Click;
        }

        private void MainTabsOverflow_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = btn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = (Brush)FindResource("BgPopup"),
                BorderBrush = (Brush)FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 130,
                MaxHeight = 350
            };

            var stack = new StackPanel();

            foreach (var item in MainTabs.Items)
            {
                if (item is TabItem tab && tab.Visibility == Visibility.Visible)
                {
                    string text = GetTabHeaderText(tab);
                    bool isSelected = tab == MainTabs.SelectedItem;

                    var itemBorder = new Border
                    {
                        Background = isSelected
                            ? (Brush)FindResource("BgHover")
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
                            ? (Brush)FindResource("Accent")
                            : (Brush)FindResource("TextBody"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal
                    };

                    itemBorder.Child = textBlock;

                    var capturedTab = tab;
                    itemBorder.MouseEnter += (_, _) =>
                    {
                        if (capturedTab != MainTabs.SelectedItem)
                            itemBorder.Background = (Brush)FindResource("BgElevated");
                    };
                    itemBorder.MouseLeave += (_, _) =>
                    {
                        if (capturedTab != MainTabs.SelectedItem)
                            itemBorder.Background = Brushes.Transparent;
                    };
                    itemBorder.MouseLeftButtonDown += (_, _) =>
                    {
                        MainTabs.SelectedItem = capturedTab;
                        popup.IsOpen = false;
                    };

                    stack.Children.Add(itemBorder);
                }
            }

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        private static string GetTabHeaderText(TabItem tab)
        {
            if (tab.Header is TextBlock tb) return tb.Text;
            if (tab.Header is StackPanel sp)
            {
                foreach (var child in sp.Children)
                    if (child is TextBlock t) return t.Text;
            }
            if (tab.Header is string s) return s;
            return "Tab";
        }

        private void RefreshActivityDashboard()
        {
            _activityDashboard.MarkDirty();
            if (MainTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent);
        }

        private void RefreshInlineProjectStats()
        {
            _projectManager.RefreshProjectList(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);
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

        private static string WrapTooltipText(string text, int maxLineLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength)
                return text;

            var sb = new System.Text.StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length > 0) sb.Append('\n');
                var remaining = line;
                while (remaining.Length > maxLineLength)
                {
                    int breakAt = remaining.LastIndexOf(' ', maxLineLength);
                    if (breakAt <= 0) breakAt = maxLineLength;
                    sb.Append(remaining, 0, breakAt);
                    sb.Append('\n');
                    remaining = remaining.Substring(breakAt).TrimStart();
                }
                sb.Append(remaining);
            }
            return sb.ToString();
        }

        // ── Automation ─────────────────────────────────────────────

        private void BuildInvestigation_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = _projectManager.ProjectPath;
            if (string.IsNullOrEmpty(projectPath)) return;

            var crashPaths = _projectManager.GetCrashLogPaths(projectPath);
            var pathsList = string.Join("\n", crashPaths.Select(p => $"- `{p}`"));

            var desc = "Investigate recent build failures and crashes for this project.\n\n" +
                       "## Instructions\n\n" +
                       "1. Read the following crash/error log files and analyze their contents:\n" +
                       $"{pathsList}\n\n" +
                       "2. Identify the root cause of the most recent errors or crashes.\n" +
                       "3. Propose and implement fixes for the issues found.\n" +
                       "4. If a log file does not exist or is empty, note it and continue with the others.\n\n" +
                       "Focus on the most recent entries first. Provide a clear summary of what went wrong and what was fixed.";

            var task = TaskLauncher.CreateTask(
                desc,
                projectPath,
                true,
                RemoteSessionToggle.IsChecked == true,
                false,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                planOnly: false,
                imagePaths: _imageManager.DetachImages());
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);
            task.Summary = "Build Investigation";

            ResetPerTaskToggles();

            if (task.Headless)
            {
                _taskExecutionManager.LaunchHeadless(task);
                UpdateStatus();
                return;
            }

            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
            {
                task.Status = AgentTaskStatus.InitQueued;
                task.QueuedReason = "Max concurrent tasks reached";
                _outputTabManager.AppendOutput(task.Id,
                    $"[HappyEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            RefreshFilterCombos();
            UpdateStatus();
        }

        private async void GenerateSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (_helperManager.IsGenerating) return;

            var category = SuggestionCategory.General;
            if (HelperCategoryCombo.SelectedItem is ComboBoxItem catItem)
            {
                var tag = catItem.Tag?.ToString() ?? "General";
                Enum.TryParse(tag, out category);
            }

            var guidance = SuggestionGuidanceInput.Text?.Trim();
            if (!string.IsNullOrEmpty(guidance))
                SuggestionGuidanceInput.Clear();
            await _helperManager.GenerateSuggestionsAsync(_projectManager.ProjectPath, category, guidance);
        }

        private void SuggestionGuidanceInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                GenerateSuggestions_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ClearSuggestions_Click(object sender, RoutedEventArgs e)
        {
            _helperManager.ClearSuggestions();
            HelperStatusText.Text = "";
        }

        private void RunSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;

            // For Rules category, add to project rules instead of running a task
            if (suggestion.Category == SuggestionCategory.Rules)
            {
                _projectManager.AddProjectRule($"{suggestion.Title}: {suggestion.Description}");
                _helperManager.RemoveSuggestion(suggestion);
                return;
            }

            var desc = $"Implement the following improvement:\n\n" +
                       $"## {suggestion.Title}\n\n" +
                       $"{suggestion.Description}\n\n" +
                       "You MUST fully implement this change — write the actual code, do not just analyze or produce a plan.";

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem && modelItem.Tag?.ToString() == "Gemini")
                selectedModel = ModelType.Gemini;

            // Helper suggestions must always implement — never plan-only
            var task = TaskLauncher.CreateTask(
                desc,
                _projectManager.ProjectPath,
                true,
                RemoteSessionToggle.IsChecked == true,
                false,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                planOnly: false,
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel);
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);
            task.Summary = suggestion.Title;

            ResetPerTaskToggles();

            if (selectedModel == ModelType.Gemini)
            {
                ExecuteGeminiTask(task);
                _helperManager.RemoveSuggestion(suggestion);
                RefreshFilterCombos();
                UpdateStatus();
                return;
            }

            if (task.Headless)
            {
                _taskExecutionManager.LaunchHeadless(task);
                _helperManager.RemoveSuggestion(suggestion);
                UpdateStatus();
                return;
            }

            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            if (CountActiveSessionTasks() >= _settingsManager.MaxConcurrentTasks)
            {
                task.Status = AgentTaskStatus.InitQueued;
                task.QueuedReason = "Max concurrent tasks reached";
                _outputTabManager.AppendOutput(task.Id,
                    $"[HappyEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
            }
            else
            {
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            _helperManager.RemoveSuggestion(suggestion);

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

        private void IgnoreSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            _helperManager.IgnoreSuggestion(suggestion);
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
            _helperManager.RemoveSuggestion(suggestion);
        }

        private static readonly string[] _helperAnimPhases = [
            "Analyzing project",
            "Scanning files",
            "Generating suggestions",
            "Thinking",
        ];

        private void StartHelperAnimation()
        {
            _helperAnimTick = 0;
            _helperAnimTimer?.Stop();
            _helperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _helperAnimTimer.Tick += (_, _) =>
            {
                var dots = new string('.', (_helperAnimTick % 3) + 1);
                var phase = _helperAnimPhases[(_helperAnimTick / 6) % _helperAnimPhases.Length];
                HelperStatusText.Text = phase + dots;
                _helperAnimTick++;
            };
            _helperAnimTimer.Start();
        }

        private void StopHelperAnimation()
        {
            _helperAnimTimer?.Stop();
            _helperAnimTimer = null;
            GenerateSuggestionsBtn.BeginAnimation(OpacityProperty, null);
            GenerateSuggestionsBtn.Opacity = 1.0;
        }

        private void OnHelperGenerationStarted()
        {
            Dispatcher.BeginInvoke(() =>
            {
                App.TraceUi("HelperGenerationStarted");
                GenerateSuggestionsBtn.IsEnabled = false;
                GenerateSuggestionsBtn.Content = "Generating...";
                HelperStatusText.Text = "Analyzing project...";

                var pulse = new DoubleAnimation(1.0, 0.5, TimeSpan.FromSeconds(0.8))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase()
                };
                GenerateSuggestionsBtn.BeginAnimation(OpacityProperty, pulse);

                StartHelperAnimation();
            });
        }

        private void OnHelperGenerationCompleted()
        {
            Dispatcher.BeginInvoke(() =>
            {
                StopHelperAnimation();
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Generate Suggestions";
                HelperStatusText.Text = $"{_helperManager.Suggestions.Count} suggestions generated";
            });
        }

        private void OnHelperGenerationFailed(string error)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StopHelperAnimation();
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Generate Suggestions";
                HelperStatusText.Text = error;
            });
        }

        // ── Chat Panel (delegated to ChatManager) ─────────────────

        private void NewChat_Click(object sender, RoutedEventArgs e) => _chatManager.HandleNewChat();
        private void ChatSend_Click(object sender, RoutedEventArgs e) => _chatManager.HandleSendClick();
        private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_chatManager.HandlePaste())
                {
                    e.Handled = true;
                    return;
                }
            }
            _chatManager.HandleInputKeyDown(e);
        }
        private void ChatInput_DragOver(object sender, DragEventArgs e) => _chatManager.HandleDragOver(e);
        private void ChatInput_Drop(object sender, DragEventArgs e) => _chatManager.HandleDrop(e);
        private void ChatModelCombo_Changed(object sender, SelectionChangedEventArgs e) => _chatManager.HandleModelComboChanged();

        // ── Status ─────────────────────────────────────────────────

        private void UpdateStatus()
        {
            var running = _activeTasks.Count(t => t.Status == AgentTaskStatus.Running);
            var planning = _activeTasks.Count(t => t.Status == AgentTaskStatus.Planning);
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var waiting = _activeTasks.Count(t => t.Status == AgentTaskStatus.InitQueued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLockManager.LockCount;
            var projectName = Path.GetFileName(_projectManager.ProjectPath);
            var planningPart = planning > 0 ? $"  |  Planning: {planning}" : "";
            var waitingPart = waiting > 0 ? $"  |  Waiting: {waiting}" : "";
            StatusText.Text = $"{projectName}  |  Running: {running}{planningPart}  |  Queued: {queued}{waitingPart}  |  " +
                              $"Completed: {completed}  |  Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                              $"Locks: {locks}  |  {_projectManager.ProjectPath}";
        }
    }
}
