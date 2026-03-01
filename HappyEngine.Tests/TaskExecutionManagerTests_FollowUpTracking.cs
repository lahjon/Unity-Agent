using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using HappyEngine;
using HappyEngine.Managers;
using HappyEngine.Models;
using NSubstitute;
using Xunit;

namespace HappyEngine.Tests
{
    public class TaskExecutionManagerTests_FollowUpTracking
    {
        [Fact]
        public void SendFollowUp_AppendsFollowUpPromptToTaskDescription()
        {
            // Arrange
            var scriptDir = Path.GetTempPath();
            var fileLockManager = Substitute.For<FileLockManager>();
            var outputTabManager = Substitute.For<OutputTabManager>();
            var gitHelper = Substitute.For<IGitHelper>();
            var completionAnalyzer = Substitute.For<ICompletionAnalyzer>();
            var promptBuilder = Substitute.For<IPromptBuilder>();
            var taskFactory = Substitute.For<ITaskFactory>();
            var getSystemPrompt = () => "system prompt";
            var getProjectDescription = (AgentTask task) => "project desc";
            var getProjectRulesBlock = (string path) => "rules";
            var isGameProject = (string path) => false;
            var messageBusManager = new MessageBusManager();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            promptBuilder.GetCliModelForTask(Arg.Any<AgentTask>()).Returns("haiku");

            var taskExecManager = new TaskExecutionManager(
                scriptDir,
                fileLockManager,
                outputTabManager,
                gitHelper,
                completionAnalyzer,
                promptBuilder,
                taskFactory,
                getSystemPrompt,
                getProjectDescription,
                getProjectRulesBlock,
                isGameProject,
                messageBusManager,
                dispatcher
            );

            var task = new AgentTask
            {
                Id = "test123",
                TaskNumber = 1,
                Description = "Initial task description",
                ProjectPath = scriptDir,
                Status = AgentTaskStatus.Completed,
                Runtime = new RuntimeTaskContext()
            };

            var activeTasks = new ObservableCollection<AgentTask> { task };
            var historyTasks = new ObservableCollection<AgentTask>();

            // Act - Send first follow-up
            taskExecManager.SendFollowUp(task, "First follow-up prompt", activeTasks, historyTasks);

            // Assert - Description should now include the follow-up
            Assert.Contains("Initial task description | Follow-up: First follow-up prompt", task.Description);

            // Act - Send second follow-up
            taskExecManager.SendFollowUp(task, "Second follow-up prompt", activeTasks, historyTasks);

            // Assert - Description should include both follow-ups
            Assert.Contains("First follow-up prompt", task.Description);
            Assert.Contains("Second follow-up prompt", task.Description);
        }

        [Fact]
        public void SendFollowUp_HandlesEmptyInitialDescription()
        {
            // Arrange
            var scriptDir = Path.GetTempPath();
            var fileLockManager = Substitute.For<FileLockManager>();
            var outputTabManager = Substitute.For<OutputTabManager>();
            var gitHelper = Substitute.For<IGitHelper>();
            var completionAnalyzer = Substitute.For<ICompletionAnalyzer>();
            var promptBuilder = Substitute.For<IPromptBuilder>();
            var taskFactory = Substitute.For<ITaskFactory>();
            var getSystemPrompt = () => "system prompt";
            var getProjectDescription = (AgentTask task) => "project desc";
            var getProjectRulesBlock = (string path) => "rules";
            var isGameProject = (string path) => false;
            var messageBusManager = new MessageBusManager();
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            promptBuilder.GetCliModelForTask(Arg.Any<AgentTask>()).Returns("haiku");

            var taskExecManager = new TaskExecutionManager(
                scriptDir,
                fileLockManager,
                outputTabManager,
                gitHelper,
                completionAnalyzer,
                promptBuilder,
                taskFactory,
                getSystemPrompt,
                getProjectDescription,
                getProjectRulesBlock,
                isGameProject,
                messageBusManager,
                dispatcher
            );

            var task = new AgentTask
            {
                Id = "test456",
                TaskNumber = 2,
                Description = "", // Empty initial description
                ProjectPath = scriptDir,
                Status = AgentTaskStatus.Completed,
                Runtime = new RuntimeTaskContext()
            };

            var activeTasks = new ObservableCollection<AgentTask> { task };
            var historyTasks = new ObservableCollection<AgentTask>();

            // Act
            taskExecManager.SendFollowUp(task, "Follow-up on empty task", activeTasks, historyTasks);

            // Assert - Description should now be the follow-up text
            Assert.Equal("Follow-up on empty task", task.Description);
        }
    }
}