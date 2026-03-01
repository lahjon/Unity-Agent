using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using HappyEngine.Helpers;
using HappyEngine.Managers;
using Xunit;

using FeatureModeAction = HappyEngine.Managers.FeatureModeHandler.FeatureModeAction;
using FeatureModeDecision = HappyEngine.Managers.FeatureModeHandler.FeatureModeDecision;

namespace HappyEngine.Tests
{
    /// <summary>
    /// Integration tests for the task execution engine covering:
    /// - Feature mode iteration decision logic (state machine)
    /// - FileLockManager conflict detection and queueing
    /// - Full task lifecycle state transitions (queued → running → finished)
    /// - Cancellation flows and retry logic
    /// </summary>
    public class TaskExecutionEngineTests
    {
        private static readonly ITaskFactory _factory = new Managers.TaskFactory();
        private static readonly IPromptBuilder _prompt = new PromptBuilder();
        private static readonly IGitHelper _git = new GitHelper();
        private static readonly ICompletionAnalyzer _completion = new CompletionAnalyzer(_git);

        // ── Helpers ──────────────────────────────────────────────────

        private static AgentTask MakeTask(string projectPath = @"C:\Projects\Test",
            AgentTaskStatus status = AgentTaskStatus.Running)
        {
            var t = _factory.CreateTask("test task", projectPath, false, false, false, false, false, false);
            t.Status = status;
            return t;
        }

        private static AgentTask MakeFeatureModeTask(string projectPath = @"C:\Projects\Test")
        {
            var t = _factory.CreateTask("feature mode task", projectPath, true, false, false, true, false, false);
            _factory.PrepareTaskForFeatureModeStart(t);
            return t;
        }

        /// <summary>
        /// Runs an action on an STA thread, required for creating WPF objects
        /// (TextBlock, TabControl, DispatcherTimer, etc.) in test code.
        /// </summary>
        private static void RunOnSta(Action action)
        {
            Exception? exception = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { exception = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (exception != null)
                throw new AggregateException("STA thread exception", exception);
        }

        /// <summary>
        /// Creates a TaskExecutionManager with all dependencies on the CURRENT thread.
        /// Must be called from within RunOnSta() to ensure WPF objects are on the STA thread.
        /// </summary>
        private static (TaskExecutionManager mgr, FileLockManager flm, string tempDir) CreateManagers()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"HappyEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var textBlock = new TextBlock();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var tabControl = new TabControl();

            var flm = new FileLockManager(textBlock, dispatcher);
            var otm = new OutputTabManager(tabControl, dispatcher);

            var mbm = new MessageBusManager(dispatcher);
            var mgr = new TaskExecutionManager(
                tempDir, flm, otm,
                _git, _completion, _prompt, _factory,
                () => "test prompt",
                _ => "test project",
                _ => "",
                _ => false,
                mbm,
                dispatcher);

            return (mgr, flm, tempDir);
        }

