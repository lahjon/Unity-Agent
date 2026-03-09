using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Tasks Panel ─────────────────────────────────────────────

        private void InitializeTaskList()
        {
            // Defer task list initialization until the tab is actually shown
        }

        private void LoadTasksForDisplay()
        {
            if (_projectTaskManager == null) return;

            _projectTaskManager.CurrentProjectPath = _projectManager.ProjectPath;

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                TaskListControl.ItemsSource = new ObservableCollection<ProjectTaskItem>(_projectTaskManager.Tasks);
            }));
        }

        private void AddTask_Click(object sender, RoutedEventArgs e)
        {
            AddTaskItem();
        }

        private void TaskInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddTaskItem();
            }
        }

        private void AddTaskItem()
        {
            var text = TaskInputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text == TaskInputBox.Tag?.ToString())
                return;

            if (!_projectManager.HasProjects)
                return;

            try
            {
                _projectTaskManager.CurrentProjectPath = _projectManager.ProjectPath;
                var newTask = _projectTaskManager.AddTask(text);
                TaskInputBox.Clear();

                if (TaskListControl.ItemsSource is ObservableCollection<ProjectTaskItem> tasks)
                {
                    tasks.Add(newTask);
                }
                else
                {
                    LoadTasksForDisplay();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Tasks", "Failed to add task", ex);
            }
        }

        private async void TaskItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ProjectTaskItem task)
            {
                task.IsCompleted = !task.IsCompleted;

                if (task.IsCompleted)
                {
                    task.CompletedAt = DateTime.UtcNow;
                }
                else
                {
                    task.CompletedAt = null;
                }

                await _projectTaskManager.SaveTasksAsync();

                if (TaskListControl.ItemsSource is ObservableCollection<ProjectTaskItem> tasks)
                {
                    var index = tasks.IndexOf(task);
                    if (index >= 0)
                    {
                        tasks[index] = task;
                    }
                }
            }
        }

        private async void RemoveTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ProjectTaskItem task)
            {
                DependencyObject? current = button;
                while (current != null && current is not Border)
                {
                    current = VisualTreeHelper.GetParent(current);
                }

                if (current is Border border)
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                    fadeOut.Completed += async (s, args) =>
                    {
                        if (TaskListControl.ItemsSource is ObservableCollection<ProjectTaskItem> tasks)
                        {
                            tasks.Remove(task);
                        }
                        _projectTaskManager.RemoveTask(task);
                        await _projectTaskManager.SaveTasksAsync();
                    };
                    border.BeginAnimation(OpacityProperty, fadeOut);
                }
                else
                {
                    if (TaskListControl.ItemsSource is ObservableCollection<ProjectTaskItem> tasks)
                    {
                        tasks.Remove(task);
                    }
                    _projectTaskManager.RemoveTask(task);
                    await _projectTaskManager.SaveTasksAsync();
                }

                e.Handled = true;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;

                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
