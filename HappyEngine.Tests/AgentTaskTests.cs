using System.ComponentModel;
using Xunit;

namespace HappyEngine.Tests
{
    public class AgentTaskTests
    {
        [Fact]
        public void Id_Is16Characters()
        {
            var task = new AgentTask();
            Assert.Equal(16, task.Id.Length);
        }

        [Fact]
        public void Id_IsHexadecimal()
        {
            var task = new AgentTask();
            Assert.Matches("^[0-9a-f]{16}$", task.Id);
        }

        [Fact]
        public void DefaultStatus_IsRunning()
        {
            var task = new AgentTask();
            Assert.Equal(AgentTaskStatus.Running, task.Status);
        }

        [Fact]
        public void StatusText_Running_NonFeatureMode()
        {
            var task = new AgentTask { IsFeatureMode = false };
            Assert.Equal("Running", task.StatusText);
        }

        [Fact]
        public void StatusText_Running_FeatureMode_ShowsIteration()
        {
            var task = new AgentTask { IsFeatureMode = true, MaxIterations = 50 };
            task.CurrentIteration = 3;
            Assert.Equal("Running (3/50)", task.StatusText);
        }

        [Fact]
        public void StatusText_Completed()
        {
            var task = new AgentTask();
            task.Status = AgentTaskStatus.Completed;
            Assert.Equal("Finished", task.StatusText);
        }

        [Fact]
        public void StatusText_Cancelled()
        {
            var task = new AgentTask();
            task.Status = AgentTaskStatus.Cancelled;
            Assert.Equal("Cancelled", task.StatusText);
        }

        [Fact]
        public void StatusText_Failed()
        {
            var task = new AgentTask();
            task.Status = AgentTaskStatus.Failed;
            Assert.Equal("Failed", task.StatusText);
        }

        [Fact]
        public void StatusText_Queued()
        {
            var task = new AgentTask();
            task.Status = AgentTaskStatus.Queued;
            Assert.Equal("Queued", task.StatusText);
        }

        [Theory]
        [InlineData(AgentTaskStatus.Running, "#64B5F6")]
        [InlineData(AgentTaskStatus.Completed, "#00E676")]
        [InlineData(AgentTaskStatus.Cancelled, "#E0A030")]
        [InlineData(AgentTaskStatus.Failed, "#E05555")]
        [InlineData(AgentTaskStatus.Queued, "#FFD600")]
        public void StatusColor_MatchesStatus(AgentTaskStatus status, string expectedColor)
        {
            var task = new AgentTask();
            task.Status = status;
            Assert.Equal(expectedColor, task.StatusColor);
        }

        [Fact]
        public void ShortDescription_ShortText_NotTruncated()
        {
            var task = new AgentTask { Description = "Fix a bug" };
            Assert.Equal("Fix a bug", task.ShortDescription);
        }

        [Fact]
        public void ShortDescription_LongText_TruncatedAt45()
        {
            var task = new AgentTask { Description = new string('a', 50) };
            Assert.Equal(48, task.ShortDescription.Length); // 45 + "..."
            Assert.EndsWith("...", task.ShortDescription);
        }

        [Fact]
        public void ShortDescription_Exactly45_NotTruncated()
        {
            var task = new AgentTask { Description = new string('x', 45) };
            Assert.Equal(45, task.ShortDescription.Length);
            Assert.DoesNotContain("...", task.ShortDescription);
        }

        [Fact]
        public void ProjectName_ExtractsFileName()
        {
            var task = new AgentTask { ProjectPath = @"C:\Users\dev\MyProject" };
            Assert.Equal("MyProject", task.ProjectName);
        }

        [Fact]
        public void ProjectName_EmptyPath_ReturnsEmpty()
        {
            var task = new AgentTask { ProjectPath = "" };
            Assert.Equal("", task.ProjectName);
        }

        [Fact]
        public void PropertyChanged_Fires_WhenStatusChanges()
        {
            var task = new AgentTask();
            var changedProperties = new List<string>();
            task.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

            task.Status = AgentTaskStatus.Completed;

            Assert.Contains("Status", changedProperties);
            Assert.Contains("StatusText", changedProperties);
            Assert.Contains("StatusColor", changedProperties);
            Assert.Contains("IsRunning", changedProperties);
        }

        [Fact]
        public void PropertyChanged_Fires_WhenCurrentIterationChanges()
        {
            var task = new AgentTask();
            var changedProperties = new List<string>();
            task.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

            task.CurrentIteration = 5;

            Assert.Contains("CurrentIteration", changedProperties);
            Assert.Contains("StatusText", changedProperties);
        }

        [Fact]
        public void IsRunning_TrueWhenRunning()
        {
            var task = new AgentTask();
            Assert.True(task.IsRunning);
        }

        [Fact]
        public void IsRunning_FalseWhenCompleted()
        {
            var task = new AgentTask();
            task.Status = AgentTaskStatus.Completed;
            Assert.False(task.IsRunning);
        }

        [Fact]
        public void MaxIterations_DefaultIs50()
        {
            var task = new AgentTask();
            Assert.Equal(50, task.MaxIterations);
        }

        [Fact]
        public void GitStartHash_DefaultIsNull()
        {
            var task = new AgentTask();
            Assert.Null(task.GitStartHash);
        }

        [Fact]
        public void CompletionSummary_DefaultIsEmpty()
        {
            var task = new AgentTask();
            Assert.Equal("", task.CompletionSummary);
        }

        [Fact]
        public void PropertyChanged_Fires_WhenCompletionSummaryChanges()
        {
            var task = new AgentTask();
            var changedProperties = new List<string>();
            task.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

            task.CompletionSummary = "test summary";

            Assert.Contains("CompletionSummary", changedProperties);
        }
    }
}
