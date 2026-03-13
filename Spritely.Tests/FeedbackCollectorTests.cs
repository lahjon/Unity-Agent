using System;
using System.Collections.Generic;
using System.IO;
using Spritely.Managers;
using Spritely.Models;
using Xunit;

namespace Spritely.Tests
{
    public class FeedbackCollectorTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly FeedbackStore _store;
        private readonly FeedbackCollector _collector;

        public FeedbackCollectorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "spritely_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
            _store = new FeedbackStore(_tempDir);
            // FeedbackAnalyzer and FeedbackApplicator are not needed for basic collection tests
            // since the analysis threshold (10 entries) won't be reached in single-entry tests
            _collector = new FeedbackCollector(_store, null!, null!);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void CollectAndAnalyze_CompletedTask_SavesEntry()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Single(entries);
            Assert.Equal(task.Id, entries[0].TaskId);
            Assert.Equal("Completed", entries[0].Status);
        }

        [Fact]
        public void CollectAndAnalyze_FailedTask_SavesEntry()
        {
            var task = CreateTask(AgentTaskStatus.Failed);
            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Single(entries);
            Assert.Equal("Failed", entries[0].Status);
        }

        [Fact]
        public void CollectAndAnalyze_CapturesTokenMetrics()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            task.InputTokens = 1000;
            task.OutputTokens = 500;
            task.CacheReadTokens = 200;
            task.CacheCreationTokens = 100;

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Equal(1000, entries[0].InputTokens);
            Assert.Equal(500, entries[0].OutputTokens);
            Assert.Equal(200, entries[0].CacheReadTokens);
            Assert.Equal(100, entries[0].CacheCreationTokens);
        }

        [Fact]
        public void CollectAndAnalyze_CapturesDuration()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            task.StartTime = DateTime.Now.AddMinutes(-10);
            task.EndTime = DateTime.Now;

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.True(entries[0].DurationMinutes >= 9.5 && entries[0].DurationMinutes <= 10.5);
        }

        [Fact]
        public void CollectAndAnalyze_FastCompletion_AddsSuccessFactor()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            task.StartTime = DateTime.Now.AddMinutes(-2);
            task.EndTime = DateTime.Now;
            task.ChangedFiles = new List<string> { "file1.cs", "file2.cs" };

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Contains("fast_completion", entries[0].SuccessFactors);
            Assert.Contains("focused_changes", entries[0].SuccessFactors);
        }

        [Fact]
        public void CollectAndAnalyze_FailedWithBuildError_AddsFailureFactor()
        {
            var task = CreateTask(AgentTaskStatus.Failed);
            task.FullOutput = "The build failed with compilation error in MyClass.cs";

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Contains("build_failure", entries[0].FailureFactors);
        }

        [Fact]
        public void CollectAndAnalyze_LongRunningFailure_AddsFailureFactor()
        {
            var task = CreateTask(AgentTaskStatus.Failed);
            task.StartTime = DateTime.Now.AddMinutes(-90);
            task.EndTime = DateTime.Now;

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Contains("long_running", entries[0].FailureFactors);
        }

        [Fact]
        public void CollectAndAnalyze_NoChanges_AddsFailureFactor()
        {
            var task = CreateTask(AgentTaskStatus.Failed);

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.Contains("no_changes_made", entries[0].FailureFactors);
        }

        [Fact]
        public void CollectAndAnalyze_VerificationPassed_SetsFlag()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            task.IsVerified = true;
            task.VerificationResult = "PASS: all checks passed";

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.True(entries[0].VerificationPassed);
            Assert.Contains("verification_passed", entries[0].SuccessFactors);
        }

        [Fact]
        public void CollectAndAnalyze_CapturesTaskConfiguration()
        {
            var task = CreateTask(AgentTaskStatus.Completed);
            task.IsFeatureMode = true;
            task.UseMcp = true;
            task.ExtendedPlanning = true;
            task.AutoDecompose = true;

            _collector.CollectAndAnalyze(task);

            SafeFileWriter.FlushAll();

            var entries = _store.LoadEntries(task.ProjectPath);
            Assert.True(entries[0].WasFeatureMode);
            Assert.True(entries[0].UsedMcp);
            Assert.True(entries[0].UsedExtendedPlanning);
            Assert.True(entries[0].UsedAutoDecompose);
        }

        [Fact]
        public void CollectAndAnalyze_MultipleTasks_AccumulatesEntries()
        {
            for (int i = 0; i < 5; i++)
            {
                var task = CreateTask(AgentTaskStatus.Completed);
                task.Description = $"Task {i}";
                _collector.CollectAndAnalyze(task);
                SafeFileWriter.FlushAll();
            }

            var entries = _store.LoadEntries(@"C:\TestProject");
            Assert.Equal(5, entries.Count);
        }

        private static AgentTask CreateTask(AgentTaskStatus status)
        {
            var task = new AgentTask
            {
                Description = "Test task",
                ProjectPath = @"C:\TestProject",
                StartTime = DateTime.Now.AddMinutes(-5),
                Model = ModelType.ClaudeCode
            };
            task.Status = status;
            task.EndTime = DateTime.Now;
            return task;
        }
    }
}
