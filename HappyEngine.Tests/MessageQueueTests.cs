using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using HappyEngine.Managers;
using HappyEngine.Interfaces;
using Moq;

namespace HappyEngine.Tests
{
    public class MessageQueueTests
    {
        [Fact]
        public void RuntimeTaskContext_MessageQueue_WorksCorrectly()
        {
            // Arrange
            var runtime = new RuntimeTaskContext();

            // Act
            runtime.EnqueueMessage("First message");
            runtime.EnqueueMessage("Second message");
            runtime.EnqueueMessage("Third message");

            // Assert
            Assert.Equal(3, runtime.PendingMessageCount);
            Assert.Equal("First message", runtime.DequeueMessage());
            Assert.Equal(2, runtime.PendingMessageCount);
            Assert.Equal("Second message", runtime.DequeueMessage());
            Assert.Equal(1, runtime.PendingMessageCount);
            Assert.Equal("Third message", runtime.DequeueMessage());
            Assert.Equal(0, runtime.PendingMessageCount);
            Assert.Null(runtime.DequeueMessage());
        }

        [Fact]
        public void RuntimeTaskContext_IsProcessingMessage_ThreadSafe()
        {
            // Arrange
            var runtime = new RuntimeTaskContext();
            var results = new System.Collections.Concurrent.ConcurrentBag<bool>();

            // Act - Test concurrent access
            Parallel.For(0, 100, i =>
            {
                runtime.IsProcessingMessage = i % 2 == 0;
                results.Add(runtime.IsProcessingMessage);
            });

            // Assert - No exceptions and valid state
            Assert.Equal(100, results.Count);
        }

        [Fact]
        public void TaskExecutionManager_SendFollowUp_QueuesMessageWhenBusy()
        {
            // Arrange
            var task = new AgentTask
            {
                Description = "Test task",
                ProjectPath = @"C:\TestProject",
                Process = new System.Diagnostics.Process() // Mock process
            };
            task.Runtime.IsProcessingMessage = true; // Task is busy

            var activeTasks = new ObservableCollection<AgentTask> { task };
            var historyTasks = new ObservableCollection<AgentTask>();

            // Create mocks
            var mockOutputTabManager = new Mock<OutputTabManager>();
            var mockFileLockManager = new Mock<FileLockManager>();
            var mockGitHelper = new Mock<IGitHelper>();
            var mockCompletionAnalyzer = new Mock<ICompletionAnalyzer>();
            var mockPromptBuilder = new Mock<IPromptBuilder>();
            var mockMessageBusManager = new Mock<MessageBusManager>(string.Empty);

            var taskExecutionManager = new TaskExecutionManager(
                scriptDir: @"C:\Scripts",
                fileLockManager: mockFileLockManager.Object,
                outputTabManager: mockOutputTabManager.Object,
                getSystemPrompt: () => "System prompt",
                getProjectDescription: t => "Project desc",
                getProjectRulesBlock: p => "Rules",
                isGameProject: p => false,
                messageBusManager: mockMessageBusManager.Object,
                completionAnalyzer: mockCompletionAnalyzer.Object,
                promptBuilder: mockPromptBuilder.Object,
                gitHelper: mockGitHelper.Object,
                dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                taskFactory: null,
                getAutoVerify: () => false,
                getAutoCommit: () => false,
                getTokenLimitRetryMinutes: () => 30
            );

            // Act
            taskExecutionManager.SendFollowUp(task, "Test message", activeTasks, historyTasks);

            // Assert
            Assert.Equal(1, task.Runtime.PendingMessageCount);
            var queuedMessage = task.Runtime.DequeueMessage();
            Assert.Equal("Test message", queuedMessage);
        }

        [Fact]
        public void ProcessQueuedMessages_SendsNextMessageInQueue()
        {
            // Arrange
            var task = new AgentTask
            {
                Description = "Test task",
                ProjectPath = @"C:\TestProject",
                Process = new System.Diagnostics.Process()
            };
            task.Runtime.EnqueueMessage("Queued message 1");
            task.Runtime.EnqueueMessage("Queued message 2");
            task.Runtime.IsProcessingMessage = false; // Task is ready

            var activeTasks = new ObservableCollection<AgentTask> { task };
            var historyTasks = new ObservableCollection<AgentTask>();

            // Create mocks
            var mockOutputTabManager = new Mock<OutputTabManager>();
            var mockFileLockManager = new Mock<FileLockManager>();
            var mockGitHelper = new Mock<IGitHelper>();
            var mockCompletionAnalyzer = new Mock<ICompletionAnalyzer>();
            var mockPromptBuilder = new Mock<IPromptBuilder>();
            var mockMessageBusManager = new Mock<MessageBusManager>(string.Empty);

            var taskExecutionManager = new TaskExecutionManager(
                scriptDir: @"C:\Scripts",
                fileLockManager: mockFileLockManager.Object,
                outputTabManager: mockOutputTabManager.Object,
                getSystemPrompt: () => "System prompt",
                getProjectDescription: t => "Project desc",
                getProjectRulesBlock: p => "Rules",
                isGameProject: p => false,
                messageBusManager: mockMessageBusManager.Object,
                completionAnalyzer: mockCompletionAnalyzer.Object,
                promptBuilder: mockPromptBuilder.Object,
                gitHelper: mockGitHelper.Object,
                dispatcher: System.Windows.Threading.Dispatcher.CurrentDispatcher,
                taskFactory: null,
                getAutoVerify: () => false,
                getAutoCommit: () => false,
                getTokenLimitRetryMinutes: () => 30
            );

            // Act
            taskExecutionManager.ProcessQueuedMessages(task, activeTasks, historyTasks);

            // Assert
            Assert.Equal(1, task.Runtime.PendingMessageCount); // One message was processed, one remains
        }
    }
}