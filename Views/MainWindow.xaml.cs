using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Spritely.Dialogs;
using Spritely.Helpers;
using Spritely.Managers;
using Spritely.Models;
using Spritely.Services;

namespace Spritely
{
    public partial class MainWindow : Window, IDisposable, IProjectPanelView
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static readonly System.Net.Http.HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static readonly string DefaultSystemPrompt = PromptBuilder.DefaultSystemPrompt;
        private string SystemPrompt;

        // ── Injected services (created once, shared across managers) ──
        private readonly IGitHelper _gitHelper;
        private readonly ICompletionAnalyzer _completionAnalyzer;
        private readonly IPromptBuilder _promptBuilder;
        private readonly ITaskFactory _taskFactory;

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private readonly ObservableCollection<AgentTask> _storedTasks = new();
        private readonly object _activeTasksLock = new();
        private readonly object _historyTasksLock = new();
        private readonly object _storedTasksLock = new();
        private ICollectionView? _activeView;
        private ICollectionView? _historyView;
        private ICollectionView? _storedView;
        private ICollectionView? _fileLocksView;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _periodicSaveTimer;

        // Managers
        private readonly SettingsManager _settingsManager;
        private readonly HistoryManager _historyManager;
        private ImageAttachmentManager _imageManager = null!;
        private readonly FileLockManager _fileLockManager;
        private readonly OutputTabManager _outputTabManager;
        private ProjectManager _projectManager = null!;
        private TaskExecutionManager _taskExecutionManager = null!;
        private readonly MessageBusManager _messageBusManager;
        private McpHealthMonitor? _mcpHealthMonitor;
        private TerminalTabManager? _terminalManager;
        private GeminiService _geminiService = null!;
        private ClaudeService _claudeService = null!;
        private ClaudeUsageManager _claudeUsageManager = null!;
        private ModelConfigManager _modelConfigManager = null!;
        private ProjectTaskManager _projectTaskManager = null!;
        private HelperManager _helperManager = null!;
        private ActivityDashboardManager _activityDashboard = null!;
        private GitPanelManager _gitPanelManager = null!;
        private IdePanelManager _idePanelManager = null!;
        private GitOperationGuard _gitOperationGuard = null!;
        private CommitOrchestrator _commitOrchestrator = null!;
        private readonly TaskGroupTracker _taskGroupTracker;
        private readonly TaskOrchestrator _taskOrchestrator;
        private SkillManager _skillManager = null!;
        private ImprovementTaskGenerator _improvementTaskGenerator = null!;
        private DispatcherTimer? _helperAnimTimer;
        private int _helperAnimTick;

        // Task numbering (1–9999, resets on app restart)
        private int _nextTaskNumber = 1;

        // Disposal guard
        private bool _disposed;

        // Drag-drop state for task reordering
        private Point _dragStartPoint;
        private AgentTask? _draggedTask;

        // Window-level cancellation — cancelled on close to abort in-flight async work
        private readonly CancellationTokenSource _windowCts = new();

        // Chat
        private ChatManager _chatManager = null!;


        // Graph collapse state
        private bool _graphCollapsed = true;
        private GridLength _graphExpandedHeight = new(180);

        // Terminal collapse state
        private bool _terminalCollapsed = true;
        private GridLength _terminalExpandedHeight = new(120);

        // Track when any splitter is being dragged to prevent RestoreStarRows interference
        private bool _isSplitterDragging;
        private bool _restoreStarRowsPending;


