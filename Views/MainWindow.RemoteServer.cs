using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        private RemoteServerManager? _remoteServer;

        private void InitializeRemoteServer()
        {
            _remoteServer = new RemoteServerManager(
                getActiveTasks: () => _activeTasks,
                getHistoryTasks: () => _historyTasks,
                getProjects: () => _projectManager.SavedProjects.ToList(),
                getMaxConcurrent: () => _settingsManager.MaxConcurrentTasks,
                findTask: FindTaskById,
                createTask: CreateTaskFromRemote,
                cancelTask: t => Dispatcher.Invoke(() => _taskExecutionManager.CancelTaskImmediate(t)),
                pauseTask: t => Dispatcher.Invoke(() => _taskExecutionManager.PauseTask(t)),
                resumeTask: t => Dispatcher.Invoke(() =>
                    _taskExecutionManager.ResumeTask(t, _activeTasks, _historyTasks, MoveToHistory))
            );

            _remoteServer.Log += msg => AppLogger.Info("RemoteServer", msg);
            _remoteServer.StatusChanged += running => Dispatcher.Invoke(() =>
            {
                if (RemoteServerToggle != null)
                    RemoteServerToggle.IsChecked = running;
                if (RemoteServerStatus != null)
                    RemoteServerStatus.Text = running
                        ? $"Running on port {_settingsManager.RemoteServerPort}"
                        : "Stopped";
            });

            // Auto-start if enabled in settings
            if (_settingsManager.RemoteServerEnabled)
                _remoteServer.Start(_settingsManager.RemoteServerPort);
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

        private void CreateTaskFromRemote(CreateTaskRequest request)
        {
            Dispatcher.Invoke(() =>
            {
                var model = Enum.TryParse<ModelType>(request.Model, true, out var m) ? m : ModelType.ClaudeCode;
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
            });
        }

        // ── UI Event Handlers ───────────────────────────────────────────

        private void RemoteServerToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_remoteServer == null) return;

            var enabled = RemoteServerToggle.IsChecked == true;
            _settingsManager.RemoteServerEnabled = enabled;

            if (enabled)
                _remoteServer.Start(_settingsManager.RemoteServerPort);
            else
                _remoteServer.Stop();

            _settingsManager.SaveSettings(_projectManager.ProjectPath);
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
    }
}
