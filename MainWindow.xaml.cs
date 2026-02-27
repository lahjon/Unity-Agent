using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace UnityAgent
{
    public partial class MainWindow : Window
    {
        private const string SystemPrompt =
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

        private readonly ObservableCollection<AgentTask> _activeTasks = new();
        private readonly ObservableCollection<AgentTask> _historyTasks = new();
        private readonly Dictionary<string, TabItem> _tabs = new();
        private readonly Dictionary<string, TextBox> _outputBoxes = new();
        private readonly DispatcherTimer _statusTimer;
        private readonly string _projectPath;
        private readonly string _historyFile;
        private readonly string _scriptDir;

        public MainWindow()
        {
            InitializeComponent();

            _projectPath = FindProjectPath();
            _historyFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityAgent", "task_history.json");
            _scriptDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityAgent", "scripts");
            Directory.CreateDirectory(_scriptDir);

            ActiveTasksList.ItemsSource = _activeTasks;
            HistoryTasksList.ItemsSource = _historyTasks;

            LoadHistory();

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
        }

        // ── Execute ────────────────────────────────────────────────

        private void TaskInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Execute_Click(sender, e);
                e.Handled = true;
            }
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            var desc = TaskInput.Text?.Trim();
            if (string.IsNullOrEmpty(desc)) return;

            var task = new AgentTask
            {
                Description = desc,
                SkipPermissions = SkipPermissionsToggle.IsChecked == true
            };

            _activeTasks.Add(task);
            CreateTab(task);
            StartProcess(task);
            TaskInput.Clear();
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
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1B)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };
            _outputBoxes[task.Id] = outputBox;

            // --- Input bar at bottom ---
            var inputBox = new TextBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var sendBtn = new Button
            {
                Content = "Send",
                Background = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
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
                Fill = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = task.ShortDescription,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
            };

            var closeBtn = new Button
            {
                Content = "\u00D7", // multiplication sign as X
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86)),
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
            panel.Children.Add(label);
            panel.Children.Add(closeBtn);
            return panel;
        }

        private void CloseTab(AgentTask task)
        {
            if (task.Status == AgentTaskStatus.Running)
            {
                var result = MessageBox.Show(
                    "This task is still running. Closing will terminate it.\n\nAre you sure?",
                    "Task Running",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;

                task.Status = AgentTaskStatus.Cancelled;
                task.EndTime = DateTime.Now;
                KillProcess(task);
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
            UpdateStatus();
        }

        private void UpdateTabHeader(AgentTask task)
        {
            if (!_tabs.TryGetValue(task.Id, out var tab)) return;
            if (tab.Header is StackPanel sp && sp.Children[0] is System.Windows.Shapes.Ellipse dot)
            {
                var color = task.Status switch
                {
                    AgentTaskStatus.Running => Color.FromRgb(0x89, 0xB4, 0xFA),
                    AgentTaskStatus.Completed => Color.FromRgb(0xA6, 0xE3, 0xA1),
                    AgentTaskStatus.Cancelled => Color.FromRgb(0xF3, 0x8B, 0xA8),
                    AgentTaskStatus.Failed => Color.FromRgb(0xF9, 0xE2, 0xAF),
                    _ => Color.FromRgb(0x6C, 0x70, 0x86)
                };
                dot.Fill = new SolidColorBrush(color);
            }
        }

        // ── Process Management ─────────────────────────────────────

        private void StartProcess(AgentTask task)
        {
            var skipFlag = task.SkipPermissions ? " --dangerously-skip-permissions" : "";
            var fullPrompt = SystemPrompt + task.Description;

            // Write prompt to a temp file to avoid all escaping headaches
            var promptFile = Path.Combine(_scriptDir, $"prompt_{task.Id}.txt");
            File.WriteAllText(promptFile, fullPrompt, Encoding.UTF8);

            // PowerShell script reads prompt from file, passes to claude -p
            var ps1File = Path.Combine(_scriptDir, $"task_{task.Id}.ps1");
            File.WriteAllText(ps1File,
                $"Set-Location -LiteralPath '{_projectPath}'\n" +
                $"$prompt = Get-Content -Raw -LiteralPath '{promptFile}'\n" +
                $"claude -p{skipFlag} $prompt\n",
                Encoding.UTF8);

            AppendOutput(task.Id, $"[UnityAgent] Starting task...\n");
            AppendOutput(task.Id, $"[UnityAgent] Project: {_projectPath}\n");
            AppendOutput(task.Id, $"[UnityAgent] Skip permissions: {task.SkipPermissions}\n");
            AppendOutput(task.Id, $"[UnityAgent] Waiting for claude...\n\n");

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
                var line = StripAnsi(e.Data);
                Dispatcher.BeginInvoke(() => AppendOutput(task.Id, line + "\n"));
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var line = StripAnsi(e.Data);
                Dispatcher.BeginInvoke(() => AppendOutput(task.Id, line + "\n"));
            };

            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var exitCode = -1;
                    try { exitCode = process.ExitCode; } catch { }

                    // Don't move to history — let the user manually end it
                    AppendOutput(task.Id, $"\n[UnityAgent] Process finished (exit code: {exitCode}). " +
                        "Use Done/Cancel to close, or send a follow-up.\n");
                    UpdateTabHeader(task);
                    CleanupScripts(task.Id);
                });
            };

            try
            {
                process.Start();
                task.Process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
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
                $"Set-Location -LiteralPath '{_projectPath}'\n" +
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

        private void AppendOutput(string taskId, string text)
        {
            if (!_outputBoxes.TryGetValue(taskId, out var box)) return;
            box.AppendText(text);
            box.ScrollToEnd();
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
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;
            KillProcess(task);
            AppendOutput(task.Id, "\n[UnityAgent] Task cancelled.\n");
            UpdateTabHeader(task);
            MoveToHistory(task);
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not AgentTask task) return;
            TaskInput.Text = task.Description;
            SkipPermissionsToggle.IsChecked = task.SkipPermissions;
            Execute_Click(sender, e);
        }

        private void MoveToHistory(AgentTask task)
        {
            _activeTasks.Remove(task);
            _historyTasks.Insert(0, task);
            SaveHistory();
            UpdateStatus();
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
                OutputTabs.SelectedItem = tab;
        }

        private void HistoryTask_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryTasksList.SelectedItem is AgentTask task && _tabs.TryGetValue(task.Id, out var tab))
                OutputTabs.SelectedItem = tab;
        }

        // ── Window Close ───────────────────────────────────────────

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_activeTasks.Count > 0)
            {
                var result = MessageBox.Show(
                    $"There are {_activeTasks.Count} active task(s) still running.\n\n" +
                    "Closing will terminate all of them. Continue?",
                    "Active Tasks Running",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            foreach (var task in _activeTasks)
                KillProcess(task);
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
                    SkipPermissions = t.SkipPermissions
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

                var cutoff = DateTime.Now.AddHours(-25);
                foreach (var entry in entries.Where(e => e.StartTime > cutoff))
                {
                    var task = new AgentTask
                    {
                        Description = entry.Description,
                        SkipPermissions = entry.SkipPermissions,
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime
                    };
                    task.Status = Enum.TryParse<AgentTaskStatus>(entry.Status, out var s)
                        ? s : AgentTaskStatus.Completed;

                    _historyTasks.Add(task);
                    CreateTab(task);
                    AppendOutput(task.Id, $"[History] {task.StatusText} | {task.Description}\n");
                }
            }
            catch { }
        }

        private void CleanupOldHistory()
        {
            var cutoff = DateTime.Now.AddHours(-25);
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

        // ── Helpers ────────────────────────────────────────────────

        private void UpdateStatus()
        {
            var active = _activeTasks.Count;
            var completed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var cancelled = _historyTasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var failed = _historyTasks.Count(t => t.Status == AgentTaskStatus.Failed);
            StatusText.Text = $"Active: {active}  |  Completed: {completed}  |  " +
                              $"Cancelled: {cancelled}  |  Failed: {failed}  |  " +
                              $"Project: {_projectPath}";
        }

        private static string StripAnsi(string text)
        {
            return Regex.Replace(text, @"\x1B(?:\[[0-9;]*[a-zA-Z]|\].*?(?:\x07|\x1B\\))", "");
        }

        private static string FindProjectPath()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var loomPath = Path.Combine(dir, "Loom");
                if (Directory.Exists(Path.Combine(loomPath, "Assets")))
                    return loomPath;
                dir = Path.GetDirectoryName(dir);
            }
            return Directory.GetCurrentDirectory();
        }

        private class TaskHistoryEntry
        {
            public string Description { get; set; } = "";
            public string Status { get; set; } = "";
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public bool SkipPermissions { get; set; }
        }
    }
}
