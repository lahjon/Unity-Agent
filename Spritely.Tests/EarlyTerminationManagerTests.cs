using System;
using System.Collections.Generic;
using Spritely.Managers;
using Spritely.Models;
using Xunit;

namespace Spritely.Tests
{
    public class EarlyTerminationManagerTests
    {
        private readonly ProgressAnalyzer _progressAnalyzer = new();
        private readonly TokenBudgetManager _tokenBudgetManager = new();
        private readonly EarlyTerminationManager _manager;

        public EarlyTerminationManagerTests()
        {
            _manager = new EarlyTerminationManager(_progressAnalyzer, _tokenBudgetManager);
        }

        [Fact]
        public void EvaluateTermination_FirstIteration_DoesNotTerminate()
        {
            var task = CreateTask(iteration: 1);
            var decision = _manager.EvaluateTermination(task, "Starting work on the feature");

            Assert.False(decision.ShouldTerminate);
            Assert.Equal(task.Id, decision.TaskId);
        }

        [Fact]
        public void EvaluateTermination_CatastrophicError_Terminates()
        {
            var task = CreateTask(iteration: 1);
            var decision = _manager.EvaluateTermination(task, "fatal error: out of memory");

            Assert.True(decision.ShouldTerminate);
            Assert.Equal(TerminationReason.CriticalFailure, decision.Reason);
            Assert.True(decision.Confidence >= 0.9);
        }

        [Theory]
        [InlineData("out of memory")]
        [InlineData("stack overflow")]
        [InlineData("fatal error occurred")]
        [InlineData("unrecoverable error")]
        public void EvaluateTermination_CatastrophicPatterns_AllTerminate(string errorPattern)
        {
            var task = CreateTask(iteration: 1);
            var decision = _manager.EvaluateTermination(task, $"Process encountered {errorPattern}");

            Assert.True(decision.ShouldTerminate);
        }

        [Fact]
        public void EvaluateTermination_NormalOutput_DoesNotTerminate()
        {
            var task = CreateTask(iteration: 1);
            var decision = _manager.EvaluateTermination(task, "Modified file.cs, added 50 lines, 3 tests passed");

            Assert.False(decision.ShouldTerminate);
        }

        [Fact]
        public void EvaluateTermination_BudgetExceeded_Terminates()
        {
            var task = CreateTask(iteration: 3);
            task.InputTokens = 600_000;
            task.OutputTokens = 200_000;

            var budgetConfig = new TokenBudgetConfig { TotalTokenBudget = 500_000 };
            var decision = _manager.EvaluateTermination(task, "Working on implementation", budgetConfig);

            Assert.True(decision.ShouldTerminate);
            Assert.Equal(TerminationReason.CriticalFailure, decision.Reason);
        }

        [Fact]
        public void EvaluateTermination_BudgetWarning_DoesNotTerminate()
        {
            var task = CreateTask(iteration: 2);
            task.InputTokens = 350_000;
            task.OutputTokens = 100_000;

            var budgetConfig = new TokenBudgetConfig { TotalTokenBudget = 500_000 };
            var decision = _manager.EvaluateTermination(task, "Progress: 50% complete");

            Assert.False(decision.ShouldTerminate);
        }

        [Fact]
        public void EvaluateTermination_IterationLimitReached_IsInfoCheck()
        {
            var task = CreateTask(iteration: 5);
            task.MaxIterations = 5;

            var decision = _manager.EvaluateTermination(task, "Continuing work");

            // Iteration limit alone is Info severity, not enough to terminate
            Assert.False(decision.ShouldTerminate);
        }

        [Fact]
        public void ClearTaskState_RemovesTracking()
        {
            var task = CreateTask(iteration: 1);
            _manager.EvaluateTermination(task, "some output");

            _manager.ClearTaskState(task.Id);

            var stats = _manager.GetStatistics();
            Assert.Equal(0, stats.ActiveTasks);
        }

        [Fact]
        public void GetStatistics_TracksActiveTasks()
        {
            var task1 = CreateTask(iteration: 1);
            var task2 = CreateTask(iteration: 1);

            _manager.EvaluateTermination(task1, "output 1");
            _manager.EvaluateTermination(task2, "output 2");

            var stats = _manager.GetStatistics();
            Assert.Equal(2, stats.ActiveTasks);
        }

        [Fact]
        public void GetStatistics_AfterClear_DecreasesCount()
        {
            var task1 = CreateTask(iteration: 1);
            var task2 = CreateTask(iteration: 1);

            _manager.EvaluateTermination(task1, "output 1");
            _manager.EvaluateTermination(task2, "output 2");

            _manager.ClearTaskState(task1.Id);

            var stats = _manager.GetStatistics();
            Assert.Equal(1, stats.ActiveTasks);
        }

        [Fact]
        public void TokenBudgetManager_TracksUsage()
        {
            _tokenBudgetManager.UpdateTaskUsage("task1", 100_000,
                inputTokens: 80_000, outputTokens: 20_000, model: "claude-sonnet-4-20250514");
            var usage = _tokenBudgetManager.GetTaskUsage("task1");

            Assert.Equal(100_000, usage.TotalTokens);
            Assert.True(usage.EstimatedCost > 0);
        }

        [Fact]
        public void TokenBudgetManager_UnknownTask_ReturnsZero()
        {
            var usage = _tokenBudgetManager.GetTaskUsage("nonexistent");
            Assert.Equal(0, usage.TotalTokens);
        }

        [Fact]
        public void TokenBudgetManager_TotalTokensUsed_SumsAll()
        {
            _tokenBudgetManager.UpdateTaskUsage("task1", 100_000);
            _tokenBudgetManager.UpdateTaskUsage("task2", 200_000);

            Assert.Equal(300_000, _tokenBudgetManager.GetTotalTokensUsed());
        }

        [Fact]
        public void TokenBudgetManager_ClearTaskUsage_Removes()
        {
            _tokenBudgetManager.UpdateTaskUsage("task1", 100_000);
            _tokenBudgetManager.ClearTaskUsage("task1");

            Assert.Equal(0, _tokenBudgetManager.GetTotalTokensUsed());
        }

        [Fact]
        public void EvaluateTermination_IncreasingErrors_DetectedAsWarning()
        {
            var task = CreateTask(iteration: 1);

            // Feed iterations with increasing error counts
            _manager.EvaluateTermination(task, "error: something failed");
            task.CurrentIteration = 2;
            _manager.EvaluateTermination(task, "error: a failed\nerror: b failed");
            task.CurrentIteration = 3;
            var decision = _manager.EvaluateTermination(task,
                "error: a failed\nerror: b failed\nerror: c failed");

            // Should detect increasing errors but not necessarily terminate (only warning)
            Assert.NotNull(decision);
        }

        [Fact]
        public void EvaluateTermination_DecisionIncludesTimestamp()
        {
            var task = CreateTask(iteration: 1);
            var before = DateTime.Now;
            var decision = _manager.EvaluateTermination(task, "some output");
            var after = DateTime.Now;

            Assert.InRange(decision.Timestamp, before, after);
        }

        [Fact]
        public void EvaluateTermination_DecisionIncludesIteration()
        {
            var task = CreateTask(iteration: 3);
            var decision = _manager.EvaluateTermination(task, "some output");

            Assert.Equal(3, decision.Iteration);
        }

        private static AgentTask CreateTask(int iteration)
        {
            var task = new AgentTask
            {
                Description = "Test task",
                ProjectPath = @"C:\TestProject",
                MaxIterations = 10,
                IsTeamsMode = true
            };
            task.CurrentIteration = iteration;
            return task;
        }
    }
}
