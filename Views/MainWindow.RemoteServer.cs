using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow : IRemoteServerCallbacks
    {
        private RemoteServerManager? _remoteServer;

        private void InitializeRemoteServer()
        {
            _remoteServer = new RemoteServerManager(this);

            _remoteServer.Log += msg => AppLogger.Info("RemoteServer", msg);
            _remoteServer.StatusChanged += running => Dispatcher.Invoke(() =>
            {
                UpdateRemoteServerUi(running);
            });
            _remoteServer.AuditCountChanged += count => Dispatcher.Invoke(() =>
            {
                AuditLogCount.Text = count.ToString();
                AuditLogBadge.Background = count > 0
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource("BgElevated");
                AuditLogCount.Foreground = count > 0
                    ? (Brush)FindResource("TextBright")
                    : (Brush)FindResource("TextMuted");
            });

            // Display the API key
            RemoteApiKeyBox.Text = _remoteServer.ApiKey;

            // Auto-start if enabled in settings
            if (_settingsManager.RemoteServerEnabled)
                _remoteServer.Start(_settingsManager.RemoteServerPort);

            // Sync initial UI state
            UpdateRemoteServerUi(_remoteServer.IsRunning);
        }

        // ── IRemoteServerCallbacks ───────────────────────────────────────

        ObservableCollection<AgentTask> IRemoteServerCallbacks.GetActiveTasks()
        {
            lock (_activeTasksLock) return new ObservableCollection<AgentTask>(_activeTasks);
        }

        ObservableCollection<AgentTask> IRemoteServerCallbacks.GetHistoryTasks()
        {
            lock (_historyTasksLock) return new ObservableCollection<AgentTask>(_historyTasks);
        }

        List<ProjectEntry> IRemoteServerCallbacks.GetProjects() =>
            _projectManager.SavedProjects.ToList();

        int IRemoteServerCallbacks.GetMaxConcurrentTasks() =>
            _settingsManager.MaxConcurrentTasks;

        AgentTask? IRemoteServerCallbacks.FindTask(string id) => FindTaskById(id);

        AgentTask? IRemoteServerCallbacks.CreateTask(CreateTaskRequest request) =>
            CreateTaskFromRemote(request);

        void IRemoteServerCallbacks.CancelTask(AgentTask task) =>
            Dispatcher.Invoke(() => _taskExecutionManager.CancelTaskImmediate(task));

        void IRemoteServerCallbacks.PauseTask(AgentTask task) =>
            Dispatcher.Invoke(() => _taskExecutionManager.PauseTask(task));

        void IRemoteServerCallbacks.ResumeTask(AgentTask task) =>
            Dispatcher.Invoke(() => _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks));

        private void UpdateRemoteServerUi(bool running)
        {
            if (RemoteServerToggle != null)
                RemoteServerToggle.IsChecked = running;

            RemoteServerStartBtn.IsEnabled = !running;
            RemoteServerStopBtn.IsEnabled = running;
            RemoteServerPortBox.IsEnabled = !running;

            if (running)
            {
                RemoteServerDot.Fill = new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B)); // green
                RemoteServerStatus.Text = $"Server Running — port {_settingsManager.RemoteServerPort}";
                RemoteServerIndicator.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x50, 0xFA, 0x7B));
            }
            else
            {
                RemoteServerDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)); // red
                RemoteServerStatus.Text = "Server Stopped";
                RemoteServerIndicator.BorderBrush = (Brush)FindResource("BgElevated");
            }
        }

        private AgentTask? FindTaskById(string id)
        {
            lock (_activeTasksLock)
            {
                var task = _activeTasks.FirstOrDefault(t => t.Data.Id == id);
                if (task != null) return task;
            }
            lock (_historyTasksLock)
            {
                return _historyTasks.FirstOrDefault(t => t.Data.Id == id);
            }
        }

        private AgentTask? CreateTaskFromRemote(CreateTaskRequest request)
        {
            return Dispatcher.Invoke(() =>
            {
                var model = !string.IsNullOrWhiteSpace(request.Model) && Enum.TryParse<ModelType>(request.Model, true, out var parsedModel)
                    ? parsedModel
                    : ModelType.ClaudeCode;
                var priority = Enum.TryParse<TaskPriority>(request.Priority, true, out var p) ? p : TaskPriority.Normal;

                var projectPath = request.ProjectPath;
                if (string.IsNullOrWhiteSpace(projectPath))
                    projectPath = _projectManager.ProjectPath;

                var task = _taskFactory.CreateTask(
                    description: request.Description,
                    projectPath: projectPath,
                    skipPermissions: true,
                    headless: false,
                    isFeatureMode: request.IsFeatureMode,
                    ignoreFileLocks: false,
                    useMcp: request.UseMcp,
                    extendedPlanning: request.ExtendedPlanning,
                    autoDecompose: request.AutoDecompose,
                    model: model);

                task.Data.PriorityLevel = priority;
                task.Data.TaskNumber = _nextTaskNumber++;

                lock (_activeTasksLock)
                    _activeTasks.Add(task);

                _outputTabManager.CreateTab(task);
                _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);

                return task;
            });
        }

        // ── UI Event Handlers ───────────────────────────────────────────

        private void RemoteServerStart_Click(object sender, RoutedEventArgs e)
        {
            if (_remoteServer == null || _remoteServer.IsRunning) return;

            _remoteServer.Start(_settingsManager.RemoteServerPort);
            _settingsManager.RemoteServerEnabled = true;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        private void RemoteServerStop_Click(object sender, RoutedEventArgs e)
        {
            if (_remoteServer == null || !_remoteServer.IsRunning) return;

            _remoteServer.Stop();
            _settingsManager.RemoteServerEnabled = false;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        // Keep hidden toggle handler for settings persistence compatibility
        private void RemoteServerToggle_Changed(object sender, RoutedEventArgs e) { }

        private void CopyApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (_remoteServer != null && !string.IsNullOrEmpty(_remoteServer.ApiKey))
            {
                Clipboard.SetText(_remoteServer.ApiKey);
                AppLogger.Info("RemoteServer", "API key copied to clipboard");
            }
        }

        private void ViewAuditLog_Click(object sender, RoutedEventArgs e)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spritely", "logs", "remote_audit.log");

            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("No audit log entries yet.", "Audit Log", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void RemoteServerPortBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && int.TryParse(tb.Text, out var port))
            {
                _settingsManager.RemoteServerPort = port;
                _settingsManager.SaveSettings(_projectManager.ProjectPath);

                // Restart server if running
                if (_remoteServer?.IsRunning == true)
                {
                    _remoteServer.Stop();
                    _remoteServer.Start(_settingsManager.RemoteServerPort);
                }
            }
        }

        private void RemoteServerPortBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        // ── APK Build ─────────────────────────────────────────────────────

        private async void BuildApkDebug_Click(object sender, RoutedEventArgs e)
            => await BuildApkAsync("assembleDebug", "debug");

        private async void BuildApkRelease_Click(object sender, RoutedEventArgs e)
            => await BuildApkAsync("assembleRelease", "release");

        private async Task BuildApkAsync(string gradleTask, string variant)
        {
            var projectRoot = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var repoRoot = FindRepoRoot(projectRoot);
            var remoteDir = repoRoot != null ? Path.Combine(repoRoot, "SpritelyRemote") : null;

            if (remoteDir == null || !Directory.Exists(remoteDir))
            {
                BuildApkStatus.Text = "SpritelyRemote folder not found.";
                return;
            }

            var gradlew = Path.Combine(remoteDir, "gradlew.bat");
            if (!File.Exists(gradlew))
            {
                BuildApkStatus.Text = "gradlew.bat not found in SpritelyRemote.";
                return;
            }

            BuildApkDebugBtn.IsEnabled = false;
            BuildApkReleaseBtn.IsEnabled = false;
            BuildApkStatus.Text = $"Building {variant} APK...";
            BuildApkStatus.Foreground = (Brush)FindResource("TextMuted");

            try
            {
                var (exitCode, output) = await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = gradlew,
                        Arguments = gradleTask,
                        WorkingDirectory = remoteDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) return (-1, "Failed to start Gradle process.");

                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(TimeSpan.FromMinutes(10));
                    return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                });

                if (exitCode == 0)
                {
                    var apkPath = Path.Combine(remoteDir, "app", "build", "outputs", "apk", variant);
                    BuildApkStatus.Foreground = (Brush)FindResource("Accent");
                    BuildApkStatus.Text = $"Build succeeded. APK at:\n{apkPath}";
                    AppLogger.Info("RemoteServer", $"APK build ({variant}) succeeded: {apkPath}");
                }
                else
                {
                    var lines = output.Split('\n');
                    var tail = string.Join("\n", lines[^Math.Min(5, lines.Length)..]);
                    BuildApkStatus.Foreground = (Brush)FindResource("TextLight");
                    BuildApkStatus.Text = $"Build failed (exit {exitCode}):\n{tail.Trim()}";
                    AppLogger.Warn("RemoteServer", $"APK build ({variant}) failed: exit {exitCode}");
                }
            }
            catch (Exception ex)
            {
                BuildApkStatus.Foreground = (Brush)FindResource("TextLight");
                BuildApkStatus.Text = $"Build error: {ex.Message}";
                AppLogger.Error("RemoteServer", $"APK build exception: {ex.Message}");
            }
            finally
            {
                BuildApkDebugBtn.IsEnabled = true;
                BuildApkReleaseBtn.IsEnabled = true;
            }
        }

        private static string? FindRepoRoot(string? dir)
        {
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }
    }
}