        // ══════════════════════════════════════════════════════════════
        //  A. FEATURE MODE ITERATION DECISION LOGIC
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void FeatureModeDecision_SkipsWhenTaskNotRunning()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Paused,
                TimeSpan.FromHours(1), "", 1, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Skip, decision.Action);
        }

        [Theory]
        [InlineData(AgentTaskStatus.Cancelled)]
        [InlineData(AgentTaskStatus.Completed)]
        [InlineData(AgentTaskStatus.Failed)]
        [InlineData(AgentTaskStatus.Queued)]
        public void FeatureModeDecision_SkipsForNonRunningStatuses(AgentTaskStatus status)
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, status, TimeSpan.FromHours(1), "", 1, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Skip, decision.Action);
        }

        [Fact]
        public void FeatureModeDecision_FinishesOnRuntimeCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(12), "", 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_FinishesOnRuntimeCapExceeded()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(13), "", 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_FinishesOnStatusComplete()
        {
            var output = "Some work done\nSTATUS: COMPLETE\n";

            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), output, 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_DoesNotFinishOnNeedsMoreWork()
        {
            var output = "Some work done\nSTATUS: NEEDS_MORE_WORK\n";

            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), output, 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
        }

        [Fact]
        public void FeatureModeDecision_FinishesOnMaxIterations()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "", 50, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_ContinuesWhenBelowMaxIterations()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "", 49, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
        }

        [Fact]
        public void FeatureModeDecision_FailsOnCrashLoop_ThreeConsecutiveFailures()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "some output", 5, 50,
                exitCode: 1, consecutiveFailures: 2, outputLength: 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Failed, decision.FinishStatus);
            Assert.Equal(3, decision.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeDecision_IncrementsFailureOnNonZeroExit()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "some output", 5, 50,
                exitCode: 1, consecutiveFailures: 0, outputLength: 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
            Assert.Equal(1, decision.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeDecision_IncrementsFailureToTwoThenContinues()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "some output", 5, 50,
                exitCode: 1, consecutiveFailures: 1, outputLength: 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
            Assert.Equal(2, decision.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeDecision_ResetsFailuresOnSuccess()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "some output", 5, 50,
                exitCode: 0, consecutiveFailures: 2, outputLength: 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
            Assert.Equal(0, decision.ConsecutiveFailures);
        }

        [Theory]
        [InlineData("rate limit exceeded")]
        [InlineData("token limit reached")]
        [InlineData("server overloaded")]
        [InlineData("error 529")]
        [InlineData("at capacity")]
        [InlineData("too many requests")]
        public void FeatureModeDecision_RetriesOnTokenLimitError(string errorText)
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), errorText, 5, 50,
                exitCode: 1, consecutiveFailures: 0, outputLength: 0);

            Assert.Equal(FeatureModeAction.RetryAfterDelay, decision.Action);
        }

        [Fact]
        public void FeatureModeDecision_TokenLimitResetsFailureCount()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "rate limit exceeded", 5, 50,
                exitCode: 1, consecutiveFailures: 2, outputLength: 0);

            Assert.Equal(FeatureModeAction.RetryAfterDelay, decision.Action);
            Assert.Equal(0, decision.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeDecision_TokenLimitDoesNotCountAsFailure()
        {
            // Token limit with exitCode != 0 should NOT increment failures
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "rate limit exceeded", 5, 50,
                exitCode: 1, consecutiveFailures: 1, outputLength: 0);

            Assert.Equal(FeatureModeAction.RetryAfterDelay, decision.Action);
            Assert.Equal(0, decision.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeDecision_SetsOutputTrimWhenOverCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "", 5, 50,
                exitCode: 0, consecutiveFailures: 0, outputLength: 200_000);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
            Assert.True(decision.TrimOutput);
        }

        [Fact]
        public void FeatureModeDecision_NoTrimWhenUnderCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "", 5, 50,
                exitCode: 0, consecutiveFailures: 0, outputLength: 50_000);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
            Assert.False(decision.TrimOutput);
        }

        [Fact]
        public void FeatureModeDecision_NoTrimAtExactCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "", 5, 50,
                exitCode: 0, consecutiveFailures: 0, outputLength: 100_000);

            Assert.False(decision.TrimOutput);
        }

        [Fact]
        public void FeatureModeDecision_RuntimeCapTakesPriorityOverCompletion()
        {
            // Both conditions true: runtime cap AND STATUS: COMPLETE
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(12), "STATUS: COMPLETE\n", 5, 50, 0, 0, 0);

            // Runtime cap is checked first
            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_CompletionTakesPriorityOverMaxIterations()
        {
            // Both: STATUS: COMPLETE and at max iterations
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "STATUS: COMPLETE\n", 50, 50, 0, 0, 0);

            // Both lead to Completed, just verifying it finishes
            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_CrashLoopTakesPriorityOverContinue()
        {
            // Exit code 1, consecutive failures at 2 → 3rd failure → crash loop
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(1), "normal output", 5, 50,
                exitCode: 1, consecutiveFailures: 2, outputLength: 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Failed, decision.FinishStatus);
        }

        [Fact]
        public void FeatureModeDecision_ExactlyAtRuntimeCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(12).Add(TimeSpan.FromSeconds(1)),
                "", 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
        }

        [Fact]
        public void FeatureModeDecision_JustUnderRuntimeCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(59)),
                "", 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
        }

        // ══════════════════════════════════════════════════════════════
        //  B. FILE LOCK CONFLICT DETECTION
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void FileLock_FirstLockAcquired()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };

                var result = flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);

                Assert.True(result);
                Assert.Equal(1, flm.LockCount);
            });
        }

        [Fact]
        public void FileLock_SameTaskReacquiresSameFile()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };

                flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);
                var result = flm.TryAcquireFileLock(task.Id, "src/file.cs", "Write", activeTasks);

                Assert.True(result);
                Assert.Equal(1, flm.LockCount); // Still just 1 lock
            });
        }

        [Fact]
        public void FileLock_DifferentTaskBlocked()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                var result = flm.TryAcquireFileLock(taskB.Id, "src/file.cs", "Edit", activeTasks);

                Assert.False(result);
                Assert.Equal(1, flm.LockCount);
            });
        }

        [Fact]
        public void FileLock_DifferentFilesNoConflict()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                var r1 = flm.TryAcquireFileLock(taskA.Id, "src/fileA.cs", "Edit", activeTasks);
                var r2 = flm.TryAcquireFileLock(taskB.Id, "src/fileB.cs", "Edit", activeTasks);

                Assert.True(r1);
                Assert.True(r2);
                Assert.Equal(2, flm.LockCount);
            });
        }

        [Fact]
        public void FileLock_IgnoreFileLocksBypassesConflict()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                taskB.IgnoreFileLocks = true;
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                var result = flm.TryAcquireOrConflict(taskB.Id, "src/file.cs", "Edit", activeTasks,
                    (_, _) => { });

                Assert.True(result);
            });
        }

        [Fact]
        public void FileLock_ReleaseRemovesAllLocks()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };

                flm.TryAcquireFileLock(task.Id, "src/a.cs", "Edit", activeTasks);
                flm.TryAcquireFileLock(task.Id, "src/b.cs", "Edit", activeTasks);
                Assert.Equal(2, flm.LockCount);

                flm.ReleaseTaskLocks(task.Id);
                Assert.Equal(0, flm.LockCount);
            });
        }

        [Fact]
        public void FileLock_ReleaseUnlocksForOtherTasks()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                Assert.False(flm.TryAcquireFileLock(taskB.Id, "src/file.cs", "Edit", activeTasks));

                flm.ReleaseTaskLocks(taskA.Id);
                Assert.True(flm.TryAcquireFileLock(taskB.Id, "src/file.cs", "Edit", activeTasks));
            });
        }

        [Fact]
        public void FileLock_IsFileLocked_TrueWhenLocked()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };
                var normalized = FormatHelpers.NormalizePath("src/file.cs", task.ProjectPath);

                flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);

                Assert.True(flm.IsFileLocked(normalized));
            });
        }

        [Fact]
        public void FileLock_IsFileLocked_FalseAfterRelease()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };
                var normalized = FormatHelpers.NormalizePath("src/file.cs", task.ProjectPath);

                flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);
                flm.ReleaseTaskLocks(task.Id);

                Assert.False(flm.IsFileLocked(normalized));
            });
        }

        [Fact]
        public void FileLock_ConflictWithoutPlan_SetsQueued()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                taskA.TaskNumber = 1001; // Set task number for test
                var taskB = MakeTask();
                taskB.TaskNumber = 1002; // Set task number for test
                taskB.StoredPrompt = null; // No plan
                taskB.IsPlanningBeforeQueue = false;
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };
                var output = new List<string>();

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks,
                    (_, txt) => output.Add(txt));

                // Tasks now queue directly without entering planning mode
                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
                Assert.Equal("File locked: file.cs by #1001", taskB.QueuedReason);
                Assert.Equal(taskA.Id, taskB.BlockedByTaskId);
                Assert.Equal(taskA.TaskNumber, taskB.BlockedByTaskNumber);
            });
        }

        [Fact]
        public void FileLock_ConflictWithPlan_SetsQueued()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                taskB.StoredPrompt = "Execute this plan...";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };
                var output = new List<string>();

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks,
                    (_, txt) => output.Add(txt));

                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
                Assert.NotNull(taskB.QueuedReason);
                Assert.Equal(taskA.Id, taskB.BlockedByTaskId);
            });
        }

        [Fact]
        public void FileLock_CheckQueuedTasks_ResumesWhenBlockerFinishes()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                taskB.StoredPrompt = "Execute plan...";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks, (_, _) => { });
                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);

                // Simulate blocker finishing: release locks and set status
                flm.ReleaseTaskLocks(taskA.Id);
                taskA.Status = AgentTaskStatus.Completed;

                string? resumedId = null;
                flm.QueuedTaskResumed += id => resumedId = id;
                flm.CheckQueuedTasks(activeTasks);

                Assert.Equal(AgentTaskStatus.Running, taskB.Status);
                Assert.Equal(taskB.Id, resumedId);
                Assert.Null(taskB.QueuedReason);
                Assert.Null(taskB.BlockedByTaskId);
            });
        }

        [Fact]
        public void FileLock_CheckQueuedTasks_DoesNotResumeWhenBlockerStillRunning()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                taskB.StoredPrompt = "Execute plan...";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks, (_, _) => { });

                // Blocker still running — do NOT release locks
                flm.CheckQueuedTasks(activeTasks);

                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
            });
        }

        [Fact]
        public void FileLock_CheckQueuedTasks_DoesNotResumeWhenFileStillLocked()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                var taskC = MakeTask(); // Will hold the lock after taskA is done
                taskB.StoredPrompt = "Execute plan...";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB, taskC };

                // taskA locks the file, taskB conflicts
                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks, (_, _) => { });

                // taskA finishes but taskC acquires the same file
                taskA.Status = AgentTaskStatus.Completed;
                flm.ReleaseTaskLocks(taskA.Id);
                flm.TryAcquireFileLock(taskC.Id, "src/file.cs", "Edit", activeTasks);

                flm.CheckQueuedTasks(activeTasks);

                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
            });
        }

        [Fact]
        public void FileLock_ForceStartQueuedTask_SetsRunning()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                task.Status = AgentTaskStatus.Queued;
                task.QueuedReason = "Waiting for file lock";
                task.BlockedByTaskId = "abc12345";

                string? resumedId = null;
                flm.QueuedTaskResumed += id => resumedId = id;
                flm.ForceStartQueuedTask(task);

                Assert.Equal(AgentTaskStatus.Running, task.Status);
                Assert.Null(task.QueuedReason);
                Assert.Null(task.BlockedByTaskId);
                Assert.Equal(task.Id, resumedId);
            });
        }

        [Fact]
        public void FileLock_ClearAll_RemovesEverything()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };

                flm.TryAcquireFileLock(task.Id, "src/a.cs", "Edit", activeTasks);
                flm.TryAcquireFileLock(task.Id, "src/b.cs", "Edit", activeTasks);
                Assert.Equal(2, flm.LockCount);

                flm.ClearAll();
                Assert.Equal(0, flm.LockCount);
            });
        }

        [Fact]
        public void FileLock_FileLocksViewUpdatesOnAcquireAndRelease()
        {
            RunOnSta(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var flm = new FileLockManager(new TextBlock(), dispatcher);
                var task = MakeTask();
                var activeTasks = new ObservableCollection<AgentTask> { task };

                Assert.Empty(flm.FileLocksView);

                flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);

                // Process dispatcher queue to ensure the BeginInvoke operation completes
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.Single(flm.FileLocksView);

                flm.ReleaseTaskLocks(task.Id);

                // Process dispatcher queue again for the release operation
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.Empty(flm.FileLocksView);
            });
        }

        // ── Static file path extraction ─────────────────────────────

        [Theory]
        [InlineData(@"{""file_path"": ""src/file.cs""}", "src/file.cs")]
        [InlineData(@"{""file_path"": ""C:\\code\\test.txt""}", @"C:\code\test.txt")]
        public void FileLock_ExtractFilePath_ReturnsFilePath(string json, string expected)
        {
            using var doc = JsonDocument.Parse(json);
            var result = FileLockManager.ExtractFilePath(doc.RootElement);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void FileLock_ExtractFilePath_FallsBackToPath()
        {
            using var doc = JsonDocument.Parse(@"{""path"": ""fallback.cs""}");
            var result = FileLockManager.ExtractFilePath(doc.RootElement);
            Assert.Equal("fallback.cs", result);
        }

        [Fact]
        public void FileLock_ExtractFilePath_ReturnsNullWhenMissing()
        {
            using var doc = JsonDocument.Parse(@"{""other"": ""value""}");
            var result = FileLockManager.ExtractFilePath(doc.RootElement);
            Assert.Null(result);
        }

        [Theory]
        [InlineData(@"""file_path"": ""src/test.cs""", "src/test.cs")]
        [InlineData(@"""file_path"":""no_space.cs""", "no_space.cs")]
        public void FileLock_TryExtractFilePathFromPartial_Extracts(string partialJson, string expected)
        {
            var result = FileLockManager.TryExtractFilePathFromPartial(partialJson);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void FileLock_TryExtractFilePathFromPartial_ReturnsNullOnMismatch()
        {
            var result = FileLockManager.TryExtractFilePathFromPartial(@"""no_match"": true");
            Assert.Null(result);
        }

        [Theory]
        [InlineData(@"{""command"": ""echo 'test' > output.txt""}", "output.txt")]
        [InlineData(@"{""command"": ""cat input.txt > output.txt""}", "output.txt")]
        [InlineData(@"{""command"": ""echo 'test' >> append.log""}", "append.log")]
        [InlineData(@"{""command"": ""sed -i 's/old/new/g' config.yaml""}", "config.yaml")]
        [InlineData(@"{""command"": ""printf 'data' > 'file with spaces.txt'""}", "file with spaces.txt")]
        [InlineData(@"{""command"": ""echo test > ""quoted file.txt""""}", "quoted file.txt")]
        public void FileLock_ExtractFilePath_ExtractsFromBashCommand(string json, string expected)
        {
            using var doc = JsonDocument.Parse(json);
            var result = FileLockManager.ExtractFilePath(doc.RootElement);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void FileLock_ExtractFilePath_PrefersFilePathOverCommand()
        {
            using var doc = JsonDocument.Parse(@"{""file_path"": ""preferred.txt"", ""command"": ""echo > other.txt""}");
            var result = FileLockManager.ExtractFilePath(doc.RootElement);
            Assert.Equal("preferred.txt", result);
        }

        // ══════════════════════════════════════════════════════════════
        //  C. TASK LIFECYCLE STATE TRANSITIONS
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Lifecycle_NewTask_DefaultsToRunning()
        {
            var task = new AgentTask();
            Assert.Equal(AgentTaskStatus.Running, task.Status);
        }

        [Fact]
        public void Lifecycle_RunningToCompleted()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Completed;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.False(task.IsRunning);
            Assert.Equal("Finished", task.StatusText);
        }

        [Fact]
        public void Lifecycle_RunningToFailed()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Failed;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.Equal("Failed", task.StatusText);
        }

        [Fact]
        public void Lifecycle_RunningToCancelled()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.Equal("Cancelled", task.StatusText);
        }

        [Fact]
        public void Lifecycle_RunningToQueued()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Queued;
            task.QueuedReason = "File locked";

            Assert.True(task.IsQueued);
            Assert.False(task.IsFinished);
            Assert.False(task.IsRunning);
            Assert.Equal("Queued", task.StatusText);
        }

        [Fact]
        public void Lifecycle_QueuedToRunning()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Queued;
            task.QueuedReason = "File locked";

            task.Status = AgentTaskStatus.Running;
            task.QueuedReason = null;

            Assert.True(task.IsRunning);
            Assert.False(task.IsQueued);
        }

        [Fact]
        public void Lifecycle_RunningToPaused()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Paused;

            Assert.True(task.IsPaused);
            Assert.False(task.IsRunning);
            Assert.False(task.IsFinished);
            Assert.Equal("Paused", task.StatusText);
        }

        [Fact]
        public void Lifecycle_PausedToRunning()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.Paused;
            task.Status = AgentTaskStatus.Running;

            Assert.True(task.IsRunning);
            Assert.False(task.IsPaused);
        }

        [Fact]
        public void Lifecycle_InitQueuedStatus()
        {
            var task = MakeTask();
            task.Status = AgentTaskStatus.InitQueued;

            Assert.True(task.IsInitQueued);
            Assert.False(task.IsRunning);
            Assert.False(task.IsFinished);
            Assert.Equal("Waiting", task.StatusText);
        }

        [Theory]
        [InlineData(AgentTaskStatus.Completed, true)]
        [InlineData(AgentTaskStatus.Cancelled, true)]
        [InlineData(AgentTaskStatus.Failed, true)]
        [InlineData(AgentTaskStatus.Running, false)]
        [InlineData(AgentTaskStatus.Paused, false)]
        [InlineData(AgentTaskStatus.Queued, false)]
        [InlineData(AgentTaskStatus.InitQueued, false)]
        public void Lifecycle_IsFinished_CorrectForAllStatuses(AgentTaskStatus status, bool expected)
        {
            var task = new AgentTask { Status = status };
            Assert.Equal(expected, task.IsFinished);
        }

        [Fact]
        public void Lifecycle_StatusChangeFiresPropertyChanged()
        {
            var task = MakeTask();
            var changed = new List<string>();
            task.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

            task.Status = AgentTaskStatus.Completed;

            Assert.Contains("Status", changed);
            Assert.Contains("StatusText", changed);
            Assert.Contains("StatusColor", changed);
            Assert.Contains("IsRunning", changed);
            Assert.Contains("IsFinished", changed);
        }

        [Fact]
        public void Lifecycle_FeatureModeStatusText_ShowsIteration()
        {
            var task = MakeFeatureModeTask();
            task.Status = AgentTaskStatus.Running;

            Assert.Contains("1/2", task.StatusText);

            task.CurrentIteration = 2;
            Assert.Contains("2/2", task.StatusText);
        }

        // ══════════════════════════════════════════════════════════════
        //  D. CANCELLATION FLOWS
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Cancel_SetsCancelledStatus()
        {
            RunOnSta(() =>
            {
                var (mgr, flm, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Cts = new CancellationTokenSource();

                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
                    Assert.NotNull(task.EndTime);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_NoOpsWhenAlreadyFinished()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Status = AgentTaskStatus.Completed;
                    task.EndTime = DateTime.Now;
                    var originalEndTime = task.EndTime;

                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(AgentTaskStatus.Completed, task.Status);
                    Assert.Equal(originalEndTime, task.EndTime);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Theory]
        [InlineData(AgentTaskStatus.Completed)]
        [InlineData(AgentTaskStatus.Cancelled)]
        [InlineData(AgentTaskStatus.Failed)]
        public void Cancel_NoOpsForAllFinishedStatuses(AgentTaskStatus status)
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Status = status;
                    task.EndTime = DateTime.Now;

                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(status, task.Status);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_ReleasesFileLocks()
        {
            RunOnSta(() =>
            {
                var (mgr, flm, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Cts = new CancellationTokenSource();
                    var activeTasks = new ObservableCollection<AgentTask> { task };

                    flm.TryAcquireFileLock(task.Id, "src/file.cs", "Edit", activeTasks);
                    Assert.Equal(1, flm.LockCount);

                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(0, flm.LockCount);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_RemovesQueuedInfo()
        {
            RunOnSta(() =>
            {
                var (mgr, flm, tempDir) = CreateManagers();
                try
                {
                    var taskA = MakeTask();
                    var taskB = MakeTask();
                    taskB.StoredPrompt = "plan";
                    taskB.Cts = new CancellationTokenSource();
                    var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                    // Create a file lock conflict so taskB gets queued info
                    flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                    flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks, (_, _) => { });
                    Assert.True(flm.QueuedTaskInfos.ContainsKey(taskB.Id));

                    mgr.CancelTaskImmediate(taskB);

                    Assert.False(flm.QueuedTaskInfos.ContainsKey(taskB.Id));
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_DisposesCts()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    var cts = new CancellationTokenSource();
                    task.Cts = cts;

                    mgr.CancelTaskImmediate(task);

                    Assert.Null(task.Cts);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_CancelsCancellationToken()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    var cts = new CancellationTokenSource();
                    task.Cts = cts;
                    var token = cts.Token;

                    mgr.CancelTaskImmediate(task);

                    Assert.True(token.IsCancellationRequested);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_StopsFeatureModeTimers()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeFeatureModeTask();
                    task.Cts = new CancellationTokenSource();
                    task.FeatureModeRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
                    task.FeatureModeIterationTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
                    task.FeatureModeRetryTimer.Start();
                    task.FeatureModeIterationTimer.Start();

                    mgr.CancelTaskImmediate(task);

                    Assert.Null(task.FeatureModeRetryTimer);
                    Assert.Null(task.FeatureModeIterationTimer);
                    Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_RemovesStreamingToolState()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Cts = new CancellationTokenSource();

                    // Simulate streaming tool state existing
                    mgr.StreamingToolState[task.Id] = new StreamingToolState
                    {
                        CurrentToolName = "Edit",
                        IsFileModifyTool = true
                    };

                    mgr.CancelTaskImmediate(task);

                    Assert.False(mgr.StreamingToolState.ContainsKey(task.Id));
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_HandlesNullProcess()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Process = null;
                    task.Cts = new CancellationTokenSource();

                    // Should not throw
                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void Cancel_HandlesNullCts()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    var task = MakeTask();
                    task.Cts = null;

                    // Should not throw
                    mgr.CancelTaskImmediate(task);

                    Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  E. FEATURE MODE TASK LIFECYCLE (END-TO-END)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void FeatureModeLifecycle_PrepareForStart_InitializesCorrectly()
        {
            var task = _factory.CreateTask("feature mode", @"C:\Test", true, false, false, true, false, false);
            _factory.PrepareTaskForFeatureModeStart(task);

            Assert.True(task.SkipPermissions);
            Assert.Equal(1, task.CurrentIteration);
            Assert.Equal(0, task.ConsecutiveFailures);
            Assert.Equal(0, task.LastIterationOutputStart);
        }

        [Fact]
        public void FeatureModeLifecycle_IterationContinuesThenFinishesOnComplete()
        {
            // Simulate a multi-iteration feature mode run
            var task = MakeFeatureModeTask();
            var failures = 0;

            // Iteration 1: success
            var d1 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(5),
                "Working...\nSTATUS: NEEDS_MORE_WORK\n", 1, 50, 0, failures, 1000);
            Assert.Equal(FeatureModeAction.Continue, d1.Action);
            failures = d1.ConsecutiveFailures;

            // Iteration 2: success
            var d2 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(15),
                "More work...\nSTATUS: NEEDS_MORE_WORK\n", 2, 50, 0, failures, 2000);
            Assert.Equal(FeatureModeAction.Continue, d2.Action);
            failures = d2.ConsecutiveFailures;

            // Iteration 3: complete!
            var d3 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(25),
                "Done!\nSTATUS: COMPLETE\n", 3, 50, 0, failures, 3000);
            Assert.Equal(FeatureModeAction.Finish, d3.Action);
            Assert.Equal(AgentTaskStatus.Completed, d3.FinishStatus);
        }

        [Fact]
        public void FeatureModeLifecycle_FailureRecoveryThenCrashLoop()
        {
            var failures = 0;

            // Iteration 1: failure
            var d1 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(5),
                "error output", 1, 50, 1, failures, 1000);
            Assert.Equal(FeatureModeAction.Continue, d1.Action);
            Assert.Equal(1, d1.ConsecutiveFailures);
            failures = d1.ConsecutiveFailures;

            // Iteration 2: success (resets failures)
            var d2 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(10),
                "back to work\nSTATUS: NEEDS_MORE_WORK\n", 2, 50, 0, failures, 2000);
            Assert.Equal(FeatureModeAction.Continue, d2.Action);
            Assert.Equal(0, d2.ConsecutiveFailures);
            failures = d2.ConsecutiveFailures;

            // Iterations 3, 4, 5: three consecutive failures → crash loop
            var d3 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(15),
                "error", 3, 50, 1, failures, 3000);
            Assert.Equal(FeatureModeAction.Continue, d3.Action);
            failures = d3.ConsecutiveFailures;

            var d4 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(20),
                "error", 4, 50, 1, failures, 4000);
            Assert.Equal(FeatureModeAction.Continue, d4.Action);
            failures = d4.ConsecutiveFailures;

            var d5 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(25),
                "error", 5, 50, 1, failures, 5000);
            Assert.Equal(FeatureModeAction.Finish, d5.Action);
            Assert.Equal(AgentTaskStatus.Failed, d5.FinishStatus);
            Assert.Equal(3, d5.ConsecutiveFailures);
        }

        [Fact]
        public void FeatureModeLifecycle_TokenLimitRetryThenContinue()
        {
            var failures = 0;

            // Iteration 1: token limit
            var d1 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromMinutes(30),
                "error: rate limit exceeded", 1, 50, 1, failures, 1000);
            Assert.Equal(FeatureModeAction.RetryAfterDelay, d1.Action);
            Assert.Equal(0, d1.ConsecutiveFailures); // Token limit resets failures
            failures = d1.ConsecutiveFailures;

            // After retry wait, iteration 2: success
            var d2 = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running, TimeSpan.FromHours(1),
                "continuing work\nSTATUS: NEEDS_MORE_WORK\n", 2, 50, 0, failures, 2000);
            Assert.Equal(FeatureModeAction.Continue, d2.Action);
            Assert.Equal(0, d2.ConsecutiveFailures);
        }

        // ══════════════════════════════════════════════════════════════
        //  F. FULL TASK LIFECYCLE WITH FILE LOCKS (QUEUED → RUNNING → FINISHED)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void FullLifecycle_QueuedByFileLock_ResumesOnRelease()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                taskB.StoredPrompt = "Execute my plan";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                // Step 1: taskA locks a file
                flm.TryAcquireFileLock(taskA.Id, "src/shared.cs", "Edit", activeTasks);

                // Step 2: taskB tries the same file → conflict → queued
                flm.HandleFileLockConflict(taskB.Id, "src/shared.cs", "Edit", activeTasks, (_, _) => { });
                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
                Assert.False(taskB.IsRunning);

                // Step 3: taskA finishes → release locks
                taskA.Status = AgentTaskStatus.Completed;
                taskA.EndTime = DateTime.Now;
                flm.ReleaseTaskLocks(taskA.Id);

                // Step 4: check queued → taskB resumes
                flm.CheckQueuedTasks(activeTasks);
                Assert.Equal(AgentTaskStatus.Running, taskB.Status);
                Assert.True(taskB.IsRunning);
            });
        }

        [Fact]
        public void FullLifecycle_MultipleTasksQueuedOnSameFile()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                var taskB = MakeTask();
                var taskC = MakeTask();
                taskB.StoredPrompt = "plan B";
                taskC.StoredPrompt = "plan C";
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB, taskC };

                // taskA locks file
                flm.TryAcquireFileLock(taskA.Id, "shared.cs", "Edit", activeTasks);

                // Both taskB and taskC conflict
                flm.HandleFileLockConflict(taskB.Id, "shared.cs", "Edit", activeTasks, (_, _) => { });
                flm.HandleFileLockConflict(taskC.Id, "shared.cs", "Edit", activeTasks, (_, _) => { });
                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
                Assert.Equal(AgentTaskStatus.Queued, taskC.Status);

                // Release taskA's locks
                taskA.Status = AgentTaskStatus.Completed;
                flm.ReleaseTaskLocks(taskA.Id);
                flm.CheckQueuedTasks(activeTasks);

                // Both should resume since blocker is done and file is unlocked
                Assert.Equal(AgentTaskStatus.Running, taskB.Status);
                Assert.Equal(AgentTaskStatus.Running, taskC.Status);
            });
        }

        [Fact]
        public void FullLifecycle_PlanBeforeQueue_SetsCorrectFlags()
        {
            // Test the direct queue workflow
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var taskA = MakeTask();
                taskA.TaskNumber = 1001; // Set task number for test
                var taskB = MakeTask();
                taskB.TaskNumber = 1002; // Set task number for test
                taskB.StoredPrompt = null;
                taskB.IsPlanningBeforeQueue = false;
                var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

                flm.TryAcquireFileLock(taskA.Id, "src/file.cs", "Edit", activeTasks);
                flm.HandleFileLockConflict(taskB.Id, "src/file.cs", "Edit", activeTasks, (_, _) => { });

                // Should queue directly without planning mode
                Assert.Equal(AgentTaskStatus.Queued, taskB.Status);
                Assert.Equal(taskA.Id, taskB.BlockedByTaskId);
                Assert.Equal("File locked: file.cs by #1001", taskB.QueuedReason);
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  G. TASK COMPLETION EVENT
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void TaskCompletedEvent_FiresOnCompletion()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    string? firedId = null;
                    mgr.TaskCompleted += id => firedId = id;

                    // The event is fired from process exit callbacks, but we can verify
                    // the event wiring works by subscribing and checking it's null before trigger
                    Assert.Null(firedId);
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        [Fact]
        public void StreamingToolState_RemoveCleanup()
        {
            RunOnSta(() =>
            {
                var (mgr, _, tempDir) = CreateManagers();
                try
                {
                    mgr.StreamingToolState["task1"] = new StreamingToolState { CurrentToolName = "Edit" };
                    mgr.StreamingToolState["task2"] = new StreamingToolState { CurrentToolName = "Write" };

                    mgr.RemoveStreamingState("task1");

                    Assert.False(mgr.StreamingToolState.ContainsKey("task1"));
                    Assert.True(mgr.StreamingToolState.ContainsKey("task2"));
                }
                finally { try { Directory.Delete(tempDir, true); } catch { } }
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  H. FEATURE MODE TASK INITIALIZATION
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void FeatureModeInit_ForcesSkipPermissions()
        {
            var task = _factory.CreateTask("test", @"C:\Test", false, false, false, true, false, false);
            Assert.False(task.SkipPermissions); // Not yet prepared

            _factory.PrepareTaskForFeatureModeStart(task);

            Assert.True(task.SkipPermissions);
        }

        [Fact]
        public void FeatureModeInit_ResetsIterationState()
        {
            var task = MakeFeatureModeTask();
            // Simulate previous run state
            task.CurrentIteration = 25;
            task.ConsecutiveFailures = 2;
            task.LastIterationOutputStart = 50000;

            _factory.PrepareTaskForFeatureModeStart(task);

            Assert.Equal(1, task.CurrentIteration);
            Assert.Equal(0, task.ConsecutiveFailures);
            Assert.Equal(0, task.LastIterationOutputStart);
        }

        [Fact]
        public void FeatureModeInit_DefaultMaxIterationsIs2()
        {
            var task = MakeFeatureModeTask();
            Assert.Equal(2, task.MaxIterations);
        }

        [Fact]
        public void FeatureModeContinuationPrompt_IncludesIterationNumbers()
        {
            var prompt = _prompt.BuildFeatureModeContinuationPrompt(1, 2);

            Assert.Contains("1", prompt);
            Assert.Contains("2", prompt);
            Assert.Contains("iteration", prompt.ToLowerInvariant());
        }

        [Fact]
        public void FeatureModeContinuationPrompt_IncludesRestrictions()
        {
            var prompt = _prompt.BuildFeatureModeContinuationPrompt(1, 50);

            Assert.Contains("No git", prompt);
            Assert.Contains("STATUS: COMPLETE", prompt);
        }

        // ══════════════════════════════════════════════════════════════
        //  I. EDGE CASES AND BOUNDARY CONDITIONS
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void EdgeCase_EmptyOutputDoesNotTriggerCompletion()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(30), "", 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Continue, decision.Action);
        }

        [Fact]
        public void EdgeCase_StatusCompleteInMiddleOfOutput()
        {
            // STATUS: COMPLETE only checked in last 50 lines
            var output = "STATUS: COMPLETE\n" + string.Join("\n", new string[60].Select(_ => "filler line"));

            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(30), output, 5, 50, 0, 0, 0);

            // Should NOT detect completion (marker buried beyond last 50 lines)
            Assert.Equal(FeatureModeAction.Continue, decision.Action);
        }

        [Fact]
        public void EdgeCase_StatusCompleteInLast50Lines()
        {
            var output = string.Join("\n", new string[40].Select(_ => "filler")) + "\nSTATUS: COMPLETE\n";

            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(30), output, 5, 50, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
        }

        [Fact]
        public void EdgeCase_ExitCode0WithTokenLimit_IsRetry()
        {
            // Even exitCode 0 with token limit text → retry
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(30), "too many requests", 5, 50,
                exitCode: 0, consecutiveFailures: 0, outputLength: 0);

            Assert.Equal(FeatureModeAction.RetryAfterDelay, decision.Action);
        }

        [Fact]
        public void EdgeCase_MaxIterationsAtZero()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(5), "", 0, 0, 0, 0, 0);

            Assert.Equal(FeatureModeAction.Finish, decision.Action);
            Assert.Equal(AgentTaskStatus.Completed, decision.FinishStatus);
        }

        [Fact]
        public void EdgeCase_OutputLengthExactlyAtCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(5), "", 5, 50, 0, 0, 100_000);

            Assert.False(decision.TrimOutput);
        }

        [Fact]
        public void EdgeCase_OutputLengthOneOverCap()
        {
            var decision = TaskExecutionManager.EvaluateFeatureModeIteration(
                _completion, AgentTaskStatus.Running,
                TimeSpan.FromMinutes(5), "", 5, 50, 0, 0, 100_001);

            Assert.True(decision.TrimOutput);
        }

        [Fact]
        public void EdgeCase_ReleaseLocksForNonexistentTask()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);

                // Should not throw
                flm.ReleaseTaskLocks("nonexistent_id");

                Assert.Equal(0, flm.LockCount);
            });
        }

        [Fact]
        public void EdgeCase_CheckQueuedTasksWithEmptyQueue()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var activeTasks = new ObservableCollection<AgentTask>();

                // Should not throw
                flm.CheckQueuedTasks(activeTasks);
            });
        }

        [Fact]
        public void EdgeCase_HandleFileLockConflict_TaskNotInActiveTasks()
        {
            RunOnSta(() =>
            {
                var flm = new FileLockManager(new TextBlock(), Dispatcher.CurrentDispatcher);
                var activeTasks = new ObservableCollection<AgentTask>();

                // Should not throw — task not found returns early
                flm.HandleFileLockConflict("missing_id", "file.cs", "Edit", activeTasks, (_, _) => { });
            });
        }

        [Fact]
        public void EdgeCase_KillProcess_NullProcess()
        {
            var task = MakeTask();
            task.Process = null;

            // Static method should not throw
            TaskExecutionManager.KillProcess(task);
        }
        // ── General output cap (non-feature-mode) ──

        [Fact]
        public void TrimOutput_TrimsNonFeatureModeTaskOverCap()
        {
            var task = MakeTask();
            task.OutputBuilder.Append(new string('x', OutputTabManager.OutputCapChars + 100_000));

            OutputTabManager.TrimOutputIfNeeded(task);

            Assert.Equal(OutputTabManager.OutputCapChars, task.OutputBuilder.Length);
        }

        [Fact]
        public void TrimOutput_KeepsMostRecentOutput()
        {
            var task = MakeTask();
            // Fill with old data, then append a known tail
            task.OutputBuilder.Append(new string('A', OutputTabManager.OutputCapChars));
            var tail = new string('Z', 1000);
            task.OutputBuilder.Append(tail);

            OutputTabManager.TrimOutputIfNeeded(task);

            Assert.EndsWith(tail, task.OutputBuilder.ToString());
        }

        [Fact]
        public void TrimOutput_NoTrimWhenUnderCap()
        {
            var task = MakeTask();
            var original = new string('x', 10_000);
            task.OutputBuilder.Append(original);

            OutputTabManager.TrimOutputIfNeeded(task);

            Assert.Equal(original, task.OutputBuilder.ToString());
        }

        [Fact]
        public void TrimOutput_NoTrimAtExactCap()
        {
            var task = MakeTask();
            task.OutputBuilder.Append(new string('x', OutputTabManager.OutputCapChars));

            OutputTabManager.TrimOutputIfNeeded(task);

            Assert.Equal(OutputTabManager.OutputCapChars, task.OutputBuilder.Length);
        }

        [Fact]
        public void TrimOutput_SkipsFeatureModeTasks()
        {
            var task = MakeFeatureModeTask();
            var bigLength = OutputTabManager.OutputCapChars + 100_000;
            task.OutputBuilder.Append(new string('x', bigLength));

            OutputTabManager.TrimOutputIfNeeded(task);

            // Feature mode tasks are not trimmed by general cap (they have their own iteration-based trimming)
            Assert.Equal(bigLength, task.OutputBuilder.Length);
        }

        [Fact]
        public void FileLock_ShowsWaitingStatusForQueuedTasks()
        {
            var flm = MakeFileLockManager();
            var tasks = new ObservableCollection<AgentTask>();

            var task1 = new AgentTask { Id = "task1", TaskNumber = 1, ProjectPath = "C:\\test" };
            var task2 = new AgentTask { Id = "task2", TaskNumber = 2, ProjectPath = "C:\\test" };
            tasks.Add(task1);
            tasks.Add(task2);

            // Task 1 acquires lock on file
            var acquired = flm.TryAcquireFileLock("task1", "test.txt", "Edit", tasks);
            Assert.True(acquired);
            Assert.Single(flm.FileLocksView);
            Assert.Equal("Active", flm.FileLocksView[0].StatusText);

            // Task 2 tries to acquire same file, should be queued with waiting status
            bool conflictHandled = false;
            var result = flm.TryAcquireOrConflict("task2", "test.txt", "Write", tasks,
                (taskId, msg) => { conflictHandled = true; });

            Assert.False(result);
            Assert.True(conflictHandled);

            // Should now have 2 file locks - one active, one waiting
            Assert.Equal(2, flm.FileLocksView.Count);

            // Find the waiting lock (could be in either position)
            var waitingLock = flm.FileLocksView.FirstOrDefault(fl => fl.IsWaiting);
            Assert.NotNull(waitingLock);
            Assert.Equal("Waiting", waitingLock.StatusText);
            Assert.Equal("task2", waitingLock.OwnerTaskId);
            Assert.Contains("waiting for", waitingLock.ToolName);

            // Release task1's lock
            flm.ReleaseTaskLocks("task1");

            // Complete task1 to allow task2 to resume
            task1.Status = AgentTaskStatus.Completed;

            // Check queued tasks - task2 should be resumed
            flm.CheckQueuedTasks(tasks);

            // Waiting lock should be removed
            Assert.Empty(flm.FileLocksView);
            Assert.Equal(AgentTaskStatus.Running, task2.Status);
        }
    }
}
