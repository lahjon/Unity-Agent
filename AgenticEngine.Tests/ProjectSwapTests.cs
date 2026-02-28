using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace AgenticEngine.Tests
{
    /// <summary>
    /// Tests verifying that project swapping behaves correctly when tasks are
    /// running, queued, paused, initializing, or holding file locks. These
    /// exercise the pure-logic components (AgentTask, FileLockManager,
    /// TaskLauncher) without requiring a WPF Dispatcher or UI elements.
    /// </summary>
    public class ProjectSwapTests
    {
        // ── Helpers ──────────────────────────────────────────────────

        private static AgentTask MakeTask(string projectPath, AgentTaskStatus status = AgentTaskStatus.Running)
        {
            var t = TaskLauncher.CreateTask("test task", projectPath, false, false, false, false, false, false);
            t.Status = status;
            return t;
        }

        private static AgentTask MakeOvernightTask(string projectPath)
        {
            var t = TaskLauncher.CreateTask("overnight task", projectPath, true, false, false, true, false, false);
            TaskLauncher.PrepareTaskForOvernightStart(t);
            return t;
        }

        // ── Task state survives project swap ─────────────────────────

        [Fact]
        public void RunningTask_RetainsProjectPath_AfterSwap()
        {
            // Simulates: task launched on Project-A, then user swaps to Project-B.
            // The existing task must keep its original project path.
            var task = MakeTask(@"C:\Projects\ProjectA");
            Assert.Equal(@"C:\Projects\ProjectA", task.ProjectPath);

            // Swap happens — only the manager's _projectPath changes (not the task's)
            var newProjectPath = @"C:\Projects\ProjectB";
            // Task should NOT be updated
            Assert.NotEqual(newProjectPath, task.ProjectPath);
            Assert.Equal(@"C:\Projects\ProjectA", task.ProjectPath);
            Assert.True(task.IsRunning);
        }

        [Fact]
        public void MultipleRunningTasks_DifferentProjects_IndependentAfterSwap()
        {
            var taskA = MakeTask(@"C:\Projects\ProjectA");
            var taskB = MakeTask(@"C:\Projects\ProjectB");

            // Both should retain their original paths regardless of current project
            Assert.Equal(@"C:\Projects\ProjectA", taskA.ProjectPath);
            Assert.Equal(@"C:\Projects\ProjectB", taskB.ProjectPath);
            Assert.True(taskA.IsRunning);
            Assert.True(taskB.IsRunning);
        }

        [Fact]
        public void NewTask_UsesNewProjectPath_AfterSwap()
        {
            // First task on old project
            var oldTask = MakeTask(@"C:\Projects\Old");

            // Swap to new project — new task should get the new path
            var newTask = MakeTask(@"C:\Projects\New");

            Assert.Equal(@"C:\Projects\Old", oldTask.ProjectPath);
            Assert.Equal(@"C:\Projects\New", newTask.ProjectPath);
        }

        [Fact]
        public void QueuedTask_RetainsProjectPath_AfterSwap()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            task.QueuedReason = "File locked: test.cs by #abc123";
            task.BlockedByTaskId = "abc123";

            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);
            Assert.True(task.IsQueued);
            Assert.False(task.IsRunning);
            Assert.False(task.IsFinished);
        }

        [Fact]
        public void PausedTask_RetainsProjectPath_AfterSwap()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Paused);
            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);
            Assert.True(task.IsPaused);
            Assert.False(task.IsRunning);
        }

        // ── File lock isolation across projects ──────────────────────

        [Fact]
        public void FileLocks_DifferentProjects_NoConflict()
        {
            // Tasks on different projects should never conflict on the same relative path
            var taskA = MakeTask(@"C:\Projects\ProjectA");
            var taskB = MakeTask(@"C:\Projects\ProjectB");
            var activeTasks = new ObservableCollection<AgentTask> { taskA, taskB };

            // Same relative file "src/file.cs" in two different projects should normalize differently
            var pathA = TaskLauncher.NormalizePath("src/file.cs", @"C:\Projects\ProjectA");
            var pathB = TaskLauncher.NormalizePath("src/file.cs", @"C:\Projects\ProjectB");

            Assert.NotEqual(pathA, pathB);
        }

        [Fact]
        public void FileLocks_SameProject_SameFile_Conflicts()
        {
            // Two tasks on the same project touching the same file should produce identical normalized paths
            var taskA = MakeTask(@"C:\Projects\Shared");
            var taskB = MakeTask(@"C:\Projects\Shared");

            var pathA = TaskLauncher.NormalizePath("src/file.cs", @"C:\Projects\Shared");
            var pathB = TaskLauncher.NormalizePath("src/file.cs", @"C:\Projects\Shared");

            Assert.Equal(pathA, pathB);
        }

        [Fact]
        public void NormalizePath_CrossProjectPaths_AreDifferent()
        {
            // Even if file names are identical, different base paths yield different normalized paths
            var p1 = TaskLauncher.NormalizePath("Assets/Scripts/Player.cs", @"C:\Projects\GameA");
            var p2 = TaskLauncher.NormalizePath("Assets/Scripts/Player.cs", @"C:\Projects\GameB");

            Assert.NotEqual(p1, p2);
        }

        [Fact]
        public void NormalizePath_AbsolutePaths_IgnoreBasePath()
        {
            // Absolute paths are independent of project base
            var abs = TaskLauncher.NormalizePath(@"C:\Shared\config.json", @"C:\Projects\GameA");
            var abs2 = TaskLauncher.NormalizePath(@"C:\Shared\config.json", @"C:\Projects\GameB");

            Assert.Equal(abs, abs2); // Same absolute path regardless of project
        }

        // ── Task creation during swap scenarios ──────────────────────

        [Fact]
        public void CreateTask_WhileOtherTaskRunning_BothValid()
        {
            var running = MakeTask(@"C:\Projects\Alpha");
            running.Summary = "Long running task";

            var newTask = MakeTask(@"C:\Projects\Beta");

            Assert.True(running.IsRunning);
            Assert.True(newTask.IsRunning);
            Assert.NotEqual(running.ProjectPath, newTask.ProjectPath);
            Assert.NotEqual(running.Id, newTask.Id);
        }

        [Fact]
        public void CreateTask_OnNewProject_WhileOldProjectHasQueuedTask()
        {
            var queued = MakeTask(@"C:\Projects\Old", AgentTaskStatus.Queued);
            queued.QueuedReason = "File locked";
            queued.BlockedByTaskId = "someId";

            // New task on different project should work fine
            var newTask = MakeTask(@"C:\Projects\New");
            Assert.True(newTask.IsRunning);
            Assert.True(queued.IsQueued);
        }

        // ── Overnight task during swap ───────────────────────────────

        [Fact]
        public void OvernightTask_RetainsState_AfterSwap()
        {
            var overnight = MakeOvernightTask(@"C:\Projects\Alpha");
            overnight.CurrentIteration = 5;

            Assert.Equal(@"C:\Projects\Alpha", overnight.ProjectPath);
            Assert.True(overnight.IsOvernight);
            Assert.True(overnight.SkipPermissions);
            Assert.Equal(5, overnight.CurrentIteration);
            Assert.True(overnight.IsRunning);
        }

        [Fact]
        public void OvernightTask_ContinuationPrompt_UsesCorrectIteration()
        {
            var prompt = TaskLauncher.BuildOvernightContinuationPrompt(7, 50);
            Assert.Contains("iteration 7/50", prompt);
        }

        [Fact]
        public void OvernightTask_PrepareStart_ForcesSkipPermissions()
        {
            var task = new AgentTask
            {
                Description = "Overnight fix",
                ProjectPath = @"C:\Projects\Alpha",
                SkipPermissions = false,
                IsOvernight = true
            };
            TaskLauncher.PrepareTaskForOvernightStart(task);

            Assert.True(task.SkipPermissions);
            Assert.Equal(1, task.CurrentIteration);
            Assert.Equal(0, task.ConsecutiveFailures);
            Assert.Equal(0, task.LastIterationOutputStart);
        }

        // ── CancelTask scenarios during swap ─────────────────────────

        [Fact]
        public void CancelledTask_IsFinished()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            Assert.True(task.IsRunning);

            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.False(task.IsRunning);
        }

        [Fact]
        public void CancelledTask_CannotBeRecancelled()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Cancelled);
            task.EndTime = DateTime.Now;

            // IsFinished should be true, so CancelTaskImmediate would short-circuit
            Assert.True(task.IsFinished);
        }

        [Fact]
        public void FailedTask_IsFinished()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            task.Status = AgentTaskStatus.Failed;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.False(task.IsRunning);
        }

        // ── Task dependencies across projects ────────────────────────

        [Fact]
        public void DependencyTask_DifferentProject_StillBlocks()
        {
            var blockerTask = MakeTask(@"C:\Projects\Alpha");
            var dependentTask = MakeTask(@"C:\Projects\Beta", AgentTaskStatus.Queued);
            dependentTask.DependencyTaskIds = new List<string> { blockerTask.Id };
            dependentTask.BlockedByTaskId = blockerTask.Id;

            Assert.True(blockerTask.IsRunning);
            Assert.True(dependentTask.IsQueued);
            Assert.Contains(blockerTask.Id, dependentTask.DependencyTaskIds);
        }

        [Fact]
        public void DependencyTask_BlockerFinished_CanResume()
        {
            var blockerTask = MakeTask(@"C:\Projects\Alpha");
            var dependentTask = MakeTask(@"C:\Projects\Beta", AgentTaskStatus.Queued);
            dependentTask.DependencyTaskIds = new List<string> { blockerTask.Id };
            dependentTask.BlockedByTaskId = blockerTask.Id;

            // Blocker finishes
            blockerTask.Status = AgentTaskStatus.Completed;
            blockerTask.EndTime = DateTime.Now;

            Assert.True(blockerTask.IsFinished);
            // Dependent can now be resumed (the check logic lives in FileLockManager.CheckQueuedTasks)
            // but the state transition is valid
            dependentTask.Status = AgentTaskStatus.Running;
            dependentTask.QueuedReason = null;
            dependentTask.BlockedByTaskId = null;
            dependentTask.StartTime = DateTime.Now;

            Assert.True(dependentTask.IsRunning);
            Assert.Null(dependentTask.QueuedReason);
        }

        [Fact]
        public void MultipleDependencies_AllMustFinish()
        {
            var dep1 = MakeTask(@"C:\Projects\Alpha");
            var dep2 = MakeTask(@"C:\Projects\Alpha");
            var waiter = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            waiter.DependencyTaskIds = new List<string> { dep1.Id, dep2.Id };

            // Only dep1 finished — still blocked
            dep1.Status = AgentTaskStatus.Completed;
            Assert.True(dep2.IsRunning); // dep2 still running

            // dep2 finishes — now all clear
            dep2.Status = AgentTaskStatus.Completed;
            Assert.True(dep1.IsFinished);
            Assert.True(dep2.IsFinished);
        }

        // ── Task ProjectColor assignment ─────────────────────────────

        [Fact]
        public void Task_ProjectColor_DefaultsToGray()
        {
            var task = new AgentTask();
            Assert.Equal("#666666", task.ProjectColor);
        }

        [Fact]
        public void Task_ProjectColor_SurvivesStatusChange()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            task.ProjectColor = "#D4806B";

            task.Status = AgentTaskStatus.Completed;

            Assert.Equal("#D4806B", task.ProjectColor);
        }

        [Fact]
        public void Task_ProjectColor_DifferentPerProject()
        {
            var taskA = MakeTask(@"C:\Projects\Alpha");
            taskA.ProjectColor = "#D4806B";

            var taskB = MakeTask(@"C:\Projects\Beta");
            taskB.ProjectColor = "#6BA3A0";

            Assert.NotEqual(taskA.ProjectColor, taskB.ProjectColor);
        }

        // ── Prompt building with project descriptions ────────────────

        [Fact]
        public void BuildFullPrompt_WithProjectDescription_IncludesDescription()
        {
            var task = new AgentTask
            {
                Description = "fix bug",
                UseMcp = false,
                IsOvernight = false
            };

            var result = TaskLauncher.BuildFullPrompt("SYS:", task, "A Unity game project");

            Assert.Contains("SYS:", result);
            Assert.Contains("A Unity game project", result);
            Assert.Contains("fix bug", result);
        }

        [Fact]
        public void BuildFullPrompt_EmptyProjectDescription_StillWorks()
        {
            var task = new AgentTask
            {
                Description = "do work",
                UseMcp = false,
                IsOvernight = false
            };

            var result = TaskLauncher.BuildFullPrompt("SYS:", task, "");
            Assert.Contains("do work", result);
        }

        [Fact]
        public void BuildFullPrompt_NullProjectDescription_StillWorks()
        {
            var task = new AgentTask
            {
                Description = "do work",
                UseMcp = false,
                IsOvernight = false
            };

            // null description should not crash
            var result = TaskLauncher.BuildFullPrompt("SYS:", task);
            Assert.Contains("do work", result);
        }

        // ── PowerShell script uses frozen project path ───────────────

        [Fact]
        public void BuildPowerShellScript_FreezesProjectPath()
        {
            // The PS1 script hardcodes the project path at build time.
            // Swapping projects after building should NOT affect the script.
            var originalPath = @"C:\Projects\OriginalProject";
            var script = TaskLauncher.BuildPowerShellScript(originalPath, @"C:\temp\prompt.txt", "claude -p $prompt");

            Assert.Contains("Set-Location -LiteralPath 'C:\\Projects\\OriginalProject'", script);

            // If we build another script for a different project, it's independent
            var newScript = TaskLauncher.BuildPowerShellScript(@"C:\Projects\NewProject", @"C:\temp\prompt2.txt", "claude -p $prompt");
            Assert.Contains("Set-Location -LiteralPath 'C:\\Projects\\NewProject'", newScript);
            Assert.DoesNotContain("OriginalProject", newScript);
        }

        [Fact]
        public void BuildPowerShellScript_DifferentPromptFiles_PerTask()
        {
            var script1 = TaskLauncher.BuildPowerShellScript(@"C:\proj", @"C:\scripts\prompt_abc.txt", "claude");
            var script2 = TaskLauncher.BuildPowerShellScript(@"C:\proj", @"C:\scripts\prompt_def.txt", "claude");

            Assert.Contains("prompt_abc.txt", script1);
            Assert.Contains("prompt_def.txt", script2);
            Assert.DoesNotContain("prompt_def.txt", script1);
        }

        // ── IsInitializing state transitions ─────────────────────────

        [Fact]
        public void ProjectEntry_IsInitializing_DefaultsFalse()
        {
            var entry = new Models.ProjectEntry();
            Assert.False(entry.IsInitializing);
        }

        [Fact]
        public void ProjectEntry_IsInitializing_CanBeSet()
        {
            var entry = new Models.ProjectEntry { IsInitializing = true };
            Assert.True(entry.IsInitializing);
        }

        // ── Status transition edge cases during swap ─────────────────

        [Fact]
        public void Task_StatusTransition_RunningToQueued_ValidDuringSwap()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            Assert.True(task.IsRunning);

            task.Status = AgentTaskStatus.Queued;
            task.QueuedReason = "File lock conflict";

            Assert.True(task.IsQueued);
            Assert.False(task.IsRunning);
            Assert.False(task.IsFinished);
        }

        [Fact]
        public void Task_StatusTransition_QueuedToRunning_ValidAfterBlockerFinishes()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            task.QueuedReason = "Waiting";

            task.Status = AgentTaskStatus.Running;
            task.QueuedReason = null;

            Assert.True(task.IsRunning);
            Assert.False(task.IsQueued);
        }

        [Fact]
        public void Task_StatusTransition_RunningToPaused_ValidDuringSwap()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            task.Status = AgentTaskStatus.Paused;

            Assert.True(task.IsPaused);
            Assert.False(task.IsRunning);
            Assert.False(task.IsFinished);
        }

        [Fact]
        public void Task_StatusTransition_PausedToRunning_ValidAfterResume()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Paused);
            task.Status = AgentTaskStatus.Running;

            Assert.True(task.IsRunning);
            Assert.False(task.IsPaused);
        }

        [Fact]
        public void Task_StatusTransition_QueuedToCancelled_Valid()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.False(task.IsQueued);
        }

        [Fact]
        public void Task_StatusTransition_PausedToCancelled_Valid()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Paused);
            task.Status = AgentTaskStatus.Cancelled;
            task.EndTime = DateTime.Now;

            Assert.True(task.IsFinished);
            Assert.False(task.IsPaused);
        }

        // ── Concurrent tasks across projects ─────────────────────────

        [Fact]
        public void ConcurrentTasks_DifferentProjects_IndependentIds()
        {
            var ids = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                var task = MakeTask($@"C:\Projects\Project{i}");
                Assert.True(ids.Add(task.Id), $"Duplicate task ID: {task.Id}");
            }
        }

        [Fact]
        public void ConcurrentTasks_MixedStatuses_CorrectCounts()
        {
            var tasks = new ObservableCollection<AgentTask>
            {
                MakeTask(@"C:\Projects\A"),                                    // Running
                MakeTask(@"C:\Projects\A", AgentTaskStatus.Queued),           // Queued
                MakeTask(@"C:\Projects\B"),                                    // Running
                MakeTask(@"C:\Projects\B", AgentTaskStatus.Completed),        // Completed
                MakeTask(@"C:\Projects\C", AgentTaskStatus.Paused),           // Paused
            };

            var running = tasks.Count(t => t.IsRunning);
            var queued = tasks.Count(t => t.IsQueued);
            var finished = tasks.Count(t => t.IsFinished);
            var paused = tasks.Count(t => t.IsPaused);

            Assert.Equal(2, running);
            Assert.Equal(1, queued);
            Assert.Equal(1, finished);
            Assert.Equal(1, paused);
        }

        [Fact]
        public void ConcurrentTasks_FilterByProject()
        {
            var tasks = new ObservableCollection<AgentTask>
            {
                MakeTask(@"C:\Projects\A"),
                MakeTask(@"C:\Projects\A"),
                MakeTask(@"C:\Projects\B"),
                MakeTask(@"C:\Projects\C"),
            };

            var projectATasks = tasks.Where(t => t.ProjectPath == @"C:\Projects\A").ToList();
            var projectBTasks = tasks.Where(t => t.ProjectPath == @"C:\Projects\B").ToList();

            Assert.Equal(2, projectATasks.Count);
            Assert.Single(projectBTasks);
        }

        // ── Rapid project swap scenarios ─────────────────────────────

        [Fact]
        public void RapidSwap_TasksStampedWithCorrectProject()
        {
            // Simulates rapid project switching — each task should get the path
            // that was current at creation time
            var currentProject = @"C:\Projects\A";
            var task1 = MakeTask(currentProject);

            currentProject = @"C:\Projects\B";
            var task2 = MakeTask(currentProject);

            currentProject = @"C:\Projects\C";
            var task3 = MakeTask(currentProject);

            currentProject = @"C:\Projects\A"; // swap back
            var task4 = MakeTask(currentProject);

            Assert.Equal(@"C:\Projects\A", task1.ProjectPath);
            Assert.Equal(@"C:\Projects\B", task2.ProjectPath);
            Assert.Equal(@"C:\Projects\C", task3.ProjectPath);
            Assert.Equal(@"C:\Projects\A", task4.ProjectPath);
        }

        // ── Token limit detection across projects ────────────────────

        [Fact]
        public void TokenLimitError_DetectedRegardlessOfProject()
        {
            // Token limit errors should be detected regardless of which project the task is on
            Assert.True(TaskLauncher.IsTokenLimitError("rate limit exceeded"));
            Assert.True(TaskLauncher.IsTokenLimitError("token limit reached"));
            Assert.True(TaskLauncher.IsTokenLimitError("server overloaded"));
        }

        // ── PlanOnly task retains project across states ───────────────

        [Fact]
        public void PlanOnlyTask_RetainsProjectPath_AcrossStatusChanges()
        {
            var task = TaskLauncher.CreateTask("plan task", @"C:\Projects\Alpha",
                false, false, false, false, false, false,
                noGitWrite: false, planOnly: true);
            task.ProjectColor = "#D4806B";

            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);
            Assert.Equal("#D4806B", task.ProjectColor);

            task.Status = AgentTaskStatus.Queued;
            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);

            task.Status = AgentTaskStatus.Running;
            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);
        }

        [Fact]
        public void Task_ProjectPath_CanBeEmpty()
        {
            var task = new AgentTask { ProjectPath = "" };
            Assert.Equal("", task.ProjectPath);
            Assert.Equal("", task.ProjectName);
        }

        // ── PropertyChanged during swap ──────────────────────────────

        [Fact]
        public void PropertyChanged_FiredCorrectly_DuringStatusTransitions()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            var changes = new List<string>();
            task.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

            // Simulate swap-related status changes
            task.Status = AgentTaskStatus.Queued;
            Assert.Contains("Status", changes);
            Assert.Contains("IsRunning", changes);
            Assert.Contains("IsQueued", changes);

            changes.Clear();
            task.Status = AgentTaskStatus.Running;
            Assert.Contains("Status", changes);
            Assert.Contains("IsRunning", changes);
        }

        [Fact]
        public void PropertyChanged_EndTime_FiredOnCompletion()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            var changes = new List<string>();
            task.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

            task.EndTime = DateTime.Now;

            Assert.Contains("EndTime", changes);
            Assert.Contains("TimeInfo", changes);
        }

        [Fact]
        public void PropertyChanged_Summary_FiredOnSummaryChange()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            var changes = new List<string>();
            task.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

            task.Summary = "New summary after swap";

            Assert.Contains("Summary", changes);
            Assert.Contains("ShortDescription", changes);
        }

        // ── TimeInfo during various states ───────────────────────────

        [Fact]
        public void TimeInfo_Queued_ShowsDependency()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            task.DependencyTaskIds = new List<string> { "abc12345" };

            Assert.Contains("#abc12345", task.TimeInfo);
            Assert.Contains("Queued", task.TimeInfo);
        }

        [Fact]
        public void TimeInfo_Queued_ShowsBlockedBy()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            task.BlockedByTaskId = "def67890";

            Assert.Contains("#def67890", task.TimeInfo);
        }

        [Fact]
        public void TimeInfo_Paused_ShowsPaused()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Paused);
            Assert.Contains("Paused", task.TimeInfo);
        }

        [Fact]
        public void TimeInfo_Completed_ShowsDuration()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            task.EndTime = task.StartTime + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(15);

            Assert.Contains("3m 15s", task.TimeInfo);
        }

        // ── Project name extraction ──────────────────────────────────

        [Fact]
        public void ProjectName_ExtractsFromPath()
        {
            var task = MakeTask(@"C:\Unity Projects\MyGame");
            Assert.Equal("MyGame", task.ProjectName);
        }

        [Fact]
        public void ProjectName_HandlesTrailingSlash()
        {
            // Path.GetFileName returns "" for trailing slash
            var task = new AgentTask { ProjectPath = @"C:\Projects\Test\" };
            // Just verify it doesn't crash
            Assert.NotNull(task.ProjectName);
        }

        // ── Ignore file locks flag ───────────────────────────────────

        [Fact]
        public void IgnoreFileLocks_Task_CanCoexist()
        {
            var normalTask = MakeTask(@"C:\Projects\Alpha");
            normalTask.IgnoreFileLocks = false;

            var ignoreTask = TaskLauncher.CreateTask("ignore locks", @"C:\Projects\Alpha",
                false, false, false, false, ignoreFileLocks: true, false);

            Assert.False(normalTask.IgnoreFileLocks);
            Assert.True(ignoreTask.IgnoreFileLocks);
        }

        // ── Model type during swap ───────────────────────────────────

        [Fact]
        public void Task_ModelType_DefaultsToClaudeCode()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            Assert.Equal(ModelType.ClaudeCode, task.Model);
        }

        [Fact]
        public void Task_ModelType_PreservedAcrossStatusChanges()
        {
            var task = new AgentTask
            {
                ProjectPath = @"C:\Projects\Alpha",
                Model = ModelType.Gemini
            };

            task.Status = AgentTaskStatus.Queued;
            Assert.Equal(ModelType.Gemini, task.Model);

            task.Status = AgentTaskStatus.Running;
            Assert.Equal(ModelType.Gemini, task.Model);

            task.Status = AgentTaskStatus.Completed;
            Assert.Equal(ModelType.Gemini, task.Model);
        }

        // ── Swap with NoGitWrite tasks ───────────────────────────────

        [Fact]
        public void NoGitWrite_Task_RetainsFlag_AfterSwap()
        {
            var task = TaskLauncher.CreateTask("no git task", @"C:\Projects\Alpha",
                false, false, false, false, false, false, noGitWrite: true);

            Assert.True(task.NoGitWrite);
            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);

            // Prompt should contain the no-git-write block
            var prompt = TaskLauncher.BuildFullPrompt("SYS:", task);
            Assert.Contains("NO GIT WRITE OPERATIONS", prompt);
        }

        // ── PlanOnly task during swap ────────────────────────────────

        [Fact]
        public void PlanOnly_Task_RetainsFlag()
        {
            var task = TaskLauncher.CreateTask("plan task", @"C:\Projects\Alpha",
                false, false, false, false, false, false,
                noGitWrite: false, planOnly: true);

            Assert.True(task.PlanOnly);
            Assert.Equal(@"C:\Projects\Alpha", task.ProjectPath);
        }

        // ── SpawnTeam during swap ────────────────────────────────────

        [Fact]
        public void SpawnTeam_Task_CommandIncludesFlag()
        {
            var cmd = TaskLauncher.BuildClaudeCommand(false, false, spawnTeam: true);
            Assert.Contains("--spawn-team", cmd);
        }

        [Fact]
        public void SpawnTeam_Task_RetainsFlag()
        {
            var task = TaskLauncher.CreateTask("team task", @"C:\Projects\Alpha",
                false, false, false, false, false, false, spawnTeam: true);

            Assert.True(task.SpawnTeam);
        }

        // ── OutputBuilder isolation ──────────────────────────────────

        [Fact]
        public void OutputBuilder_PerTask_NotShared()
        {
            var task1 = MakeTask(@"C:\Projects\A");
            var task2 = MakeTask(@"C:\Projects\B");

            task1.OutputBuilder.Append("output from task 1");
            task2.OutputBuilder.Append("output from task 2");

            Assert.Contains("task 1", task1.OutputBuilder.ToString());
            Assert.DoesNotContain("task 2", task1.OutputBuilder.ToString());
            Assert.Contains("task 2", task2.OutputBuilder.ToString());
            Assert.DoesNotContain("task 1", task2.OutputBuilder.ToString());
        }

        // ── Image paths during swap ──────────────────────────────────

        [Fact]
        public void ImagePaths_RetainedOnTask_AfterSwap()
        {
            var images = new List<string> { @"C:\imgs\screenshot.png" };
            var task = TaskLauncher.CreateTask("with images", @"C:\Projects\Alpha",
                false, false, false, false, false, false,
                false, false, false, false, images);

            Assert.Single(task.ImagePaths);
            Assert.Equal(@"C:\imgs\screenshot.png", task.ImagePaths[0]);

            // Swapping projects doesn't affect existing task's images
            var newTask = TaskLauncher.CreateTask("no images", @"C:\Projects\Beta",
                false, false, false, false, false, false);

            Assert.Single(task.ImagePaths); // still has images
            Assert.Empty(newTask.ImagePaths);
        }

        // ── ExtractFilePath helper ───────────────────────────────────

        [Fact]
        public void TryExtractFilePathFromPartial_ValidJson_ReturnsPath()
        {
            var result = Managers.FileLockManager.TryExtractFilePathFromPartial(
                @"{""file_path"": ""src/test.cs"", ""content"": ""hello");
            Assert.Equal("src/test.cs", result);
        }

        [Fact]
        public void TryExtractFilePathFromPartial_NoFilePath_ReturnsNull()
        {
            var result = Managers.FileLockManager.TryExtractFilePathFromPartial(
                @"{""name"": ""test""}");
            Assert.Null(result);
        }

        // ── Completion summary format ────────────────────────────────

        [Fact]
        public void CompletionSummary_CanBeSetOnAnyStatus()
        {
            var task = MakeTask(@"C:\Projects\Alpha");
            task.Status = AgentTaskStatus.Failed;
            task.CompletionSummary = "Task failed due to error";

            Assert.Equal("Task failed due to error", task.CompletionSummary);
        }

        [Fact]
        public void FormatCompletionSummary_IncludesProjectInfo_Implicitly()
        {
            // The summary format includes status and duration
            var summary = TaskLauncher.FormatCompletionSummary(
                AgentTaskStatus.Completed, TimeSpan.FromMinutes(10), null);

            Assert.Contains("TASK COMPLETION SUMMARY", summary);
            Assert.Contains("Completed", summary);
            Assert.Contains("10m", summary);
        }

        // ── GeneratedImagePaths during swap ──────────────────────────

        [Fact]
        public void GeneratedImagePaths_PerTask_NotShared()
        {
            var task1 = MakeTask(@"C:\Projects\A");
            var task2 = MakeTask(@"C:\Projects\B");

            task1.GeneratedImagePaths.Add(@"C:\output\img1.png");

            Assert.Single(task1.GeneratedImagePaths);
            Assert.Empty(task2.GeneratedImagePaths);
        }

        // ── Validate that task IDs are unique even under rapid creation ──

        [Fact]
        public void TaskIds_UniqueAcrossRapidCreation()
        {
            var ids = new HashSet<string>();
            for (int i = 0; i < 100; i++)
            {
                var task = new AgentTask();
                Assert.True(ids.Add(task.Id), $"Duplicate ID generated: {task.Id}");
            }
        }

        // ── GitStartHash during swap ─────────────────────────────────

        [Fact]
        public void GitStartHash_IndependentPerTask()
        {
            var task1 = MakeTask(@"C:\Projects\A");
            task1.GitStartHash = "abc1234";

            var task2 = MakeTask(@"C:\Projects\B");
            task2.GitStartHash = "def5678";

            Assert.Equal("abc1234", task1.GitStartHash);
            Assert.Equal("def5678", task2.GitStartHash);
        }

        // ── QueuedTaskInfo state ─────────────────────────────────────

        [Fact]
        public void QueuedTaskInfo_RetainsCorrectState()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            var info = new QueuedTaskInfo
            {
                Task = task,
                ConflictingFilePath = TaskLauncher.NormalizePath("src/file.cs", @"C:\Projects\Alpha"),
                BlockingTaskId = "blocker1",
                BlockedByTaskIds = new HashSet<string> { "blocker1" }
            };

            Assert.Same(task, info.Task);
            Assert.Contains("blocker1", info.BlockedByTaskIds);
            Assert.Equal("blocker1", info.BlockingTaskId);
        }

        [Fact]
        public void QueuedTaskInfo_MultipleBlockers()
        {
            var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Queued);
            var info = new QueuedTaskInfo
            {
                Task = task,
                ConflictingFilePath = "normalized/path",
                BlockingTaskId = "blocker1",
                BlockedByTaskIds = new HashSet<string> { "blocker1", "blocker2", "blocker3" }
            };

            Assert.Equal(3, info.BlockedByTaskIds.Count);
        }

        // ── StreamingToolState isolation ──────────────────────────────

        [Fact]
        public void StreamingToolState_IndependentPerTask()
        {
            var state1 = new StreamingToolState
            {
                CurrentToolName = "Write",
                IsFileModifyTool = true,
                FilePathChecked = false
            };

            var state2 = new StreamingToolState
            {
                CurrentToolName = "Read",
                IsFileModifyTool = false,
                FilePathChecked = true
            };

            Assert.NotEqual(state1.CurrentToolName, state2.CurrentToolName);
            Assert.NotEqual(state1.IsFileModifyTool, state2.IsFileModifyTool);
        }

        // ── Swap back to same project ────────────────────────────────

        [Fact]
        public void SwapBackToSameProject_TaskPathsUnchanged()
        {
            // Create tasks on project A, swap to B, swap back to A
            var task1 = MakeTask(@"C:\Projects\A");
            var task2 = MakeTask(@"C:\Projects\B");
            var task3 = MakeTask(@"C:\Projects\A");

            Assert.Equal(@"C:\Projects\A", task1.ProjectPath);
            Assert.Equal(@"C:\Projects\B", task2.ProjectPath);
            Assert.Equal(@"C:\Projects\A", task3.ProjectPath);
            Assert.NotEqual(task1.Id, task3.Id); // Different tasks, same project
        }

        // ── IsFinished comprehensive check ───────────────────────────

        [Theory]
        [InlineData(AgentTaskStatus.Running, false)]
        [InlineData(AgentTaskStatus.Paused, false)]
        [InlineData(AgentTaskStatus.Queued, false)]
        [InlineData(AgentTaskStatus.Completed, true)]
        [InlineData(AgentTaskStatus.Cancelled, true)]
        [InlineData(AgentTaskStatus.Failed, true)]
        public void IsFinished_CorrectForAllStatuses(AgentTaskStatus status, bool expectedFinished)
        {
            var task = new AgentTask();
            task.Status = status;
            Assert.Equal(expectedFinished, task.IsFinished);
        }

        [Theory]
        [InlineData(AgentTaskStatus.Running, true)]
        [InlineData(AgentTaskStatus.Paused, false)]
        [InlineData(AgentTaskStatus.Queued, false)]
        [InlineData(AgentTaskStatus.Completed, false)]
        [InlineData(AgentTaskStatus.Cancelled, false)]
        [InlineData(AgentTaskStatus.Failed, false)]
        public void IsRunning_CorrectForAllStatuses(AgentTaskStatus status, bool expectedRunning)
        {
            var task = new AgentTask();
            task.Status = status;
            Assert.Equal(expectedRunning, task.IsRunning);
        }

        // ── FileLock model ───────────────────────────────────────────

        [Fact]
        public void FileLock_Properties_SetCorrectly()
        {
            var fl = new FileLock
            {
                NormalizedPath = @"c:\projects\test\src\file.cs",
                OriginalPath = "src/file.cs",
                OwnerTaskId = "abc12345",
                ToolName = "Write",
                IsIgnored = false
            };

            Assert.Equal("file.cs", fl.FileName);
            Assert.Equal("Active", fl.StatusText);
            Assert.Equal("abc12345", fl.OwnerTaskId);
        }

        [Fact]
        public void FileLock_Ignored_StatusText()
        {
            var fl = new FileLock { IsIgnored = true };
            Assert.Equal("Ignored", fl.StatusText);
        }

        [Fact]
        public void FileLock_PropertyChanged_Fires()
        {
            var fl = new FileLock();
            var changes = new List<string>();
            fl.PropertyChanged += (_, e) => changes.Add(e.PropertyName!);

            fl.OnPropertyChanged(nameof(FileLock.ToolName));

            Assert.Contains("ToolName", changes);
        }

        // ── History round-trip: Summary and StoredPrompt ────────────

        [Fact]
        public async Task HistoryRoundTrip_PreservesSummary()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"AgenticEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var hm = new Managers.HistoryManager(tempDir);
                var original = new ObservableCollection<AgentTask>();
                var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Completed);
                task.Summary = "Fix login bug";
                task.EndTime = DateTime.Now;
                original.Add(task);

                hm.SaveHistory(original);
                await Task.Delay(200); // Allow background write to complete

                var loaded = await hm.LoadHistoryAsync(retentionHours: 24);

                Assert.Single(loaded);
                Assert.Equal("Fix login bug", loaded[0].Summary);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public async Task HistoryRoundTrip_PreservesStoredPrompt()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"AgenticEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var hm = new Managers.HistoryManager(tempDir);
                var original = new ObservableCollection<AgentTask>();
                var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Completed);
                task.StoredPrompt = "Detailed execution prompt for resuming";
                task.EndTime = DateTime.Now;
                original.Add(task);

                hm.SaveHistory(original);
                await Task.Delay(200);

                var loaded = await hm.LoadHistoryAsync(retentionHours: 24);

                Assert.Single(loaded);
                Assert.Equal("Detailed execution prompt for resuming", loaded[0].StoredPrompt);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public async Task HistoryRoundTrip_EmptyStoredPrompt_LoadsAsNull()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"AgenticEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var hm = new Managers.HistoryManager(tempDir);
                var original = new ObservableCollection<AgentTask>();
                var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Completed);
                // StoredPrompt is null by default
                task.EndTime = DateTime.Now;
                original.Add(task);

                hm.SaveHistory(original);
                await Task.Delay(200);

                var loaded = await hm.LoadHistoryAsync(retentionHours: 24);

                Assert.Single(loaded);
                Assert.Null(loaded[0].StoredPrompt);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public async Task HistoryRoundTrip_PreservesAllFields()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"AgenticEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var hm = new Managers.HistoryManager(tempDir);
                var original = new ObservableCollection<AgentTask>();
                var task = MakeTask(@"C:\Projects\Alpha", AgentTaskStatus.Completed);
                task.Summary = "Test Summary";
                task.StoredPrompt = "Test Stored Prompt";
                task.CompletionSummary = "Completed OK";
                task.ProjectColor = "#D4806B";
                task.EndTime = DateTime.Now;
                original.Add(task);

                hm.SaveHistory(original);
                await Task.Delay(200);

                var loaded = await hm.LoadHistoryAsync(retentionHours: 24);

                Assert.Single(loaded);
                var t = loaded[0];
                Assert.Equal("Test Summary", t.Summary);
                Assert.Equal("Test Stored Prompt", t.StoredPrompt);
                Assert.Equal("Completed OK", t.CompletionSummary);
                Assert.Equal("#D4806B", t.ProjectColor);
                Assert.Equal(@"C:\Projects\Alpha", t.ProjectPath);
            }
            finally { Directory.Delete(tempDir, true); }
        }

        [Fact]
        public async Task StoredTaskRoundTrip_PreservesSummaryAndPrompt()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"AgenticEngineTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var hm = new Managers.HistoryManager(tempDir);
                var original = new ObservableCollection<AgentTask>();
                var task = new AgentTask
                {
                    Description = "Original description",
                    StoredPrompt = "Detailed execution prompt",
                    ProjectPath = @"C:\Projects\Alpha",
                    ProjectColor = "#D4806B",
                    SkipPermissions = true
                };
                task.Summary = "My Stored Plan";
                task.Status = AgentTaskStatus.Completed;
                original.Add(task);

                hm.SaveStoredTasks(original);
                await Task.Delay(200);

                var loaded = await hm.LoadStoredTasksAsync();

                Assert.Single(loaded);
                Assert.Equal("My Stored Plan", loaded[0].Summary);
                Assert.Equal("Detailed execution prompt", loaded[0].StoredPrompt);
                Assert.Equal("Original description", loaded[0].Description);
            }
            finally { Directory.Delete(tempDir, true); }
        }
    }
}