        // IProjectPanelView — expose named XAML controls to ProjectManager
        ComboBox IProjectPanelView.PromptProjectLabel => PromptProjectLabel;
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
                "Spritely");
            Directory.CreateDirectory(appDataDir);

            var scriptDir = Path.Combine(appDataDir, "scripts");
            Directory.CreateDirectory(scriptDir);

            // Initialize managers (no file I/O here — async loading happens in Window_Loaded)
            _settingsManager = new SettingsManager(appDataDir);

            _historyManager = new HistoryManager(appDataDir, _historyTasksLock, _storedTasksLock);

            _fileLockManager = new FileLockManager(Dispatcher);
            _fileLockManager.QueuedTaskResumed += OnQueuedTaskResumed;
            _fileLockManager.TaskNeedsPause += OnTaskNeedsPause;
            _fileLockManager.LocksChanged += UpdateFileLocks;

            _outputTabManager = new OutputTabManager(OutputTabs, Dispatcher);
            _outputTabManager.TabCloseRequested += OnTabCloseRequested;
            _outputTabManager.TabStoreRequested += OnTabStoreRequested;
            _outputTabManager.TabResumeRequested += OnTabResumeRequested;
            _outputTabManager.InputSent += OnTabInputSent;
            _outputTabManager.InterruptInputSent += OnTabInterruptInputSent;

            // ProjectManager needs many UI refs — initialize after InitializeComponent.
            // Use a quick sync peek at settings for the initial project path (tiny file);
            // full async load of all data happens in Window_Loaded.
            _projectManager = new ProjectManager(
                appDataDir,
                PeekInitialProjectPath(appDataDir),
                this);
            _projectManager.McpOutputChanged += OnMcpOutputChanged;
            _projectManager.ProjectSwapStarted += OnProjectSwapStarted;
            _projectManager.ProjectSwapCompleted += OnProjectSwapCompleted;
            _projectManager.ProjectRenamed += OnProjectRenamed;

            _imageManager = new ImageAttachmentManager(appDataDir, ImageIndicator, ClearImagesBtn);
            _geminiService = new GeminiService(appDataDir);
            _claudeService = new ClaudeService(appDataDir);
            _claudeUsageManager = new ClaudeUsageManager(appDataDir);
            _claudeService.SetUsageManager(_claudeUsageManager);
            _modelConfigManager = new ModelConfigManager(appDataDir);
            ClaudeService.AvailableModels = _modelConfigManager.ClaudeModels;
            GeminiService.AvailableModels = _modelConfigManager.GeminiModels;
            _projectTaskManager = new ProjectTaskManager(appDataDir);
            _skillManager = new SkillManager(appDataDir);
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
            _messageBusManager.MessageReceived += OnBusMessageReceived;

            _gitHelper = new GitHelper();
            _completionAnalyzer = new CompletionAnalyzer(_gitHelper);
            _promptBuilder = new Managers.PromptBuilder();
            _taskFactory = new Managers.TaskFactory();
            _projectManager.SetTaskFactory(_taskFactory);
            _projectManager.SetSettingsManager(_settingsManager);

            var featureRegistryManager = new FeatureRegistryManager();
            var codebaseIndexManager = new CodebaseIndexManager();
            var moduleRegistryManager = new ModuleRegistryManager();
            var embeddingService = new EmbeddingService();
            var vectorStore = new VectorStore();
            var featureContextResolver = new FeatureContextResolver(
                featureRegistryManager, codebaseIndexManager, moduleRegistryManager,
                embeddingService, vectorStore, _claudeService);
            var hybridSearchManager = new HybridSearchManager(
                embeddingService, vectorStore, featureRegistryManager,
                featureContextResolver, codebaseIndexManager);
            var featureUpdateAgent = new FeatureUpdateAgent(
                featureRegistryManager, codebaseIndexManager, moduleRegistryManager, _claudeService);

            featureUpdateAgent.StartBackgroundRetryWorker(
                isBusy: () => { lock (_activeTasksLock) return _activeTasks.Any(t => !t.IsFinished); },
                getProjectPaths: () => _projectManager.SavedProjects
                    .Select(p => p.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                shutdownToken: _windowCts.Token);

            // Feedback System: self-improving feedback loop
            var feedbackStore = new FeedbackStore(appDataDir);
            var improvementTaskGenerator = new ImprovementTaskGenerator(appDataDir);
            var feedbackAnalyzer = new FeedbackAnalyzer();
            var feedbackApplicator = new FeedbackApplicator(
                getProjectEntry: path => _projectManager.SavedProjects.FirstOrDefault(p => p.Path == path),
                saveProjects: () => _projectManager.SaveProjects(),
                rulesManager: _projectManager.Rules,
                taskGenerator: improvementTaskGenerator);
            var feedbackCollector = new FeedbackCollector(feedbackStore, feedbackAnalyzer, feedbackApplicator);
            _improvementTaskGenerator = improvementTaskGenerator;

            _gitOperationGuard = new GitOperationGuard(_fileLockManager);

            _taskExecutionManager = new TaskExecutionManager(new TaskExecutionServices
            {
                ScriptDir = scriptDir,
                Dispatcher = Dispatcher,
                FileLockManager = _fileLockManager,
                OutputTabManager = _outputTabManager,
                MessageBusManager = _messageBusManager,
                GitOperationGuard = _gitOperationGuard,
                GitHelper = _gitHelper,
                CompletionAnalyzer = _completionAnalyzer,
                PromptBuilder = _promptBuilder,
                TaskFactory = _taskFactory,
                GetSystemPrompt = () => SystemPrompt,
                GetProjectDescription = task => _projectManager.GetProjectDescription(task),
                GetProjectRulesBlock = path => _projectManager.GetProjectRulesBlock(path),
                IsGameProject = path => _projectManager.IsGameProject(path),
                GetTokenLimitRetryMinutes = () => _settingsManager.TokenLimitRetryMinutes,
                GetAutoVerify = () => _settingsManager.AutoVerify,
                GetShowCodeChanges = () => _settingsManager.ShowCodeChanges,
                GetSkillsBlock = () => GetActiveSkillsBlock(),
                GetOpusEffortLevel = () => _settingsManager.OpusEffortLevel,
                FeatureRegistryManager = featureRegistryManager,
                FeatureContextResolver = featureContextResolver,
                FeatureUpdateAgent = featureUpdateAgent,
                HybridSearchManager = hybridSearchManager,
                ClaudeService = _claudeService,
                FeedbackCollector = feedbackCollector,
                SkillDiscoveryAgent = new SkillDiscoveryAgent(_claudeService, _skillManager, Dispatcher),
            });
            _taskExecutionManager.TaskCompleted += OnTaskProcessCompleted;
            _taskExecutionManager.SubTaskSpawned += OnSubTaskSpawned;

            _taskOrchestrator = new TaskOrchestrator();
            _taskOrchestrator.TaskReady += OnOrchestratorTaskReady;
            _taskExecutionManager.TaskNeedsOrchestratorRegistration += (task, deps) => _taskOrchestrator.AddTask(task, deps);

            _taskGroupTracker = new TaskGroupTracker();
            _taskGroupTracker.GroupCompleted += OnTaskGroupCompleted;

            // Wire improvement suggestions → stored tasks
            _improvementTaskGenerator.TaskQueued += OnImprovementTaskQueued;

            _activityDashboard = new ActivityDashboardManager(_activeTasks, _historyTasks, _projectManager.SavedProjects);

            _commitOrchestrator = new CommitOrchestrator(
                _gitHelper,
                _gitOperationGuard,
                _fileLockManager,
                _historyManager,
                () => _historyTasks,
                () => _activeTasks,
                () => _gitPanelManager?.MarkDirty());
            _gitPanelManager = new GitPanelManager(
                _gitHelper,
                () => _projectManager.ProjectPath,
                _fileLockManager,
                _gitOperationGuard,
                Dispatcher,
                _settingsManager);
            _taskExecutionManager.TaskCompleted += _gitPanelManager.OnTaskCompleted;
            _gitPanelManager.StartWatching();

            _idePanelManager = new IdePanelManager(
                _gitHelper,
                () => _projectManager.ProjectPath,
                _fileLockManager,
                () => { lock (_activeTasksLock) return _activeTasks.ToList(); },
                () => { lock (_historyTasksLock) return _historyTasks.ToList(); },
                Dispatcher);
            _taskExecutionManager.TaskCompleted += _idePanelManager.OnTaskCompleted;

            _projectManager.SetTaskCollections(_activeTasks, _historyTasks);

            // Set up collections with cross-thread synchronization
            BindingOperations.EnableCollectionSynchronization(_activeTasks, _activeTasksLock);
            BindingOperations.EnableCollectionSynchronization(_historyTasks, _historyTasksLock);
            BindingOperations.EnableCollectionSynchronization(_storedTasks, _storedTasksLock);
            _activeView = CollectionViewSource.GetDefaultView(_activeTasks);
            _historyView = CollectionViewSource.GetDefaultView(_historyTasks);
            _storedView = CollectionViewSource.GetDefaultView(_storedTasks);
            _fileLocksView = CollectionViewSource.GetDefaultView(_fileLockManager.FileLocksView);
            ActiveTasksList.ItemsSource = _activeView;
            HistoryTasksList.ItemsSource = _historyView;
            StoredTasksList.ItemsSource = _storedView;
            FileLocksListView.ItemsSource = _fileLocksView;
            SuggestionsListView.ItemsSource = _helperManager.Suggestions;

            _activeTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            _historyTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            _storedTasks.CollectionChanged += OnCollectionChangedUpdateTabs;
            UpdateTabCounts();
            InitializeNodeGraphPanel();
            UpdateFileLocks();

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

                // Check for task timeouts
                App.TraceUi("StatusTimer.CheckTimeouts");
                CheckTaskTimeouts();
            };

            // Initialize periodic save timer to checkpoint active queue every 5 minutes
            _periodicSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _periodicSaveTimer.Tick += (_, _) =>
            {
                App.TraceUi("PeriodicSaveTimer.SaveActiveQueue");
                // Use background write for periodic saves to avoid blocking UI thread
                _historyManager.SaveActiveQueue(_activeTasks, _activeTasksLock, useBackgroundWrite: true);
            };

            Closing += OnWindowClosing;

            _terminalManager = new TerminalTabManager(
                TerminalTabBar, TerminalOutput, TerminalInput,
                TerminalSendBtn, TerminalInterruptBtn, TerminalRootPath,
                Dispatcher, _projectManager.ProjectPath);
            _terminalManager.AddTerminal();

            // Saved prompts loaded async in Window_Loaded

            MainTabs.SelectionChanged += MainTabs_SelectionChanged;
            StatisticsTabs.SelectionChanged += StatisticsTabs_SelectionChanged;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            App.ClearTrayBadge();
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
            catch (IOException ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to read settings.json: {ex.Message}"); }
            catch (System.Text.Json.JsonException ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to parse settings.json: {ex.Message}"); }

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
            catch (IOException ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to read projects.json: {ex.Message}"); }
            catch (System.Text.Json.JsonException ex) { Managers.AppLogger.Debug("MainWindow", $"Failed to parse projects.json: {ex.Message}"); }

            var restored = lastSelected != null && savedProjects.Any(p => p.Path == lastSelected);
            return restored ? lastSelected! :
                   savedProjects.Count > 0 ? savedProjects[0].Path : Directory.GetCurrentDirectory();
        }

        // ── Window Loaded ──────────────────────────────────────────

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var ct = _windowCts.Token;

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var value = 1;
                DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { Managers.AppLogger.Debug("MainWindow", "DWM dark mode attribute failed", ex); }

            await LoadSystemPromptAsync(ct);
            ct.ThrowIfCancellationRequested();
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
            await LoadStartupDataAsync(ct);
            ct.ThrowIfCancellationRequested();
            await LoadSavedPromptsAsync(ct);
            ct.ThrowIfCancellationRequested();
            await LoadSkillsAsync();
            ct.ThrowIfCancellationRequested();
            InitializeFeaturesTab();
            await LoadFeaturesAsync();
            ct.ThrowIfCancellationRequested();
            await _settingsManager.LoadTemplatesAsync(_projectManager.ProjectPath);
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
            TaskTimeoutBox.Text = _settingsManager.TaskTimeoutMinutes.ToString();
            AutoVerifyToggle.IsChecked = _settingsManager.AutoVerify;
            ShowCodeChangesToggle.IsChecked = _settingsManager.ShowCodeChanges;
            DefaultMcpServerNameBox.Text = _settingsManager.DefaultMcpServerName;
            DefaultMcpAddressBox.Text = _settingsManager.DefaultMcpAddress;
            DefaultMcpStartCommandBox.Text = _settingsManager.DefaultMcpStartCommand;

            foreach (ComboBoxItem item in OpusEffortCombo.Items)
            {
                if (item.Tag?.ToString() == _settingsManager.OpusEffortLevel)
                {
                    OpusEffortCombo.SelectedItem = item;
                    break;
                }
            }

            // Remote Server settings
            RemoteServerToggle.IsChecked = _settingsManager.RemoteServerEnabled;
            RemoteServerPortBox.Text = _settingsManager.RemoteServerPort.ToString();
            InitializeRemoteServer();

            if (_settingsManager.SettingsPanelCollapsed)
                ApplySettingsPanelCollapsed(true);
            if (_settingsManager.LeftPanelCollapsed)
                ApplyLeftPanelCollapsed(true);

            SetupMainTabsOverflow();

            await CheckClaudeCliAsync(ct);
        }

        private async System.Threading.Tasks.Task CheckClaudeCliAsync(CancellationToken ct = default)
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

                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0)
                    ShowClaudeNotFoundWarning();
            }
            catch (OperationCanceledException) { /* window closing — abandon check */ }
            catch (System.ComponentModel.Win32Exception)
            {
                ShowClaudeNotFoundWarning();
            }
            catch (InvalidOperationException)
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

        private async System.Threading.Tasks.Task LoadStartupDataAsync(CancellationToken ct = default)
        {
            try
            {
                // Load settings, projects, history, stored tasks, and any recoverable queue off the UI thread
                var settingsTask = _settingsManager.LoadSettingsAsync();
                var projectsTask = _projectManager.LoadProjectsAsync();
                var historyTask = _historyManager.LoadHistoryAsync(_settingsManager.HistoryRetentionHours);
                var storedTask = _historyManager.LoadStoredTasksAsync();
                var activeQueueTask = _historyManager.LoadActiveQueueAsync();
                var tasksTask = _projectTaskManager.LoadTasksAsync();

                await System.Threading.Tasks.Task.WhenAll(settingsTask, projectsTask, historyTask, storedTask, activeQueueTask, tasksTask);
                ct.ThrowIfCancellationRequested();

                // Populate collections on the UI thread — pin Row 0 to prevent layout jitter
                var historyItems = historyTask.Result;
                var storedItems = storedTask.Result;
                var recoveredTasks = activeQueueTask.Result;

                PinRowHeights();

                foreach (var item in historyItems)
                    _historyTasks.Add(item);
                foreach (var item in storedItems)
                    _storedTasks.Add(item);

                RestoreStarRows();
                RefreshFilterCombos();
                RefreshActivityDashboard();
                _gitPanelManager.RefreshIfNeeded(GitTabContent);
                _projectTaskManager.CurrentProjectPath = _projectManager.ProjectPath;
                InitializeTaskList();
                _projectManager.RefreshProjectList(
                    p => _terminalManager?.UpdateWorkingDirectory(p),
                    () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                    SyncSettingsForProject);

                // Sync Game/App UI for the initially selected project
                _projectManager.UpdateMcpToggleForProject();
                var activeEntry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
                ProjectTypeGameToggle.IsChecked = activeEntry?.IsGame == true;
                UpdateMcpVisibility(activeEntry?.IsGame == true);
                SyncMcpSettingsFields();

                // Migrate .agent-bus folders from project directories to appData
                await MigrateAllProjectBusesAsync();

                _statusTimer.Start();
                _periodicSaveTimer.Start();
                UpdateNoProjectState();
                UpdateStatus();

                // Offer to re-queue recovered tasks from a previous session
                if (recoveredTasks.Count > 0)
                    RecoverActiveQueue(recoveredTasks);
            }
            catch (OperationCanceledException) { /* window closing — stop startup */ }
            catch (IOException ex)
            {
                Managers.AppLogger.Warn("MainWindow", "Failed during async startup loading (I/O)", ex);
            }
            catch (System.Text.Json.JsonException ex)
            {
                Managers.AppLogger.Warn("MainWindow", "Failed during async startup loading (JSON parse)", ex);
            }
            catch (InvalidOperationException ex)
            {
                Managers.AppLogger.Warn("MainWindow", "Failed during async startup loading", ex);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Migrates all project .agent-bus folders from project directories to appData
        /// </summary>
        private async System.Threading.Tasks.Task MigrateAllProjectBusesAsync()
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var projects = _projectManager.SavedProjects;
                    var migratedCount = 0;

                    foreach (var project in projects)
                    {
                        if (_messageBusManager.ForceMigrateActiveBus(project.Path))
                        {
                            migratedCount++;
                            Managers.AppLogger.Info("MainWindow", $"Migrated agent bus for project: {project.Path}");
                        }
                    }

                    if (migratedCount > 0)
                    {
                        Managers.AppLogger.Info("MainWindow", $"Successfully migrated {migratedCount} project bus(es) to appData");
                    }
                }
                catch (IOException ex)
                {
                    Managers.AppLogger.Error("MainWindow", "Failed to migrate project buses (I/O)", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Managers.AppLogger.Error("MainWindow", "Failed to migrate project buses (access denied)", ex);
                }
            });
        }

        /// <summary>
        /// Shows a confirmation dialog listing tasks recovered from a previous session and
        /// re-queues them as new InitQueued tasks if the user confirms.
        /// </summary>
        private void RecoverActiveQueue(List<AgentTask> recoveredTasks)
        {
            var descriptions = string.Join("\n",
                recoveredTasks.Select((t, i) => $"  {i + 1}. {_taskFactory.GenerateLocalSummary(t.Description)}"));

            var confirmed = Dialogs.DarkDialog.ShowConfirm(
                $"{recoveredTasks.Count} task(s) were still queued when the application last closed:\n\n" +
                $"{descriptions}\n\n" +
                "Would you like to re-queue them?",
                "Recover Queued Tasks");

            if (confirmed)
            {
                foreach (var task in recoveredTasks)
                {
                    task.StartTime = DateTime.Now;
                    LaunchTask(task);
                }
            }

            // Always clear the recovery file
            _historyManager.ClearActiveQueue();
        }

        // ── System Prompt ──────────────────────────────────────────

        private string SystemPromptFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spritely", "system_prompt.txt");

        private async System.Threading.Tasks.Task LoadSystemPromptAsync(CancellationToken ct = default)
        {
            try
            {
                if (File.Exists(SystemPromptFile))
                {
                    var text = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(SystemPromptFile), ct);
                    ct.ThrowIfCancellationRequested();
                    if (text.Contains("# MCP VERIFICATION"))
                    {
                        text = text.Replace(PromptBuilder.McpPromptBlock, "");
                        var cleanedText = text;
                        var path = SystemPromptFile;
                        Managers.SafeFileWriter.WriteInBackground(path, cleanedText, "MainWindow");
                    }
                    SystemPrompt = text;
                    SystemPromptBox.Text = SystemPrompt;
                }
            }
            catch (OperationCanceledException) { /* window closing */ }
            catch (IOException ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load system prompt", ex); }
        }

        // ── Settings Sync ──────────────────────────────────────────

        private async void SyncSettingsForProject()
        {
            try
            {
                await SyncSettingsForProjectAsync(_windowCts.Token);
            }
            catch (OperationCanceledException) { /* window closing */ }
            catch (IOException ex) { Managers.AppLogger.Warn("MainWindow", "Failed to sync settings for project (I/O)", ex); }
            catch (System.Text.Json.JsonException ex) { Managers.AppLogger.Warn("MainWindow", "Failed to sync settings for project (JSON)", ex); }
            catch (InvalidOperationException ex) { Managers.AppLogger.Warn("MainWindow", "Failed to sync settings for project", ex); }
            finally
            {
                RestoreStarRows();
            }
        }

        private async System.Threading.Tasks.Task SyncSettingsForProjectAsync(CancellationToken ct)
        {
            App.TraceUi("SyncSettingsForProject");

            // Pin both rows to prevent prompt panel resize jitter during layout changes
            PinRowHeights();

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

            // All critical UI updates must happen synchronously (before any await)
            RefreshFilterCombos();
            ActiveFilter_Changed(ActiveFilterCombo, null!);
            HistoryFilter_Changed(HistoryFilterCombo, null!);

            if (EditSystemPromptToggle.IsChecked == true)
            {
                EditSystemPromptToggle.IsChecked = false;
                SystemPromptBox.Text = SystemPrompt;
            }

            UpdateStatus();
            UpdateFileLocks();
            UpdateNoProjectState();

            ct.ThrowIfCancellationRequested();

            // Non-critical async work: reload helper suggestions for the new project.
            await _helperManager.SwitchProjectAsync(_projectManager.ProjectPath);
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

        // ── Node Graph ──────────────────────────────────────────────

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
                    _outputTabManager.AppendOutput(task.Id, "\nTask paused.\n", _activeTasks, _historyTasks);
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
                    task.Status = AgentTaskStatus.Stored;
                    task.QueuedReason = null;
                    task.StartTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\nForce-starting task #{task.TaskNumber} (limit bypassed)...\n\n",
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
                                $"\nForce-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                                _activeTasks, _historyTasks);
                        }
                        else
                        {
                            task.Status = AgentTaskStatus.Stored;
                            task.StartTime = DateTime.Now;
                            _outputTabManager.AppendOutput(task.Id,
                                $"\nForce-starting task #{task.TaskNumber} (dependencies skipped)...\n\n",
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

                if (dependent.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning)
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
                    $"\nTask #{dependent.TaskNumber} queued — waiting for #{prerequisite.TaskNumber} to complete.\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(dependent);
                UpdateStatus();
            };
            NodeGraphPanel.DependenciesRemoved += task =>
            {
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
                                $"\nDependencies removed — resuming task #{task.TaskNumber}.\n",
                                _activeTasks, _historyTasks);
                        }
                        else
                        {
                            task.Status = AgentTaskStatus.Stored;
                            task.StartTime = DateTime.Now;
                            _outputTabManager.AppendOutput(task.Id,
                                $"\nDependencies removed — starting task #{task.TaskNumber}...\n",
                                _activeTasks, _historyTasks);
                            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                        }
                    }
                }

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
            NodeGraphPanel.TaskScrollRequested += taskId =>
            {
                var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    ActiveTasksList.SelectedItem = task;
                    ActiveTasksList.ScrollIntoView(task);
                }
            };
        }

        /// <summary>
        /// Auto-expands the graph panel when feature mode tasks are present,
        /// and collapses it when no tasks remain.
        /// </summary>
        private void CheckAutoExpandGraph()
        {
            bool hasFeatureModeTasks = _activeTasks.Any(t => t.IsFeatureMode && !t.IsFinished);
            bool hasDependencyTasks = _activeTasks.Any(t => t.DependencyTaskIdCount > 0 || t.BlockedByTaskId != null);

            if ((hasFeatureModeTasks || hasDependencyTasks) && _graphCollapsed)
                ToggleGraphCollapse();
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

            if (!_gitHelper.IsValidGitHash(task.GitStartHash))
            {
                Dialogs.DarkDialog.ShowAlert("Invalid git hash format detected. Cannot perform revert.", "Invalid Git Hash");
                AppLogger.Warn("MainWindow", $"Invalid git hash detected in revert operation: {task.GitStartHash}");
                return;
            }

            var shortHash = task.GitStartHash[..Math.Min(8, task.GitStartHash.Length)];
            if (!Dialogs.DarkDialog.ShowConfirm(
                $"This will run 'git reset --hard {shortHash}' in:\n{task.ProjectPath}\n\nAll uncommitted changes will be lost. Continue?",
                "Revert Task Changes"))
                return;
            try
            {
                var result = await _gitHelper.RunGitCommandAsync(
                    task.ProjectPath, $"reset --hard {task.GitStartHash}");

                if (result != null)
                {
                    _outputTabManager.AppendOutput(task.Id,
                        $"\nReverted to commit {shortHash}.\n", _activeTasks, _historyTasks);
                    Dialogs.DarkDialog.ShowAlert($"Successfully reverted to commit {shortHash}.", "Revert Complete");
                }
                else
                {
                    Dialogs.DarkDialog.ShowAlert("Git reset failed. The commit may no longer exist or the repository state may have changed.", "Revert Failed");
                }
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Error("MainWindow", "Git revert failed", ex);
                Dialogs.DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
            catch (IOException ex)
            {
                AppLogger.Error("MainWindow", "Git revert failed (I/O)", ex);
                Dialogs.DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
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

        private void TaskInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentLoadedPrompt != null)
            {
                var currentText = TaskInput.Text?.Trim() ?? "";
                var originalText = _currentLoadedPrompt.PromptText?.Trim() ?? "";
                _loadedPromptModified = currentText != originalText;

                if (string.IsNullOrEmpty(currentText) || _loadedPromptModified)
                {
                    if (string.IsNullOrEmpty(currentText))
                        _currentLoadedPrompt = null;
                }
            }
        }

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
            _currentLoadedPrompt = null;
            _loadedPromptModified = false;
        }

        // ── Dependencies ────────────────────────────────────────────

        private readonly List<AgentTask> _pendingDependencies = new();

        internal void TaskCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInsideButton(e.OriginalSource as DependencyObject))
            {
                _dragStartedOnButton = true;
                return;
            }
            _dragStartPoint = e.GetPosition(null);
            _dragStartedOnButton = false;
        }

        private bool _dragStartedOnButton;

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        internal void TaskCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (task.IsFinished) return;
            if (_dragStartedOnButton) return;

            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

            var pos = e.GetPosition(null);
            var diff = pos - _dragStartPoint;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

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

        internal void TaskCard_DragOver(object sender, DragEventArgs e)
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

            e.Effects = IsReorderablePair(dragged, target) ? DragDropEffects.Move : DragDropEffects.Link;
            e.Handled = true;
        }

        private static bool IsReorderablePair(AgentTask a, AgentTask b) =>
            (a.IsQueued || a.IsInitQueued) && (b.IsQueued || b.IsInitQueued);

        internal void TaskCard_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("AgentTask")) return;
            if (sender is not FrameworkElement { DataContext: AgentTask target }) return;
            if (e.Data.GetData("AgentTask") is not AgentTask dragged) return;
            if (dragged.Id == target.Id || target.IsFinished || dragged.IsFinished) return;

            if (IsReorderablePair(dragged, target))
            {
                ReorderQueuedTask(dragged, target);
                e.Handled = true;
                return;
            }

            if (dragged.ContainsDependencyTaskId(target.Id)) return;

            if (_taskOrchestrator.ContainsTask(dragged.Id) && _taskOrchestrator.ContainsTask(target.Id))
            {
                if (_taskOrchestrator.DetectCycle(dragged.Id, target.Id)) return;
            }
            else if (WouldCreateCircularDependency(dragged, target)) return;

            dragged.AddDependencyTaskId(target.Id);
            dragged.DependencyTaskNumbers.Add(target.TaskNumber);
            _taskOrchestrator.AddTask(dragged, new List<string> { target.Id });
            dragged.BlockedByTaskId = target.Id;
            dragged.BlockedByTaskNumber = target.TaskNumber;
            dragged.QueuedReason = $"Waiting for #{target.TaskNumber} to complete";

            if (dragged.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning)
            {
                _taskExecutionManager.PauseTask(dragged);
                dragged.Status = AgentTaskStatus.Queued;
            }
            else if (dragged.Status is AgentTaskStatus.Paused)
            {
                dragged.Status = AgentTaskStatus.Queued;
            }
            else if (dragged.Status is AgentTaskStatus.InitQueued)
            {
                dragged.Status = AgentTaskStatus.Queued;
            }

            _outputTabManager.AppendOutput(dragged.Id,
                $"\nTask #{dragged.TaskNumber} queued — waiting for #{target.TaskNumber} to complete.\n",
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

            var sorted = queuedTasks
                .Select((t, i) => (Task: t, Index: i))
                .OrderByDescending(x => (int)x.Task.PriorityLevel)
                .ThenBy(x => x.Index)
                .Select(x => x.Task)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
                sorted[i].Priority = sorted.Count - i;
        }

        /// <summary>
        /// Reorders queued tasks in the active list by PriorityLevel,
        /// preserving running/non-queued task positions, then recalculates
        /// numeric Priority values.
        /// </summary>
        private void ReorderByPriority()
        {
            var queuedTasks = _activeTasks
                .Where(t => t.IsQueued || t.IsInitQueued)
                .ToList();

            if (queuedTasks.Count <= 1)
            {
                RecalculateQueuePriorities();
                return;
            }

            var sorted = queuedTasks
                .OrderByDescending(t => (int)t.PriorityLevel)
                .ToList();

            var queuedSlots = _activeTasks
                .Select((t, i) => (Task: t, Index: i))
                .Where(x => x.Task.IsQueued || x.Task.IsInitQueued)
                .Select(x => x.Index)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var currentIdx = _activeTasks.IndexOf(sorted[i]);
                var targetIdx = queuedSlots[i];
                if (currentIdx != targetIdx)
                    _activeTasks.Move(currentIdx, targetIdx);
            }

            RecalculateQueuePriorities();
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
            if (PlanOnlyToggle.IsChecked == true && ApplyFixToggle.IsChecked == true)
                ApplyFixToggle.IsChecked = false;
            UpdateExecuteButtonText();
        }

        private void ApplyFixToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ExecuteButton == null) return;
            if (ApplyFixToggle.IsChecked == true && PlanOnlyToggle.IsChecked == true)
                PlanOnlyToggle.IsChecked = false;
            UpdateExecuteButtonText();
        }

        internal void RemoveStoredTask_Click(object sender, RoutedEventArgs e)
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

        internal void CopyStoredPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!string.IsNullOrEmpty(task.StoredPrompt))
                Clipboard.SetText(task.StoredPrompt);
        }

        internal void ExecuteStoredTask_Click(object sender, RoutedEventArgs e)
        {
            if (!_projectManager.HasProjects) return;
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (string.IsNullOrEmpty(task.StoredPrompt)) return;

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem && modelItem.Tag?.ToString() == "Gemini")
                selectedModel = ModelType.Gemini;

            var newTask = _taskFactory.CreateTask(
                task.StoredPrompt,
                task.ProjectPath,
                true,
                false,
                FeatureModeToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                PlanOnlyToggle.IsChecked == true,
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel,
                autoDecompose: AutoDecomposeToggle.IsChecked == true);
            newTask.ProjectColor = _projectManager.GetProjectColor(newTask.ProjectPath);
            newTask.ProjectDisplayName = _projectManager.GetProjectDisplayName(newTask.ProjectPath);
            newTask.Summary = $"Executing stored plan: {task.ShortDescription}";

            newTask.TimeoutMinutes = _settingsManager.TaskTimeoutMinutes;

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
                $"Executing stored plan from task #{task.TaskNumber}\n",
                _activeTasks, _historyTasks);
            _ = _taskExecutionManager.StartProcess(newTask, _activeTasks, _historyTasks, MoveToHistory);
            RefreshFilterCombos();
            UpdateStatus();
        }

        internal void ViewStoredTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            StoredTaskViewerDialog.Show(task);
        }

        internal void ViewOutput_Click(object sender, RoutedEventArgs e)
        {
            var task = GetTaskFromContextMenuItem(sender);
            if (task == null) return;
            if (string.IsNullOrEmpty(task.FullOutput) && task.OutputBuilder.Length > 0)
                task.FullOutput = task.OutputBuilder.ToString();
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

        private void OnImprovementTaskQueued(ImprovementTaskEntry entry)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var storedTask = new AgentTask
                {
                    Description = ImprovementTaskGenerator.BuildTaskDescription(entry),
                    ProjectPath = entry.ProjectPath,
                    ProjectColor = _projectManager.GetProjectColor(entry.ProjectPath),
                    ProjectDisplayName = _projectManager.GetProjectDisplayName(entry.ProjectPath),
                    StoredPrompt = ImprovementTaskGenerator.BuildTaskDescription(entry),
                    StartTime = DateTime.Now
                };
                storedTask.Summary = $"[Feedback] {entry.Title}";
                storedTask.Status = AgentTaskStatus.Completed;

                _storedTasks.Insert(0, storedTask);
                _historyManager.SaveStoredTasks(_storedTasks);
                _improvementTaskGenerator.MarkLaunched(entry.Id);

                AppLogger.Info("Feedback", $"Created stored task from improvement suggestion: {entry.Title}");
            });
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
            if (HistoryTasksList.SelectedItem is not AgentTask task) return;

            if (_outputTabManager.HasTab(task.Id))
            {
                OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
                return;
            }

            _outputTabManager.CreateTab(task);

            var existingOutput = task.OutputBuilder.Length > 0
                ? task.OutputBuilder.ToString()
                : task.FullOutput;

            if (!string.IsNullOrEmpty(existingOutput))
                _outputTabManager.AppendOutput(task.Id, existingOutput, _activeTasks, _historyTasks);

            _outputTabManager.UpdateTabHeader(task);
            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);
        }

        private void ActiveTaskList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ActiveTasksList.SelectedItem is AgentTask task)
                NodeGraphPanel.FocusOnTask(task.Id);
        }

        // ── Drag & Drop Task Reordering ──────────────────────────────

        private void ActiveTasksList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);

            var hitTest = e.OriginalSource as DependencyObject;
            while (hitTest != null && !(hitTest is ListBoxItem))
            {
                hitTest = VisualTreeHelper.GetParent(hitTest);
            }

            if (hitTest is ListBoxItem item && item.DataContext is AgentTask task)
            {
                if (task.Status == AgentTaskStatus.Queued || task.Status == AgentTaskStatus.InitQueued)
                {
                    _draggedTask = task;
                }
            }
        }

        private void ActiveTasksList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedTask == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var currentPoint = e.GetPosition(null);
            var diff = _dragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var listBox = sender as ListBox;
                var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);

                if (listBox != null && listBoxItem != null)
                {
                    DragDrop.DoDragDrop(listBoxItem, _draggedTask, DragDropEffects.Move);
                }
            }
        }

        private void ActiveTasksList_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(AgentTask)))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var draggedTask = e.Data.GetData(typeof(AgentTask)) as AgentTask;
            if (draggedTask != null &&
                (draggedTask.Status == AgentTaskStatus.Queued || draggedTask.Status == AgentTaskStatus.InitQueued))
            {
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void ActiveTasksList_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(AgentTask)) || _draggedTask == null)
                return;

            var droppedTask = e.Data.GetData(typeof(AgentTask)) as AgentTask;
            if (droppedTask == null ||
                !(droppedTask.Status == AgentTaskStatus.Queued || droppedTask.Status == AgentTaskStatus.InitQueued))
                return;

            var targetIndex = -1;
            var hitTest = VisualTreeHelper.HitTest(ActiveTasksList, e.GetPosition(ActiveTasksList))?.VisualHit;

            while (hitTest != null && !(hitTest is ListBoxItem))
            {
                hitTest = VisualTreeHelper.GetParent(hitTest);
            }

            if (hitTest is ListBoxItem targetItem && targetItem.DataContext is AgentTask targetTask)
            {
                lock (_activeTasksLock)
                {
                    targetIndex = _activeTasks.IndexOf(targetTask);
                }
            }

            if (targetIndex >= 0)
            {
                lock (_activeTasksLock)
                {
                    var oldIndex = _activeTasks.IndexOf(droppedTask);
                    if (oldIndex >= 0 && oldIndex != targetIndex)
                    {
                        _activeTasks.RemoveAt(oldIndex);
                        _activeTasks.Insert(targetIndex, droppedTask);
                    }
                }
            }

            _draggedTask = null;
            e.Handled = true;
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T ancestor)
                    return ancestor;
                current = VisualTreeHelper.GetParent(current);
            }
            while (current != null);
            return null;
        }

        // ── Status ─────────────────────────────────────────────────

        /// <summary>
        /// Updates the UI to reflect whether any projects are loaded.
        /// Disables prompt input, execute buttons, and project-dependent panels when no projects exist.
        /// </summary>
        private void UpdateNoProjectState()
        {
            var hasProjects = _projectManager.HasProjects;

            NoProjectOverlay.Visibility = hasProjects ? Visibility.Collapsed : Visibility.Visible;

            ExecuteButton.IsEnabled = hasProjects;
            TaskInput.IsEnabled = hasProjects;
            AdditionalInstructionsInput.IsEnabled = hasProjects;

            if (!hasProjects)
            {
                PromptProjectLabel.Items.Clear();
                PromptProjectLabel.ToolTip = null;
            }

            HelperTabItem.IsEnabled = hasProjects;
            HelperTabItem.ToolTip = hasProjects ? null : "Add a project to use automation features";

            GitTabItem.IsEnabled = hasProjects;
            GitTabItem.ToolTip = hasProjects ? null : "Add a project to view git information";

            TasksTabItem.IsEnabled = hasProjects;
            TasksTabItem.ToolTip = hasProjects ? null : "Add a project to manage tasks";
            NoProjectTasksOverlay.Visibility = hasProjects ? Visibility.Collapsed : Visibility.Visible;
            TaskContentPanel.Visibility = hasProjects ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateStatus()
        {
            var running = _activeTasks.Count(t => t.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored);
            var planning = _activeTasks.Count(t => t.Status == AgentTaskStatus.Planning);
            var queued = _activeTasks.Count(t => t.Status == AgentTaskStatus.Queued);
            var waiting = _activeTasks.Count(t => t.Status == AgentTaskStatus.InitQueued);
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var locks = _fileLockManager.LockCount;
            var planningPart = planning > 0 ? $"  |  Planning: {planning}" : "";
            var waitingPart = waiting > 0 ? $"  |  Waiting: {waiting}" : "";

            if (!_projectManager.HasProjects)
            {
                Title = $"Spritely  |  No project  |  Add a project to get started  |  " +
                        $"Completed: {completed}  |  Failed: {failed}";
                return;
            }

            var projectName = Path.GetFileName(_projectManager.ProjectPath);
            Title = $"Spritely  |  {projectName}  |  Running: {running}{planningPart}  |  Queued: {queued}{waitingPart}  |  " +
                    $"Completed: {completed}  |  Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                    $"Locks: {locks}  |  {_projectManager.ProjectPath}";
        }

        private void CheckTaskTimeouts()
        {
            var runningTasks = _activeTasks.Where(t => t.Status is AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning).ToList();

            foreach (var task in runningTasks)
            {
                var timeoutMinutes = task.TimeoutMinutes ?? Constants.AppConstants.DefaultTaskTimeoutMinutes;
                var elapsed = DateTime.UtcNow - task.StartTime.ToUniversalTime();
                var elapsedMinutes = elapsed.TotalMinutes;

                var warningThreshold = timeoutMinutes * Constants.AppConstants.TaskTimeoutWarningPercent;
                if (elapsedMinutes >= warningThreshold && elapsedMinutes < timeoutMinutes && !task.HasTimeoutWarning)
                {
                    task.HasTimeoutWarning = true;
                    var warningMessage = $"\n\n⚠️ Task timeout warning: {Math.Round(elapsedMinutes, 1)}/{timeoutMinutes} minutes elapsed ({Math.Round(elapsedMinutes / timeoutMinutes * 100)}%)\n\n";
                    _outputTabManager.AppendColoredOutput(task.Id, warningMessage, Brushes.Orange, _activeTasks, _historyTasks);
                }

                if (elapsedMinutes >= timeoutMinutes)
                {
                    try
                    {
                        task.Process?.Kill();
                    }
                    catch (InvalidOperationException ex)
                    {
                        AppLogger.Error("MainWindow", $"Failed to kill timed-out process for task {task.Id}: {ex.Message}", ex);
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        AppLogger.Error("MainWindow", $"Failed to kill timed-out process for task {task.Id}: {ex.Message}", ex);
                    }

                    task.Status = AgentTaskStatus.Failed;
                    task.EndTime = DateTime.Now;

                    var timeoutMessage = $"\n\n❌ Task timed out after {timeoutMinutes} minutes and was automatically cancelled.\n\n";
                    _outputTabManager.AppendColoredOutput(task.Id, timeoutMessage, Brushes.Red, _activeTasks, _historyTasks);

                    MoveToHistory(task);
                }
            }
        }

        /// <summary>Updates the queue position for all InitQueued tasks based on their priority ordering.</summary>
        private void UpdateQueuePositions()
        {
            var initQueuedTasks = _activeTasks
                .Where(t => t.Status == AgentTaskStatus.InitQueued)
                .OrderByDescending(t => (int)t.PriorityLevel)
                .ThenByDescending(t => t.Priority)
                .ToList();

            for (int i = 0; i < initQueuedTasks.Count; i++)
            {
                var task = initQueuedTasks[i];
                var newPosition = i + 1;
                if (task.QueuePosition != newPosition)
                {
                    task.QueuePosition = newPosition;
                    _outputTabManager.UpdateTabHeader(task);
                }
            }

            foreach (var task in _activeTasks.Where(t => t.Status != AgentTaskStatus.InitQueued && t.QueuePosition > 0))
            {
                task.QueuePosition = 0;
                _outputTabManager.UpdateTabHeader(task);
            }
        }

    }
}
