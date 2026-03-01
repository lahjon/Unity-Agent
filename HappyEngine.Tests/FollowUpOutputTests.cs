using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using HappyEngine.Helpers;
using HappyEngine.Managers;
using HappyEngine.Models;
using Xunit;

namespace HappyEngine.Tests
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
            // Override the auto-generated ID
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "test-task-123");

            activeTasks.Add(task);

            // Act - Try to append output when no tab exists
            outputTabManager.AppendOutput(task.Id, "Test output message\n", activeTasks, historyTasks);

            // Assert - Tab should be created automatically
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));

            // Verify output was appended
            var box = outputTabManager.GetOutputBox(task.Id);
            Assert.NotNull(box);

            // Extract text from RichTextBox
            var textRange = new System.Windows.Documents.TextRange(
                box.Document.ContentStart,
                box.Document.ContentEnd);
            var text = textRange.Text.Trim();

            Assert.Contains("Test output message", text);
            });
        }

        [Fact]
        public void AppendOutput_FindsTaskInHistoryAndCreatesTab()
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
            // Override the auto-generated ID
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "history-task-456");

            // Task is in history, not active
            historyTasks.Add(task);

            // Act - Try to append output when task is in history
            outputTabManager.AppendOutput(task.Id, "Follow-up message\n", activeTasks, historyTasks);

            // Assert - Tab should be created even for history task
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));

            // Verify output was appended
            var box = outputTabManager.GetOutputBox(task.Id);
            Assert.NotNull(box);
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
        public void FollowUp_PreservesOutputAfterTabClose()
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
            // Override the auto-generated ID
            typeof(AgentTaskData).GetProperty("Id")!.SetValue(task.Data, "followup-task-789");

            activeTasks.Add(task);

            // Create tab and add initial output
            outputTabManager.CreateTab(task);
            outputTabManager.AppendOutput(task.Id, "Initial output\n", activeTasks, historyTasks);

            // Close the tab
            outputTabManager.CloseTab(task);
            Assert.False(outputTabManager.HasTab(task.Id));

            // Act - Send follow-up output after tab is closed
            outputTabManager.AppendOutput(task.Id, "Follow-up after close\n", activeTasks, historyTasks);

            // Assert - Tab should be recreated
            Assert.Single(tabControl.Items);
            Assert.True(outputTabManager.HasTab(task.Id));

            // Verify both outputs are in task's OutputBuilder
            Assert.Contains("Initial output", task.OutputBuilder.ToString());
            Assert.Contains("Follow-up after close", task.OutputBuilder.ToString());
            });
        }
    }
}