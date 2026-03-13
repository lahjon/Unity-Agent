using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Spritely.Managers;
using Spritely.Models;
using Xunit;

namespace Spritely.Tests
{
    public class FeedbackStoreTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly FeedbackStore _store;

        public FeedbackStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "spritely_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
            _store = new FeedbackStore(_tempDir);
        }

        private static void Flush() => SafeFileWriter.FlushAll();

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        [Fact]
        public void SaveEntry_ThenLoadEntries_RoundTrips()
        {
            var entry = CreateEntry("proj1", "task1");
            _store.SaveEntry(entry);

            // Flush background writes
            Flush();

            var loaded = _store.LoadEntries("proj1");
            Assert.Single(loaded);
            Assert.Equal("task1", loaded[0].TaskId);
        }

        [Fact]
        public void LoadEntries_NoFile_ReturnsEmpty()
        {
            var entries = _store.LoadEntries(@"C:\nonexistent\project");
            Assert.Empty(entries);
        }

        [Fact]
        public void SaveEntry_MultipleSameProject_AccumulatesEntries()
        {
            for (int i = 0; i < 5; i++)
            {
                _store.SaveEntry(CreateEntry("proj1", $"task{i}"));
                Flush();
            }

            var loaded = _store.LoadEntries("proj1");
            Assert.Equal(5, loaded.Count);
        }

        [Fact]
        public void SaveEntry_DifferentProjects_IsolatedStorage()
        {
            _store.SaveEntry(CreateEntry("projA", "taskA"));
            _store.SaveEntry(CreateEntry("projB", "taskB"));

            Flush();

            Assert.Single(_store.LoadEntries("projA"));
            Assert.Single(_store.LoadEntries("projB"));
        }

        [Fact]
        public void SaveEntry_ExceedsCap_KeepsLast200()
        {
            for (int i = 0; i < 210; i++)
            {
                _store.SaveEntry(CreateEntry("proj1", $"task{i}"));
                Flush();
            }

            var loaded = _store.LoadEntries("proj1");
            Assert.Equal(200, loaded.Count);
            Assert.Equal("task209", loaded[^1].TaskId);
        }

        [Fact]
        public void SaveInsight_ThenLoadInsights_RoundTrips()
        {
            var insight = new FeedbackInsight
            {
                ProjectPath = "proj1",
                SuccessRate = 0.85,
                TasksAnalyzed = 10
            };
            _store.SaveInsight(insight);

            Flush();

            var loaded = _store.LoadInsights("proj1");
            Assert.Single(loaded);
            Assert.Equal(0.85, loaded[0].SuccessRate);
        }

        [Fact]
        public void LoadInsights_NoFile_ReturnsEmpty()
        {
            var insights = _store.LoadInsights(@"C:\nonexistent");
            Assert.Empty(insights);
        }

        [Fact]
        public void GetEntriesSinceLastInsight_NoInsights_ReturnsAllEntries()
        {
            for (int i = 0; i < 5; i++)
            {
                _store.SaveEntry(CreateEntry("proj1", $"task{i}"));
                Flush();
            }

            var count = _store.GetEntriesSinceLastInsight("proj1");
            Assert.Equal(5, count);
        }

        [Fact]
        public void GetEntriesSinceLastInsight_WithInsight_CountsOnlyNewer()
        {
            // Save old entries
            for (int i = 0; i < 3; i++)
            {
                var entry = CreateEntry("proj1", $"old{i}");
                entry.Timestamp = DateTime.Now.AddMinutes(-10);
                _store.SaveEntry(entry);
                Flush();
            }

            // Save insight
            var insight = new FeedbackInsight
            {
                ProjectPath = "proj1",
                GeneratedAt = DateTime.Now.AddMinutes(-5)
            };
            _store.SaveInsight(insight);
            Flush();

            // Save new entries
            for (int i = 0; i < 2; i++)
            {
                _store.SaveEntry(CreateEntry("proj1", $"new{i}"));
                Flush();
            }

            var count = _store.GetEntriesSinceLastInsight("proj1");
            Assert.Equal(2, count);
        }

        [Fact]
        public void LoadAllEntries_AcrossProjects_ReturnsAll()
        {
            _store.SaveEntry(CreateEntry("projA", "taskA"));
            _store.SaveEntry(CreateEntry("projB", "taskB"));

            Flush();

            var all = _store.LoadAllEntries();
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public void PathHash_CaseInsensitive()
        {
            _store.SaveEntry(CreateEntry(@"C:\MyProject", "task1"));

            Flush();

            // Same path different case should find the same file
            var loaded = _store.LoadEntries(@"c:\myproject");
            Assert.Single(loaded);
        }

        private static FeedbackEntry CreateEntry(string projectPath, string taskId) => new()
        {
            TaskId = taskId,
            ProjectPath = projectPath,
            Description = $"Test task {taskId}",
            Timestamp = DateTime.Now,
            Status = "Completed"
        };
    }
}
