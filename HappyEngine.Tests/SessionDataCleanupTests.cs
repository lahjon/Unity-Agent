using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using HappyEngine.Managers;
using Xunit;

namespace HappyEngine.Tests
{
    /// <summary>
    /// Tests verifying that session-specific data is properly cleaned up between sessions
    /// while persistent data is retained.
    /// </summary>
    public class SessionDataCleanupTests : IDisposable
    {
        private readonly string _testAppDataDir;
        private readonly string _testProjectPath;
        private readonly Dispatcher _dispatcher;
        private readonly MessageBusManager _messageBusManager;
        private readonly FileLockManager _fileLockManager;
        private readonly SettingsManager _settingsManager;
        private readonly Thread _dispatcherThread;
        private readonly ManualResetEventSlim _dispatcherReady = new();

        public SessionDataCleanupTests()
        {
            // Create test directories
            _testAppDataDir = Path.Combine(Path.GetTempPath(), $"HappyEngine_Test_{Guid.NewGuid()}");
            _testProjectPath = Path.Combine(Path.GetTempPath(), $"TestProject_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testAppDataDir);
            Directory.CreateDirectory(_testProjectPath);

            // Create dispatcher on a background thread
            _dispatcherThread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _dispatcherReady.Set();
                Dispatcher.Run();
            });
            _dispatcherThread.SetApartmentState(ApartmentState.STA);
            _dispatcherThread.Start();
            _dispatcherReady.Wait();

            // Initialize managers
            _messageBusManager = new MessageBusManager(_dispatcher);
            _fileLockManager = new FileLockManager(_dispatcher, new System.Collections.ObjectModel.ObservableCollection<FileLock>());
            _settingsManager = new SettingsManager(_testAppDataDir);
        }

        [Fact]
        public void AgentBus_IsDestroyed_WhenAllTasksComplete()
        {
            // Arrange
            var taskId1 = "test-task-1";
            var taskId2 = "test-task-2";
            var safeProjectName = MessageBusManager.GetSafeProjectName(_testProjectPath);
            var expectedBusDir = Path.Combine(MessageBusManager.AppDataBusRoot, safeProjectName);

            // Act - Join bus with two tasks
            _messageBusManager.JoinBus(_testProjectPath, taskId1, "Test Task 1");
            _messageBusManager.JoinBus(_testProjectPath, taskId2, "Test Task 2");

            // Assert - Bus directory should exist
            Assert.True(Directory.Exists(expectedBusDir), "Bus directory should exist when tasks are active");

            // Act - First task leaves
            _messageBusManager.LeaveBus(_testProjectPath, taskId1);

            // Assert - Bus should still exist with one participant
            Assert.True(Directory.Exists(expectedBusDir), "Bus directory should exist with one participant");

            // Act - Second task leaves
            _messageBusManager.LeaveBus(_testProjectPath, taskId2);
            Thread.Sleep(100); // Give time for directory deletion

            // Assert - Bus directory should be destroyed
            Assert.False(Directory.Exists(expectedBusDir), "Bus directory should be destroyed when all tasks complete");
        }

        [Fact]
        public void AgentBus_AllBusesDestroyed_OnDispose()
        {
            // Arrange
            var taskId = "test-task";
            var safeProjectName = MessageBusManager.GetSafeProjectName(_testProjectPath);
            var expectedBusDir = Path.Combine(MessageBusManager.AppDataBusRoot, safeProjectName);

            // Act - Join bus
            _messageBusManager.JoinBus(_testProjectPath, taskId, "Test Task");
            Assert.True(Directory.Exists(expectedBusDir), "Bus directory should exist");

            // Act - Dispose manager (simulates app shutdown)
            _messageBusManager.Dispose();
            Thread.Sleep(100); // Give time for directory deletion

            // Assert - Bus directory should be destroyed
            Assert.False(Directory.Exists(expectedBusDir), "Bus directory should be destroyed on dispose");
        }

