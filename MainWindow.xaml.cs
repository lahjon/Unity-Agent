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
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AgenticEngine.Dialogs;
using AgenticEngine.Games;
using AgenticEngine.Managers;
using AgenticEngine.Models;

namespace AgenticEngine
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
        private DispatcherTimer? _helperAnimTimer;
        private int _helperAnimTick;

        // Task numbering (1–9999, resets on app restart)
        private int _nextTaskNumber = 1;

        // Saved prompts
        private readonly List<SavedPromptEntry> _savedPrompts = new();

        // Chat
        private ChatManager _chatManager = null!;


        // Terminal collapse state
        private bool _terminalCollapsed = true;
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
                "AgenticEngine");
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
            _outputTabManager.InputSent += OnTabInputSent;

            // ProjectManager needs many UI refs — initialize after InitializeComponent.
            // Use a quick sync peek at settings for the initial project path (tiny file);
            // full async load of all data happens in Window_Loaded.
            _projectManager = new ProjectManager(
                appDataDir,
                PeekInitialProjectPath(appDataDir),
                PromptProjectLabel, AddProjectPath, ProjectListPanel,
                UseMcpToggle, ShortDescBox, LongDescBox, RuleInstructionBox,
                EditShortDescToggle, EditLongDescToggle, EditRuleInstructionToggle,
                ShortDescEditButtons, LongDescEditButtons, RuleInstructionEditButtons,
                ProjectRulesList,
                RegenerateDescBtn, Dispatcher);
            _projectManager.McpInvestigationRequested += OnMcpInvestigationRequested;
            _projectManager.ProjectSwapStarted += () => LoadingOverlay.Visibility = Visibility.Visible;
            _projectManager.ProjectSwapCompleted += () => LoadingOverlay.Visibility = Visibility.Collapsed;
            _projectManager.ProjectRenamed += OnProjectRenamed;

            _imageManager = new ImageAttachmentManager(appDataDir, ImageIndicator, ClearImagesBtn);
            _geminiService = new GeminiService(appDataDir);
            _claudeService = new ClaudeService(appDataDir);
            _chatManager = new ChatManager(
                ChatMessagesPanel, ChatScrollViewer, ChatInput, ChatSendBtn,
                ChatModelCombo, ChatExpandedPanel, ChatCollapsedStrip,
                ChatSplitter, ChatPanelCol, _claudeService, _geminiService);
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

            _activeTasks.CollectionChanged += (_, _) => UpdateTabCounts();
            _historyTasks.CollectionChanged += (_, _) => UpdateTabCounts();
            _storedTasks.CollectionChanged += (_, _) => UpdateTabCounts();
            UpdateTabCounts();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, _) =>
            {
                App.TraceUi("StatusTimer.Tick");
                foreach (var t in _activeTasks)
                    t.OnPropertyChanged(nameof(t.TimeInfo));
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

            // Games
            InitializeGames();
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
            catch { /* best-effort */ }

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
            catch { /* best-effort */ }

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
            catch (Exception ex) { Managers.AppLogger.Debug("MainWindow", $"DWM dark mode attribute failed: {ex.Message}"); }

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

                // Populate collections on the UI thread
                var historyItems = historyTask.Result;
                var storedItems = storedTask.Result;

                foreach (var item in historyItems)
                    _historyTasks.Add(item);
                foreach (var item in storedItems)
                    _storedTasks.Add(item);

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
            "AgenticEngine", "system_prompt.txt");

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
                _projectManager.RefreshProjectCombo();
                _projectManager.RefreshProjectList(
                    p => _terminalManager?.UpdateWorkingDirectory(p),
                    () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                    SyncSettingsForProject);
                _projectManager.UpdateMcpToggleForProject();
                var activeEntry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
                ProjectTypeGameToggle.IsChecked = activeEntry?.IsGame == true;
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

        private void ProjectTypeGameToggle_Changed(object sender, RoutedEventArgs e)
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry != null)
            {
                entry.IsGame = ProjectTypeGameToggle.IsChecked == true;
                _projectManager.SaveProjects();
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
            if (e.Key == Key.Enter)
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
            if (e.Key == Key.Enter) ApplyMaxConcurrentTasks();
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
            if (e.Key == Key.Enter) ApplyTokenLimitRetry();
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

        private void ViewDependencyGraph_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.DependencyGraphDialog.Show(_activeTasks, _fileLockManager);
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
            var isGemini = item.Tag?.ToString() == "Gemini";
            // Disable advanced options that don't apply to Gemini
            if (RemoteSessionToggle != null) RemoteSessionToggle.IsEnabled = !isGemini;
            if (HeadlessToggle != null) HeadlessToggle.IsEnabled = !isGemini;
            if (OvernightToggle != null) OvernightToggle.IsEnabled = !isGemini;
            if (SpawnTeamToggle != null) SpawnTeamToggle.IsEnabled = !isGemini;
            if (ExtendedPlanningToggle != null) ExtendedPlanningToggle.IsEnabled = !isGemini;
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

        private void ClearImages_Click(object sender, RoutedEventArgs e) => _imageManager.ClearImages();

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

            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }

        private void TaskCard_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("AgentTask")) return;
            if (sender is not FrameworkElement { DataContext: AgentTask target }) return;
            if (e.Data.GetData("AgentTask") is not AgentTask dragged) return;
            if (dragged.Id == target.Id || target.IsFinished || dragged.IsFinished) return;

            // Prevent duplicate dependency
            if (dragged.DependencyTaskIds.Contains(target.Id)) return;

            // Prevent circular dependency
            if (WouldCreateCircularDependency(dragged, target)) return;

            // Add dependency: dragged waits for target
            dragged.DependencyTaskIds.Add(target.Id);
            dragged.DependencyTaskNumbers.Add(target.TaskNumber);
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
                $"\n[AgenticEngine] Task #{dragged.TaskNumber} queued — waiting for #{target.TaskNumber} to complete.\n",
                _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(dragged);
            UpdateStatus();
            e.Handled = true;
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
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel);
            task.ProjectColor = _projectManager.GetProjectColor(task.ProjectPath);
            task.ProjectDisplayName = _projectManager.GetProjectDisplayName(task.ProjectPath);

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

            task.Summary = TaskLauncher.GenerateLocalSummary(desc!);
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);

            // Check if any dependencies are still active (not finished)
            var activeDeps = dependencies.Where(d => !d.IsFinished).ToList();
            if (activeDeps.Count > 0)
            {
                task.DependencyTaskIds = activeDeps.Select(d => d.Id).ToList();
                task.DependencyTaskNumbers = activeDeps.Select(d => d.TaskNumber).ToList();

                if (!task.PlanOnly)
                {
                    // Start in plan mode first, then queue when planning completes
                    task.IsPlanningBeforeQueue = true;
                    task.PlanOnly = true;
                    task.Status = AgentTaskStatus.Planning;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[AgenticEngine] Dependencies pending ({string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}) — starting in plan mode...\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }
                else
                {
                    // User explicitly wants plan-only — queue as before
                    task.Status = AgentTaskStatus.Queued;
                    task.QueuedReason = "Waiting for dependencies";
                    task.BlockedByTaskId = activeDeps[0].Id;
                    task.BlockedByTaskNumber = activeDeps[0].TaskNumber;
                    _outputTabManager.AppendOutput(task.Id,
                        $"[AgenticEngine] Task queued — waiting for dependencies: {string.Join(", ", activeDeps.Select(d => $"#{d.TaskNumber}"))}\n",
                        _activeTasks, _historyTasks);
                    _outputTabManager.UpdateTabHeader(task);
                }
            }
            else if (CountActiveSessionTasks() > _settingsManager.MaxConcurrentTasks)
            {
                // Max concurrent sessions reached — init-queue (no Claude session yet)
                task.Status = AgentTaskStatus.InitQueued;
                task.QueuedReason = "Max concurrent tasks reached";
                _outputTabManager.AppendOutput(task.Id,
                    $"[AgenticEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
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

        private void ExecuteGeminiTask(AgentTask task)
        {
            if (!_geminiService.IsConfigured)
            {
                Dialogs.DarkDialog.ShowConfirm(
                    "Gemini API key not configured.\n\nGo to Settings > Gemini tab to set your API key.\n" +
                    "Get one free at https://ai.google.dev/gemini-api/docs/api-key",
                    "Gemini Not Configured");
                return;
            }

            task.Cts?.Dispose();
            task.Cts = new System.Threading.CancellationTokenSource();

            task.Summary = "Generating Image...";
            AddActiveTask(task);
            _outputTabManager.CreateGeminiTab(task, _geminiService.GetImageDirectory());

            _ = RunGeminiImageGeneration(task);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task RunGeminiImageGeneration(AgentTask task)
        {
            var ct = task.Cts?.Token ?? System.Threading.CancellationToken.None;
            var progress = new Progress<string>(msg =>
            {
                _outputTabManager.AppendOutput(task.Id, msg, _activeTasks, _historyTasks);
            });

            _outputTabManager.AppendOutput(task.Id,
                $"[Gemini] Task #{task.TaskNumber} — Image generation\n" +
                $"[Gemini] Prompt: {task.Description}\n\n",
                _activeTasks, _historyTasks);

            try
            {
                var result = await _geminiService.GenerateImageAsync(task.Description, task.Id, progress, ct);

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
            catch (OperationCanceledException)
            {
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    "\n[Gemini] Generation cancelled.\n", _activeTasks, _historyTasks);
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


        // ── Tab Events ─────────────────────────────────────────────

        private void OutputTabs_SizeChanged(object sender, SizeChangedEventArgs e) => _outputTabManager.UpdateOutputTabWidths();

        private void OnTabCloseRequested(AgentTask task) => CloseTab(task);

        private void OnTabStoreRequested(AgentTask task)
        {
            // If the task is still active, cancel it first
            if (task.IsRunning || task.IsPlanning || task.IsPaused || task.IsQueued)
            {
                task.OvernightRetryTimer?.Stop();
                task.OvernightIterationTimer?.Stop();
                try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                TaskExecutionManager.KillProcess(task);
                task.Cts?.Dispose();
                task.Cts = null;
                _outputTabManager.AppendOutput(task.Id,
                    "\n[AgenticEngine] Task cancelled and stored.\n", _activeTasks, _historyTasks);
            }

            // Create the stored task entry
            var storedTask = new AgentTask
            {
                Description = task.Description,
                ProjectPath = task.ProjectPath,
                ProjectColor = task.ProjectColor,
                ProjectDisplayName = task.ProjectDisplayName,
                StoredPrompt = task.StoredPrompt ?? task.Description,
                SkipPermissions = task.SkipPermissions,
                StartTime = DateTime.Now
            };
            storedTask.Summary = !string.IsNullOrWhiteSpace(task.Summary)
                ? task.Summary : task.ShortDescription;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();

            // Clean up the active task and close the tab
            FinalizeTask(task);
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
                _outputTabManager.AppendOutput(task.Id, "\n[AgenticEngine] Task paused.\n", _activeTasks, _historyTasks);
            }
        }

        private void ToggleFileLock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            task.IgnoreFileLocks = !task.IgnoreFileLocks;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused or AgentTaskStatus.InitQueued || task.Status == AgentTaskStatus.Queued)
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

                    task.TokenLimitRetryTimer?.Stop();
                    task.TokenLimitRetryTimer = null;
                    task.Status = AgentTaskStatus.Cancelled;
                    task.EndTime = DateTime.Now;
                    TaskExecutionManager.KillProcess(task);
                }
            }
            else if (_activeTasks.Contains(task))
            {
                task.EndTime ??= DateTime.Now;
            }

            FinalizeTask(task);
        }

        // ── Task Actions ───────────────────────────────────────────

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (task.Status == AgentTaskStatus.InitQueued)
            {
                // Force-start an init-queued task (bypass max concurrent limit)
                task.Status = AgentTaskStatus.Running;
                task.QueuedReason = null;
                task.StartTime = DateTime.Now;
                _outputTabManager.AppendOutput(task.Id,
                    $"\n[AgenticEngine] Force-starting task #{task.TaskNumber} (limit bypassed)...\n\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                UpdateStatus();
                return;
            }

            if (task.Status == AgentTaskStatus.Queued)
            {
                if (task.DependencyTaskIds.Count > 0)
                {
                    // Force-start a dependency-queued task
                    task.QueuedReason = null;
                    task.BlockedByTaskId = null;
                    task.BlockedByTaskNumber = null;
                    task.DependencyTaskIds.Clear();
                    task.DependencyTaskNumbers.Clear();

                    if (task.Process is { HasExited: false })
                    {
                        // Resume suspended process (was queued via drag-drop)
                        _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[AgenticEngine] Force-resuming task #{task.TaskNumber} (dependencies skipped).\n\n",
                            _activeTasks, _historyTasks);
                    }
                    else
                    {
                        task.Status = AgentTaskStatus.Running;
                        task.StartTime = DateTime.Now;
                        _outputTabManager.AppendOutput(task.Id,
                            $"\n[AgenticEngine] Force-starting task #{task.TaskNumber} (dependencies skipped)...\n\n",
                            _activeTasks, _historyTasks);
                        _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                    }

                    _outputTabManager.UpdateTabHeader(task);
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

        private async void RevertTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;

            if (string.IsNullOrEmpty(task.GitStartHash))
            {
                DarkDialog.ShowAlert("No git snapshot was captured when this task started.\nRevert is not available.", "Revert Unavailable");
                return;
            }

            if (string.IsNullOrEmpty(task.ProjectPath) || !Directory.Exists(task.ProjectPath))
            {
                DarkDialog.ShowAlert("The project path for this task no longer exists.", "Revert Unavailable");
                return;
            }

            var shortHash = task.GitStartHash.Length > 7 ? task.GitStartHash[..7] : task.GitStartHash;
            if (!DarkDialog.ShowConfirm(
                $"This will hard-reset the project to the state before this task ran (commit {shortHash}).\n\n" +
                "All uncommitted changes and any commits made after that point will be lost.\n\n" +
                "Are you sure?",
                "Revert Task Changes"))
                return;

            try
            {
                var result = await TaskLauncher.RunGitCommandAsync(
                    task.ProjectPath, $"reset --hard {task.GitStartHash}");

                if (result != null)
                {
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[AgenticEngine] Reverted to commit {shortHash}.\n", _activeTasks, _historyTasks);
                    DarkDialog.ShowAlert($"Successfully reverted to commit {shortHash}.", "Revert Complete");
                }
                else
                {
                    DarkDialog.ShowAlert("Git reset failed. The commit may no longer exist or the repository state may have changed.", "Revert Failed");
                }
            }
            catch (Exception ex)
            {
                DarkDialog.ShowAlert($"Revert failed: {ex.Message}", "Revert Error");
            }
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
                    _outputTabManager.AppendOutput(task.Id, "\n[AgenticEngine] Task removed.\n", _activeTasks, _historyTasks);
                    AnimateRemoval(sender, () => MoveToHistory(task));
                }
                else
                {
                    MoveToHistory(task);
                }
                return;
            }

            if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused)
            {
                if (!DarkDialog.ShowConfirm(
                    $"Task #{task.TaskNumber} is still running.\nAre you sure you want to cancel it?",
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
            if (task.TokenLimitRetryTimer != null)
            {
                task.TokenLimitRetryTimer.Stop();
                task.TokenLimitRetryTimer = null;
            }
            try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            TaskExecutionManager.KillProcess(task);
            task.Cts?.Dispose();
            task.Cts = null;
            _outputTabManager.AppendOutput(task.Id, "\n[AgenticEngine] Task cancelled.\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            FinalizeTask(task);
        }

        private void RemoveHistoryTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            _outputTabManager.AppendOutput(task.Id, "\n[AgenticEngine] Task removed.\n", _activeTasks, _historyTasks);
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
            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Resumed session\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Original task: {task.Description}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Status: {task.StatusText}\n", _activeTasks, _historyTasks);
            if (!string.IsNullOrEmpty(task.ConversationId))
                _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Session: {task.ConversationId}\n", _activeTasks, _historyTasks);
            var resumeMethod = !string.IsNullOrEmpty(task.ConversationId) ? "--resume (session tracked)" : "--continue (no session ID)";
            _outputTabManager.AppendOutput(task.Id, $"\n[AgenticEngine] Type a follow-up message below. It will be sent with {resumeMethod}.\n", _activeTasks, _historyTasks);

            _historyTasks.Remove(task);
            _activeTasks.Insert(0, task);
            UpdateStatus();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            if (!task.HasRecommendations) return;

            if (!_outputTabManager.HasTab(task.Id))
                _outputTabManager.CreateTab(task);

            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Continuing with recommended next steps\n", _activeTasks, _historyTasks);
            _outputTabManager.AppendOutput(task.Id, $"[AgenticEngine] Project: {task.ProjectPath}\n", _activeTasks, _historyTasks);

            OutputTabs.SelectedItem = _outputTabManager.GetTab(task.Id);

            if (_historyTasks.Remove(task))
                _activeTasks.Insert(0, task);
            UpdateStatus();

            var prompt = "Continue with the recommended next steps from the previous task. Specifically:\n\n" + task.Recommendations;
            task.Recommendations = "";
            _taskExecutionManager.SendFollowUp(task, prompt, _activeTasks, _historyTasks);
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
                HeadlessToggle.IsChecked == true,
                OvernightToggle.IsChecked == true,
                IgnoreFileLocksToggle.IsChecked == true,
                UseMcpToggle.IsChecked == true,
                SpawnTeamToggle.IsChecked == true,
                ExtendedPlanningToggle.IsChecked == true,
                DefaultNoGitWriteToggle.IsChecked == true,
                PlanOnlyToggle.IsChecked == true,
                imagePaths: _imageManager.DetachImages(),
                model: selectedModel);
            newTask.ProjectColor = _projectManager.GetProjectColor(newTask.ProjectPath);
            newTask.ProjectDisplayName = _projectManager.GetProjectDisplayName(newTask.ProjectPath);
            newTask.Summary = $"Executing stored plan: {task.ShortDescription}";

            // Reset per-task toggles (same as Execute_Click)
            RemoteSessionToggle.IsChecked = false;
            HeadlessToggle.IsChecked = false;
            SpawnTeamToggle.IsChecked = false;
            OvernightToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;

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
                $"[AgenticEngine] Executing stored plan from task #{task.TaskNumber}\n",
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

        // ── Orchestration ──────────────────────────────────────────

        private void AddActiveTask(AgentTask task)
        {
            task.TaskNumber = _nextTaskNumber;
            _nextTaskNumber = _nextTaskNumber >= 9999 ? 1 : _nextTaskNumber + 1;

            // Insert below all finished tasks so finished stay on top
            int insertIndex = 0;
            while (insertIndex < _activeTasks.Count && _activeTasks[insertIndex].IsFinished)
                insertIndex++;
            _activeTasks.Insert(insertIndex, task);
            RefreshActivityDashboard();
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

        /// <summary>
        /// Consolidates all task teardown: releases locks, removes queued/streaming state,
        /// moves from active list to history, closes the output tab, and resumes any
        /// queued or dependency-blocked tasks. Every code path that finishes a task
        /// should funnel through here.
        /// </summary>
        private void FinalizeTask(AgentTask task, bool closeTab = true)
        {
            // If this was a plan-only task that completed successfully, create a stored task
            if (task.PlanOnly && task.Status == AgentTaskStatus.Completed)
                CreateStoredTaskFromPlan(task);

            // Release all resources associated with this task
            _fileLockManager.ReleaseTaskLocks(task.Id);
            _fileLockManager.RemoveQueuedInfo(task.Id);
            _taskExecutionManager.RemoveStreamingState(task.Id);

            // Move from active to history
            _activeTasks.Remove(task);
            _historyTasks.Insert(0, task);

            if (closeTab)
                _outputTabManager.CloseTab(task);

            _historyManager.SaveHistory(_historyTasks);
            RefreshActivityDashboard();
            RefreshFilterCombos();
            RefreshInlineProjectStats();
            UpdateStatus();

            // Resume tasks that were waiting on file locks or dependencies
            _fileLockManager.CheckQueuedTasks(_activeTasks);
            CheckDependencyQueuedTasks(task.Id);

            // Launch init-queued tasks now that a slot may be free
            DrainInitQueue();
        }

        // Keep MoveToHistory as a thin alias for callers that pass it as a delegate
        private void MoveToHistory(AgentTask task) => FinalizeTask(task);

        /// <summary>
        /// Counts tasks that have an active Claude session (Running or Paused — not Queued/InitQueued/finished).
        /// </summary>
        private int CountActiveSessionTasks()
        {
            return _activeTasks.Count(t => t.Status is AgentTaskStatus.Running or AgentTaskStatus.Planning or AgentTaskStatus.Paused);
        }

        /// <summary>
        /// Launches InitQueued tasks when slots become available under MaxConcurrentTasks.
        /// </summary>
        private void DrainInitQueue()
        {
            var max = _settingsManager.MaxConcurrentTasks;
            var toStart = new List<AgentTask>();

            foreach (var task in _activeTasks)
            {
                if (task.Status != AgentTaskStatus.InitQueued) continue;
                if (CountActiveSessionTasks() > max) break;

                toStart.Add(task);
            }

            foreach (var task in toStart)
            {
                task.Status = AgentTaskStatus.Running;
                task.QueuedReason = null;
                task.StartTime = DateTime.Now;

                _outputTabManager.AppendOutput(task.Id,
                    $"[AgenticEngine] Slot available — starting task #{task.TaskNumber}...\n\n",
                    _activeTasks, _historyTasks);
                _outputTabManager.UpdateTabHeader(task);
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            }

            if (toStart.Count > 0) UpdateStatus();
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
                // Gather context from completed dependencies before clearing them
                task.DependencyContext = TaskLauncher.BuildDependencyContext(
                    task.DependencyTaskIds, _activeTasks, _historyTasks);

                task.QueuedReason = null;
                task.BlockedByTaskId = null;
                task.BlockedByTaskNumber = null;
                task.DependencyTaskIds.Clear();
                task.DependencyTaskNumbers.Clear();

                if (task.Process is { HasExited: false })
                {
                    // Task was suspended via drag-drop — resume its process
                    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[AgenticEngine] All dependencies resolved — resuming task #{task.TaskNumber}.\n\n",
                        _activeTasks, _historyTasks);
                }
                else
                {
                    // No running process — start fresh
                    task.Status = AgentTaskStatus.Running;
                    task.StartTime = DateTime.Now;
                    _outputTabManager.AppendOutput(task.Id,
                        $"\n[AgenticEngine] All dependencies resolved — starting task #{task.TaskNumber}...\n\n",
                        _activeTasks, _historyTasks);
                    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
                }

                _outputTabManager.UpdateTabHeader(task);
                UpdateStatus();
            }
        }

        private void OnQueuedTaskResumed(string taskId)
        {
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            _outputTabManager.AppendOutput(taskId, $"\n[AgenticEngine] Resuming task #{task.TaskNumber} (blocking task finished)...\n\n", _activeTasks, _historyTasks);
            _outputTabManager.UpdateTabHeader(task);
            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            UpdateStatus();
        }

        private void OnTaskProcessCompleted(string taskId)
        {
            // Move finished task to top of active list
            var task = _activeTasks.FirstOrDefault(t => t.Id == taskId);
            if (task is { IsFinished: true })
            {
                var idx = _activeTasks.IndexOf(task);
                if (idx > 0)
                    _activeTasks.Move(idx, 0);
            }

            CheckDependencyQueuedTasks(taskId);
        }

        private void OnMcpInvestigationRequested(AgentTask task)
        {
            AddActiveTask(task);
            _outputTabManager.CreateTab(task);
            _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
            RefreshFilterCombos();
            UpdateStatus();
        }

        private void OnProjectRenamed(string projectPath, string newName)
        {
            foreach (var task in _activeTasks.Concat(_historyTasks).Concat(_storedTasks)
                         .Where(t => t.ProjectPath == projectPath))
            {
                task.ProjectDisplayName = newName;
            }
            _historyManager.SaveHistory(_historyTasks);
            _historyManager.SaveStoredTasks(_storedTasks);
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

        // ── Window Close ───────────────────────────────────────────

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

            // ── 1. Stop all timers so no callbacks fire during teardown ──
            _statusTimer.Stop();
            _helperAnimTimer?.Stop();

            // ── 2. Cancel in-flight async work ──
            _chatManager.CancelAndDispose();

            _helperManager?.CancelGeneration();

            // ── 3. Persist state (queues background writes via SafeFileWriter) ──
            _projectManager.SaveProjects();
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
            _historyManager.SaveHistory(_historyTasks);
            PersistSavedPrompts();

            // ── 4. Wait for all background file writes to complete ──
            Managers.SafeFileWriter.FlushAll(timeoutMs: 5000);

            // ── 5. Cancel CTS, stop overnight timers, kill & dispose processes ──
            foreach (var task in _activeTasks)
            {
                try { task.Cts?.Cancel(); } catch (ObjectDisposedException) { }

                task.OvernightRetryTimer?.Stop();
                task.OvernightRetryTimer = null;
                task.OvernightIterationTimer?.Stop();
                task.OvernightIterationTimer = null;

                TaskExecutionManager.KillProcess(task);

                try { task.Process?.Dispose(); } catch { }
                task.Process = null;

                task.Cts?.Dispose();
                task.Cts = null;
            }

            // ── 6. Clean up remaining state ──
            _fileLockManager.ClearAll();
            _taskExecutionManager.StreamingToolState.Clear();

            StopActiveGame();
            _terminalManager?.DisposeAll();
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

        // ── Chat splitter drag (Thumb-based, avoids GridSplitter star-sizing inversion) ──

        private void ChatSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double newSaved = SavedPanelCol.Width.Value + e.HorizontalChange;
            double newChat = ChatPanelCol.Width.Value - e.HorizontalChange;

            if (newSaved < SavedPanelCol.MinWidth || newChat < 0)
                return;

            SavedPanelCol.Width = new GridLength(newSaved);
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
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);
            var searchText = HistorySearchBox?.Text?.Trim() ?? "";
            var hasSearch = searchText.Length > 0;

            if (!hasProject && !hasStatus && !hasSearch)
                _historyView.Filter = null;
            else
                _historyView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    if (hasSearch && !TaskMatchesSearch(t, searchText)) return false;
                    return true;
                };
        }

        private void ActiveFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyActiveFilters();

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilters();

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
                    Background = (Brush)FindResource("BgElevated"),
                    BorderThickness = new Thickness(0),
                    ToolTip = game.GameDescription
                };
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = game.GameIcon,
                    Foreground = (Brush)FindResource("Accent"),
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                stack.Children.Add(new TextBlock
                {
                    Text = game.GameName,
                    Foreground = (Brush)FindResource("TextLight"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var template = new ControlTemplate(typeof(Button));
                var borderFactory = new FrameworkElementFactory(typeof(Border));
                borderFactory.Name = "Bd";
                borderFactory.SetValue(Border.BackgroundProperty, (Brush)FindResource("BgElevated"));
                borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                borderFactory.SetValue(Border.PaddingProperty, new Thickness(8));
                var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
                contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                borderFactory.AppendChild(contentFactory);
                template.VisualTree = borderFactory;

                var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                    (Brush)FindResource("BorderMedium"), "Bd"));
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
            if (MainTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent);
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

            var guidance = SuggestionGuidanceInput.Text?.Trim();
            await _helperManager.GenerateSuggestionsAsync(_projectManager.ProjectPath, category, guidance);
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
                HeadlessToggle.IsChecked == true,
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

            // Reset per-task toggles (same as Execute_Click)
            RemoteSessionToggle.IsChecked = false;
            HeadlessToggle.IsChecked = false;
            SpawnTeamToggle.IsChecked = false;
            OvernightToggle.IsChecked = false;
            ExtendedPlanningToggle.IsChecked = false;
            PlanOnlyToggle.IsChecked = false;

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
                    $"[AgenticEngine] Max concurrent tasks ({_settingsManager.MaxConcurrentTasks}) reached — task #{task.TaskNumber} waiting for a slot...\n",
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
            RenderSavedPrompts();
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

        // ── Saved Prompts ─────────────────────────────────────────

        private string SavedPromptsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticEngine", "saved_prompts.json");

        private async System.Threading.Tasks.Task LoadSavedPromptsAsync()
        {
            try
            {
                if (File.Exists(SavedPromptsFile))
                {
                    var json = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(SavedPromptsFile));
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
                var json = System.Text.Json.JsonSerializer.Serialize(_savedPrompts,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Managers.SafeFileWriter.WriteInBackground(SavedPromptsFile, json, "MainWindow");
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
            TaskInput.Text = string.Empty;
        }

        private void RenderSavedPrompts()
        {
            SavedPromptsPanel.Children.Clear();
            foreach (var entry in _savedPrompts)
            {
                var card = new Border
                {
                    Background = (Brush)FindResource("BgPopup"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 5, 6, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand,
                    Tag = entry.Id,
                };

                card.MouseEnter += (s, _) => card.Background = (Brush)FindResource("BgCardHover");
                card.MouseLeave += (s, _) => card.Background = (Brush)FindResource("BgPopup");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textBlock = new TextBlock
                {
                    Text = entry.DisplayName,
                    Foreground = (Brush)FindResource("TextLight"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = WrapTooltipText(entry.PromptText, 80),
                };
                Grid.SetColumn(textBlock, 0);

                var deleteBtn = new Button
                {
                    Content = "X",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("TextSubdued"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = entry.Id,
                };
                deleteBtn.MouseEnter += (s, _) => deleteBtn.Foreground = (Brush)FindResource("DangerDeleteHover");
                deleteBtn.MouseLeave += (s, _) => deleteBtn.Foreground = (Brush)FindResource("TextSubdued");
                deleteBtn.Click += DeleteSavedPrompt_Click;
                Grid.SetColumn(deleteBtn, 1);

                grid.Children.Add(textBlock);
                grid.Children.Add(deleteBtn);
                card.Child = grid;

                card.MouseLeftButtonDown += LoadSavedPrompt_Click;
                card.MouseDown += (s, ev) =>
                {
                    if (ev.ChangedButton == MouseButton.Middle)
                    {
                        var promptId = card.Tag as string;
                        _savedPrompts.RemoveAll(p => p.Id == promptId);
                        PersistSavedPrompts();
                        RenderSavedPrompts();
                        ev.Handled = true;
                    }
                };

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

        // ── Task Templates ────────────────────────────────────────

        private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
        {
            var name = Dialogs.DarkDialog.ShowTextInput("Save as Template", "Template name:");
            if (string.IsNullOrEmpty(name)) return;

            var modelTag = "ClaudeCode";
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
                modelTag = modelItem.Tag?.ToString() ?? "ClaudeCode";

            var template = new TaskTemplate
            {
                Name = name,
                Description = name,
                AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "",
                RemoteSession = RemoteSessionToggle.IsChecked == true,
                Headless = HeadlessToggle.IsChecked == true,
                SpawnTeam = SpawnTeamToggle.IsChecked == true,
                IsOvernight = OvernightToggle.IsChecked == true,
                ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true,
                PlanOnly = PlanOnlyToggle.IsChecked == true,
                IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true,
                UseMcp = UseMcpToggle.IsChecked == true,
                NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true,
                UseMessageBus = MessageBusToggle.IsChecked == true,
                Model = modelTag,
            };

            _settingsManager.TaskTemplates.Insert(0, template);
            _settingsManager.SaveTemplates();
            RenderTemplateCombo();
        }

        private void RenderTemplateCombo()
        {
            TemplateCombo.SelectionChanged -= TemplateCombo_Changed;
            TemplateCombo.Items.Clear();
            TemplateCombo.Items.Add(new ComboBoxItem { Content = "(No Template)", Tag = "" });

            foreach (var t in _settingsManager.TaskTemplates)
            {
                var item = new ComboBoxItem { Content = t.Name, Tag = t.Id };
                item.ToolTip = BuildTemplateTooltip(t);
                TemplateCombo.Items.Add(item);
            }

            // Add a "Manage..." option at the end
            TemplateCombo.Items.Add(new ComboBoxItem
            {
                Content = "Delete templates...",
                Tag = "__manage__",
                Foreground = (Brush)FindResource("TextMuted"),
                FontStyle = FontStyles.Italic
            });

            TemplateCombo.SelectedIndex = 0;
            TemplateCombo.SelectionChanged += TemplateCombo_Changed;
        }

        private static string BuildTemplateTooltip(TaskTemplate t)
        {
            var flags = new List<string>();
            if (t.RemoteSession) flags.Add("Remote");
            if (t.Headless) flags.Add("Headless");
            if (t.SpawnTeam) flags.Add("Team");
            if (t.IsOvernight) flags.Add("Overnight");
            if (t.ExtendedPlanning) flags.Add("ExtPlanning");
            if (t.PlanOnly) flags.Add("PlanOnly");
            if (t.UseMessageBus) flags.Add("MsgBus");
            if (t.NoGitWrite) flags.Add("NoGitWrite");
            if (t.IgnoreFileLocks) flags.Add("IgnoreLocks");
            if (t.UseMcp) flags.Add("MCP");
            var tooltip = flags.Count > 0 ? string.Join(", ", flags) : "(default settings)";
            if (!string.IsNullOrWhiteSpace(t.AdditionalInstructions))
                tooltip += "\n\nInstructions: " + (t.AdditionalInstructions.Length > 80
                    ? t.AdditionalInstructions[..80] + "..."
                    : t.AdditionalInstructions);
            return tooltip;
        }

        private void TemplateCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (TemplateCombo.SelectedItem is not ComboBoxItem selected) return;
            var tag = selected.Tag?.ToString();

            if (tag == "__manage__")
            {
                // Show template management
                TemplateCombo.SelectionChanged -= TemplateCombo_Changed;
                TemplateCombo.SelectedIndex = 0;
                TemplateCombo.SelectionChanged += TemplateCombo_Changed;
                ShowTemplateManagement();
                return;
            }

            if (string.IsNullOrEmpty(tag)) return;

            var template = _settingsManager.TaskTemplates.Find(t => t.Id == tag);
            if (template == null) return;

            // Apply template values to toggles
            RemoteSessionToggle.IsChecked = template.RemoteSession;
            HeadlessToggle.IsChecked = template.Headless;
            SpawnTeamToggle.IsChecked = template.SpawnTeam;
            OvernightToggle.IsChecked = template.IsOvernight;
            ExtendedPlanningToggle.IsChecked = template.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = template.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = template.IgnoreFileLocks;
            UseMcpToggle.IsChecked = template.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = template.NoGitWrite;
            MessageBusToggle.IsChecked = template.UseMessageBus;

            // Apply additional instructions
            AdditionalInstructionsInput.Text = template.AdditionalInstructions ?? "";

            // Apply model selection
            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (ModelCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == template.Model)
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            UpdateExecuteButtonText();
        }

        private void ShowTemplateManagement()
        {
            if (_settingsManager.TaskTemplates.Count == 0)
            {
                Dialogs.DarkDialog.ShowAlert("No templates saved yet.", "Task Templates");
                return;
            }

            var names = _settingsManager.TaskTemplates
                .Select((t, i) => $"{i + 1}. {t.Name}")
                .ToList();

            var input = Dialogs.DarkDialog.ShowTextInput(
                "Delete Template",
                "Enter the number of the template to delete:\n\n" + string.Join("\n", names));

            if (input != null && int.TryParse(input, out var idx) && idx >= 1 && idx <= _settingsManager.TaskTemplates.Count)
            {
                _settingsManager.TaskTemplates.RemoveAt(idx - 1);
                _settingsManager.SaveTemplates();
                RenderTemplateCombo();
            }
        }

        // ── Chat Panel (delegated to ChatManager) ─────────────────

        private void ChatToggle_Click(object sender, RoutedEventArgs e) => _chatManager.HandleToggleClick();
        private void NewChat_Click(object sender, RoutedEventArgs e) => _chatManager.HandleNewChat();
        private void ChatSend_Click(object sender, RoutedEventArgs e) => _chatManager.HandleSendClick();
        private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e) => _chatManager.HandleInputKeyDown(e);
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
