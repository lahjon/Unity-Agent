using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Spritely.Helpers;
using Spritely.Managers;
using Spritely.Models;
using Xunit;

namespace Spritely.Tests
{
    public class FollowUpOutputTests
    {
        /// <summary>
        /// Runs an action on an STA thread, required for creating WPF objects
        /// </summary>
        private static void RunOnSta(Action action)
        {
            Exception? exception = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // Initialize WPF Application with resources if not already initialized
                    if (Application.Current == null)
                    {
                        var app = new Application();

                        // Add the required resources that OutputTabManager expects
                        app.Resources["BgAbyss"] = new SolidColorBrush(Color.FromRgb(20, 20, 20));
                        app.Resources["TextBody"] = new SolidColorBrush(Colors.White);
                        app.Resources["BgTerminalInput"] = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                        app.Resources["BgCard"] = new SolidColorBrush(Color.FromRgb(40, 40, 40));
                        app.Resources["Accent"] = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                        app.Resources["TextDisabled"] = new SolidColorBrush(Color.FromRgb(128, 128, 128));
                        app.Resources["BorderSubtle"] = new SolidColorBrush(Color.FromRgb(60, 60, 60));
                        app.Resources["TextLight"] = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                        app.Resources["Success"] = new SolidColorBrush(Color.FromRgb(0, 230, 118));

                        // Add the button style
                        var btnStyle = new Style(typeof(Button));
                        app.Resources["Btn"] = btnStyle;
                    }

                    action();
                }
                catch (Exception ex) { exception = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (exception != null)
                throw new AggregateException("STA thread exception", exception);
        }

        [Fact]
        public void AppendOutput_CreatesTabWhenMissing()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            var task = new AgentTask
            {
                Data = { Description = "Test Task", ProjectPath = "C:\\Test", Status = AgentTaskStatus.Running }
            };
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "test-task-123");

            activeTasks.Add(task);

            // Act - Try to append output when no tab exists
            outputTabManager.AppendOutput(task.Id, "Test output message\n", activeTasks, historyTasks);

            // Assert - Tab should be created automatically
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));

            // Verify output box was created and output tracked in task builder
            var box = outputTabManager.GetOutputBox(task.Id);
            Assert.NotNull(box);
            Assert.Contains("Test output message", task.OutputBuilder.ToString());
            });
        }

        [Fact]
        public void AppendOutput_DoesNotRecreateTabForFinishedHistoryTask()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            var task = new AgentTask
            {
                Data = { Description = "History Task", ProjectPath = "C:\\Test", Status = AgentTaskStatus.Completed }
            };
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "history-task-456");

            // Task is in history with finished status
            historyTasks.Add(task);

            // Act - Try to append output for a finished history task
            outputTabManager.AppendOutput(task.Id, "Follow-up message\n", activeTasks, historyTasks);

            // Assert - Tab should NOT be recreated for finished history tasks
            Assert.Empty(tabControl.Items);
            Assert.False(outputTabManager.HasTab(task.Id));
            });
        }

        [Fact]
        public void AppendOutput_RecreatesTabForRunningHistoryTask()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            var task = new AgentTask
            {
                Data = { Description = "History Task", ProjectPath = "C:\\Test", Status = AgentTaskStatus.Running }
            };
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "history-task-456");

            historyTasks.Add(task);

            // Act - Append output for a running task (e.g. follow-up just started)
            outputTabManager.AppendOutput(task.Id, "Follow-up message\n", activeTasks, historyTasks);

            // Assert - Tab should be created for running tasks
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));
            });
        }

        [Fact]
        public void AppendOutput_HandlesNonExistentTask()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            // Act - Try to append output for non-existent task
            outputTabManager.AppendOutput("non-existent-task", "Should not appear\n", activeTasks, historyTasks);

            // Assert - No tab should be created
            Assert.Empty(tabControl.Items);
            Assert.False(outputTabManager.HasTab("non-existent-task"));
            });
        }

        [Fact]
        public void FollowUp_DoesNotRecreateTabAfterCloseForFinishedTask()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            var task = new AgentTask
            {
                Data = { Description = "Follow-up Test Task", ProjectPath = "C:\\Test", Status = AgentTaskStatus.Completed }
            };
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "followup-task-789");

            activeTasks.Add(task);

            // Create tab and add initial output
            outputTabManager.CreateTab(task);
            outputTabManager.AppendOutput(task.Id, "Initial output\n", activeTasks, historyTasks);

            // Close the tab (simulating user removing the task)
            outputTabManager.CloseTab(task);
            Assert.False(outputTabManager.HasTab(task.Id));

            // Act - Stale output arrives after tab close (e.g. from process exit callback)
            outputTabManager.AppendOutput(task.Id, "Stale output\n", activeTasks, historyTasks);

            // Assert - Tab should NOT be recreated for finished tasks
            Assert.Empty(tabControl.Items);
            Assert.False(outputTabManager.HasTab(task.Id));
            });
        }

        [Fact]
        public void FollowUp_RecreatesTabAfterCloseWhenTaskRestarted()
        {
            RunOnSta(() =>
            {
            // Arrange
            var tabControl = new TabControl();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outputTabManager = new OutputTabManager(tabControl, dispatcher);

            var activeTasks = new ObservableCollection<AgentTask>();
            var historyTasks = new ObservableCollection<AgentTask>();

            var task = new AgentTask
            {
                Data = { Description = "Follow-up Test Task", ProjectPath = "C:\\Test", Status = AgentTaskStatus.Completed }
            };
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "followup-task-789");

            activeTasks.Add(task);

            // Create tab and add initial output
            outputTabManager.CreateTab(task);
            outputTabManager.AppendOutput(task.Id, "Initial output\n", activeTasks, historyTasks);

            // Close the tab
            outputTabManager.CloseTab(task);
            Assert.False(outputTabManager.HasTab(task.Id));

            // Simulate follow-up starting (status changes to Running before output)
            task.Status = AgentTaskStatus.Running;

            // Act - Follow-up output arrives
            outputTabManager.AppendOutput(task.Id, "Follow-up output\n", activeTasks, historyTasks);

            // Assert - Tab should be recreated for running tasks
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));
            });
        }
    }
}