        [Fact]
        public void FileLocks_AreReleased_WhenTaskCompletes()
        {
            // Arrange
            var taskId = "test-task";
            var filePath = Path.Combine(_testProjectPath, "test.cs");
            var normalizedPath = PathHelper.NormalizePath(filePath);

            // Act - Acquire lock
            _dispatcher.Invoke(() =>
            {
                var acquired = _fileLockManager.TryAcquireLock(filePath, taskId, 1, "Write",
                    new System.Collections.ObjectModel.ObservableCollection<AgentTask>());
                Assert.True(acquired, "Should acquire lock successfully");
            });

            // Assert - File should be locked
            Assert.True(_fileLockManager.IsFileLocked(normalizedPath), "File should be locked");

            // Act - Release task locks (simulates task completion)
            _fileLockManager.ReleaseTaskLocks(taskId);

            // Assert - File should no longer be locked
            Assert.False(_fileLockManager.IsFileLocked(normalizedPath), "File should not be locked after release");
        }

        [Fact]
        public void FileLocks_AllCleared_OnClearAll()
        {
            // Arrange
            var taskId1 = "task-1";
            var taskId2 = "task-2";
            var file1 = Path.Combine(_testProjectPath, "file1.cs");
            var file2 = Path.Combine(_testProjectPath, "file2.cs");

            // Act - Acquire multiple locks
            _dispatcher.Invoke(() =>
            {
                _fileLockManager.TryAcquireLock(file1, taskId1, 1, "Edit",
                    new System.Collections.ObjectModel.ObservableCollection<AgentTask>());
                _fileLockManager.TryAcquireLock(file2, taskId2, 2, "Write",
                    new System.Collections.ObjectModel.ObservableCollection<AgentTask>());
            });

            // Assert - Files should be locked
            Assert.True(_fileLockManager.IsFileLocked(PathHelper.NormalizePath(file1)));
            Assert.True(_fileLockManager.IsFileLocked(PathHelper.NormalizePath(file2)));

            // Act - Clear all (simulates app shutdown)
            _fileLockManager.ClearAll();

            // Assert - No files should be locked
            Assert.False(_fileLockManager.IsFileLocked(PathHelper.NormalizePath(file1)));
            Assert.False(_fileLockManager.IsFileLocked(PathHelper.NormalizePath(file2)));
        }

        [Fact]
        public async Task Settings_ArePersisted_BetweenSessions()
        {
            // Arrange - Set some settings
            _settingsManager.HistoryRetentionHours = 48;
            _settingsManager.LastSelectedProject = _testProjectPath;
            _settingsManager.MaxConcurrentTasks = 5;
            _settingsManager.AutoVerify = true;

            // Act - Save settings
            _settingsManager.SaveSettings(_testProjectPath);
            await Task.Delay(100); // Give time for background write

            // Create new settings manager (simulates new session)
            var newSettingsManager = new SettingsManager(_testAppDataDir);
            await newSettingsManager.LoadSettingsAsync();

            // Assert - Settings should be persisted
            Assert.Equal(48, newSettingsManager.HistoryRetentionHours);
            Assert.Equal(_testProjectPath, newSettingsManager.LastSelectedProject);
            Assert.Equal(5, newSettingsManager.MaxConcurrentTasks);
            Assert.True(newSettingsManager.AutoVerify);
        }

        [Fact]
        public void AgentBusRoot_UsesLocalAppData()
        {
            // Assert - Agent bus root should be in LocalAppData
            var expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HappyEngine", "agent-bus");
            Assert.Equal(expectedRoot, MessageBusManager.AppDataBusRoot);
        }

        public void Dispose()
        {
            // Clean up
            _dispatcher?.InvokeShutdown();
            _dispatcherThread?.Join(1000);
            _dispatcherReady?.Dispose();

            // Clean up test directories
            try
            {
                if (Directory.Exists(_testAppDataDir))
                    Directory.Delete(_testAppDataDir, true);
                if (Directory.Exists(_testProjectPath))
                    Directory.Delete(_testProjectPath, true);
            }
            catch { /* Ignore cleanup errors */ }
        }
    }
}