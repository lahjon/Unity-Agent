using HappyEngine;
using HappyEngine.Managers;

namespace HappyEngine.Tests
{
    public class TaskOrchestratorTests
    {
        // ── Helpers ──────────────────────────────────────────────────────

        private static AgentTask MakeTask(string? id = null)
        {
            var t = new AgentTask { Description = "test" };
            if (id != null)
            {
                // Use reflection to set the init-only Id on the underlying Data object
                var dataProp = typeof(AgentTaskData).GetProperty(nameof(AgentTaskData.Id));
                dataProp!.SetValue(t.Data, id);
            }
            return t;
        }

        private static AgentTask MakeTaskWithPriority(string id, TaskPriority level, int priority = 0)
        {
            var t = MakeTask(id);
            t.PriorityLevel = level;
            t.Priority = priority;
            return t;
        }

        // ── AddTask & GetNextRunnableTasks ────────────────────────────────

        [Fact]
        public void AddTask_NoDependencies_TaskIsImmediatelyRunnable()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");

            orch.AddTask(task, new List<string>());

            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Single(runnable);
            Assert.Equal("task1", runnable[0].Id);
        }

        [Fact]
        public void AddTask_WithDependencies_TaskIsNotRunnable()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");

            orch.AddTask(task, new List<string> { "dep1", "dep2" });

            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Empty(runnable);
        }

        [Fact]
        public void AddTask_MultipleTasks_MaxCountLimitsResults()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string>());
            orch.AddTask(MakeTask("c"), new List<string>());

            var runnable = orch.GetNextRunnableTasks(2);
            Assert.Equal(2, runnable.Count);
        }

        [Fact]
        public void GetNextRunnableTasks_OrdersByPriorityLevelThenPriority()
        {
            var orch = new TaskOrchestrator();
            var low = MakeTaskWithPriority("low", TaskPriority.Low, 0);
            var normal = MakeTaskWithPriority("normal", TaskPriority.Normal, 5);
            var high = MakeTaskWithPriority("high", TaskPriority.High, 0);
            var critical = MakeTaskWithPriority("crit", TaskPriority.Critical, 0);

            orch.AddTask(low, new List<string>());
            orch.AddTask(normal, new List<string>());
            orch.AddTask(high, new List<string>());
            orch.AddTask(critical, new List<string>());

            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Equal(4, runnable.Count);
            Assert.Equal("crit", runnable[0].Id);
            Assert.Equal("high", runnable[1].Id);
            Assert.Equal("normal", runnable[2].Id);
            Assert.Equal("low", runnable[3].Id);
        }

        [Fact]
        public void GetNextRunnableTasks_SamePriorityLevel_OrdersByPriorityDescending()
        {
            var orch = new TaskOrchestrator();
            var p1 = MakeTaskWithPriority("p1", TaskPriority.Normal, 1);
            var p5 = MakeTaskWithPriority("p5", TaskPriority.Normal, 5);
            var p3 = MakeTaskWithPriority("p3", TaskPriority.Normal, 3);

            orch.AddTask(p1, new List<string>());
            orch.AddTask(p5, new List<string>());
            orch.AddTask(p3, new List<string>());

            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Equal("p5", runnable[0].Id);
            Assert.Equal("p3", runnable[1].Id);
            Assert.Equal("p1", runnable[2].Id);
        }

        // ── OnTaskCompleted & TaskReady event ─────────────────────────────

        [Fact]
        public void OnTaskCompleted_UnblocksDependentTask_FiresTaskReady()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep1");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep1" });

            // blocked should not be runnable yet
            Assert.DoesNotContain(orch.GetNextRunnableTasks(10), t => t.Id == "blocked");

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep1");

            Assert.Single(readyTasks);
            Assert.Equal("blocked", readyTasks[0].Id);
        }

        [Fact]
        public void OnTaskCompleted_MultiDependencies_FiresOnlyAfterAllResolved()
        {
            var orch = new TaskOrchestrator();
            var dep1 = MakeTask("dep1");
            var dep2 = MakeTask("dep2");
            var dep3 = MakeTask("dep3");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep1, new List<string>());
            orch.AddTask(dep2, new List<string>());
            orch.AddTask(dep3, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep1", "dep2", "dep3" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete first two - blocked should NOT become ready
            orch.OnTaskCompleted("dep1");
            Assert.Empty(readyTasks);

            orch.OnTaskCompleted("dep2");
            Assert.Empty(readyTasks);

            // Complete the last dependency - NOW blocked becomes ready
            orch.OnTaskCompleted("dep3");
            Assert.Single(readyTasks);
            Assert.Equal("blocked", readyTasks[0].Id);
        }

        [Fact]
        public void OnTaskCompleted_UnblocksMultipleDependents()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var child1 = MakeTask("child1");
            var child2 = MakeTask("child2");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(child1, new List<string> { "dep" });
            orch.AddTask(child2, new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep");

            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "child1");
            Assert.Contains(readyTasks, t => t.Id == "child2");
        }

        [Fact]
        public void OnTaskCompleted_NewlyReadyTasks_SortedByPriority()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var lowChild = MakeTaskWithPriority("low", TaskPriority.Low);
            var highChild = MakeTaskWithPriority("high", TaskPriority.High);

            orch.AddTask(dep, new List<string>());
            orch.AddTask(lowChild, new List<string> { "dep" });
            orch.AddTask(highChild, new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep");

            Assert.Equal(2, readyTasks.Count);
            Assert.Equal("high", readyTasks[0].Id);
            Assert.Equal("low", readyTasks[1].Id);
        }

        [Fact]
        public void OnTaskCompleted_UnknownTaskId_DoesNotThrow()
        {
            var orch = new TaskOrchestrator();
            orch.OnTaskCompleted("nonexistent");
            // Should silently return
        }

        [Fact]
        public void OnTaskCompleted_ChainedDependencies_PropagateCorrectly()
        {
            // A -> B -> C (chain: C depends on B, B depends on A)
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");
            var c = MakeTask("c");

            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });
            orch.AddTask(c, new List<string> { "b" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete A -> B becomes ready
            orch.OnTaskCompleted("a");
            Assert.Single(readyTasks);
            Assert.Equal("b", readyTasks[0].Id);

            readyTasks.Clear();

            // Complete B -> C becomes ready
            orch.OnTaskCompleted("b");
            Assert.Single(readyTasks);
            Assert.Equal("c", readyTasks[0].Id);
        }

        // ── DetectCycle ───────────────────────────────────────────────────

        [Fact]
        public void DetectCycle_NoCycle_ReturnsFalse()
        {
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");

            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });

            // Adding c -> b would not create a cycle
            Assert.False(orch.DetectCycle("c", "b"));
        }

        [Fact]
        public void DetectCycle_DirectCycle_ReturnsTrue()
        {
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");

            // b depends on a, so a has dependent b
            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });

            // Adding b -> a would create a cycle (a -> b -> a)
            Assert.True(orch.DetectCycle("b", "a"));
        }

        [Fact]
        public void DetectCycle_IndirectCycle_ReturnsTrue()
        {
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");
            var c = MakeTask("c");

            // Chain: c depends on b, b depends on a
            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });
            orch.AddTask(c, new List<string> { "b" });

            // Adding c -> a would create cycle (a -> b -> c -> a)
            Assert.True(orch.DetectCycle("c", "a"));
        }

        [Fact]
        public void DetectCycle_SelfCycle_ReturnsTrue()
        {
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            orch.AddTask(a, new List<string>());

            // a -> a is a self-cycle
            Assert.True(orch.DetectCycle("a", "a"));
        }

        [Fact]
        public void DetectCycle_ParallelPaths_NoCycle_ReturnsFalse()
        {
            // Diamond: d depends on b and c, both depend on a
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");
            var c = MakeTask("c");
            var d = MakeTask("d");

            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });
            orch.AddTask(c, new List<string> { "a" });
            orch.AddTask(d, new List<string> { "b", "c" });

            // No cycle exists in a diamond DAG
            Assert.False(orch.DetectCycle("e", "d"));
        }

        [Fact]
        public void DetectCycle_EmptyGraph_ReturnsFalse()
        {
            var orch = new TaskOrchestrator();
            Assert.False(orch.DetectCycle("x", "y"));
        }

        // ── MarkResolved ──────────────────────────────────────────────────

        [Fact]
        public void MarkResolved_PreventsTaskFromBeingRunnable()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");
            orch.AddTask(task, new List<string>());

            // Task should initially be runnable
            Assert.Single(orch.GetNextRunnableTasks(10));

            orch.MarkResolved("task1");

            // After MarkResolved, task should no longer be runnable
            Assert.Empty(orch.GetNextRunnableTasks(10));
        }

        [Fact]
        public void MarkResolved_DoesNotFireTaskReadyEvent()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // MarkResolved should NOT fire TaskReady (unlike OnTaskCompleted)
            orch.MarkResolved("dep");

            Assert.Empty(readyTasks);
        }

        [Fact]
        public void MarkResolved_DoesNotUnblockDependents()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep" });

            orch.MarkResolved("dep");

            // blocked should still not be runnable because MarkResolved doesn't
            // remove the dependency from dependent nodes
            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Empty(runnable);
        }

        [Fact]
        public void MarkResolved_UnknownTaskId_DoesNotThrow()
        {
            var orch = new TaskOrchestrator();
            orch.MarkResolved("nonexistent");
        }

        // ── ReevaluateAll ─────────────────────────────────────────────────

        [Fact]
        public void ReevaluateAll_FiresTaskReadyForAllUnblockedTasks()
        {
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");

            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string>());

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "a");
            Assert.Contains(readyTasks, t => t.Id == "b");
        }

        [Fact]
        public void ReevaluateAll_DoesNotFireForBlockedTasks()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            // Only dep should fire (blocked still has unresolved dependency)
            Assert.Single(readyTasks);
            Assert.Equal("dep", readyTasks[0].Id);
        }

        [Fact]
        public void ReevaluateAll_DoesNotFireForResolvedTasks()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");
            orch.AddTask(task, new List<string>());

            orch.MarkResolved("task1");

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            Assert.Empty(readyTasks);
        }

        [Fact]
        public void ReevaluateAll_OrdersByPriority()
        {
            var orch = new TaskOrchestrator();
            var low = MakeTaskWithPriority("low", TaskPriority.Low);
            var high = MakeTaskWithPriority("high", TaskPriority.High);
            var crit = MakeTaskWithPriority("crit", TaskPriority.Critical);

            orch.AddTask(low, new List<string>());
            orch.AddTask(high, new List<string>());
            orch.AddTask(crit, new List<string>());

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            Assert.Equal(3, readyTasks.Count);
            Assert.Equal("crit", readyTasks[0].Id);
            Assert.Equal("high", readyTasks[1].Id);
            Assert.Equal("low", readyTasks[2].Id);
        }

        [Fact]
        public void ReevaluateAll_AfterDependencyManuallyCleared_FiresForNewlyReady()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep" });

            // Simulate external completion: mark dep resolved via OnTaskCompleted
            // which clears the dependency from blocked's unresolved list
            orch.OnTaskCompleted("dep");

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Now reevaluate - blocked should be ready since its dep was resolved
            orch.ReevaluateAll();

            Assert.Single(readyTasks);
            Assert.Equal("blocked", readyTasks[0].Id);
        }

        // ── RemoveTask ────────────────────────────────────────────────────

        [Fact]
        public void RemoveTask_RemovesFromOrchestrator()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");
            orch.AddTask(task, new List<string>());

            Assert.True(orch.ContainsTask("task1"));

            orch.RemoveTask("task1");

            Assert.False(orch.ContainsTask("task1"));
            Assert.Empty(orch.GetNextRunnableTasks(10));
        }

        [Fact]
        public void RemoveTask_CleansUpDependencyReferences()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            var blocked = MakeTask("blocked");

            orch.AddTask(dep, new List<string>());
            orch.AddTask(blocked, new List<string> { "dep" });

            // Remove the dependency task - this should clean up references
            // and unblock the dependent task
            orch.RemoveTask("dep");

            // blocked should now be runnable since its dependency was removed
            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Single(runnable);
            Assert.Equal("blocked", runnable[0].Id);
        }

        [Fact]
        public void RemoveTask_UnknownTaskId_DoesNotThrow()
        {
            var orch = new TaskOrchestrator();
            orch.RemoveTask("nonexistent");
        }

        // ── ContainsTask ──────────────────────────────────────────────────

        [Fact]
        public void ContainsTask_ReturnsTrue_ForTrackedTask()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");
            orch.AddTask(task, new List<string>());

            Assert.True(orch.ContainsTask("task1"));
        }

        [Fact]
        public void ContainsTask_ReturnsFalse_ForUnknownTask()
        {
            var orch = new TaskOrchestrator();
            Assert.False(orch.ContainsTask("unknown"));
        }

        [Fact]
        public void ContainsTask_ReturnsTrue_ForPlaceholderDependency()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");

            // Adding task1 with dependency "dep" creates a placeholder node for "dep"
            orch.AddTask(task, new List<string> { "dep" });

            Assert.True(orch.ContainsTask("dep"));
        }

        // ── Integration / complex DAG scenarios ───────────────────────────

        [Fact]
        public void DiamondDependency_TaskReadyFiresOnceAfterBothPathsComplete()
        {
            //    A
            //   / \
            //  B   C
            //   \ /
            //    D
            var orch = new TaskOrchestrator();
            var a = MakeTask("a");
            var b = MakeTask("b");
            var c = MakeTask("c");
            var d = MakeTask("d");

            orch.AddTask(a, new List<string>());
            orch.AddTask(b, new List<string> { "a" });
            orch.AddTask(c, new List<string> { "a" });
            orch.AddTask(d, new List<string> { "b", "c" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete A -> B and C become ready
            orch.OnTaskCompleted("a");
            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "b");
            Assert.Contains(readyTasks, t => t.Id == "c");

            readyTasks.Clear();

            // Complete B -> D is NOT ready yet (still waiting on C)
            orch.OnTaskCompleted("b");
            Assert.Empty(readyTasks);

            // Complete C -> D becomes ready
            orch.OnTaskCompleted("c");
            Assert.Single(readyTasks);
            Assert.Equal("d", readyTasks[0].Id);
        }

        [Fact]
        public void WideFanOut_AllChildrenBecomeReadyAtOnce()
        {
            var orch = new TaskOrchestrator();
            var root = MakeTask("root");
            orch.AddTask(root, new List<string>());

            var children = new List<AgentTask>();
            for (int i = 0; i < 10; i++)
            {
                var child = MakeTask($"child{i}");
                children.Add(child);
                orch.AddTask(child, new List<string> { "root" });
            }

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("root");

            Assert.Equal(10, readyTasks.Count);
        }

        [Fact]
        public void CompletedTaskDoesNotAppearInRunnableList()
        {
            var orch = new TaskOrchestrator();
            var task = MakeTask("task1");
            orch.AddTask(task, new List<string>());

            // Initially runnable
            Assert.Single(orch.GetNextRunnableTasks(10));

            orch.OnTaskCompleted("task1");

            // After completion, no longer runnable
            Assert.Empty(orch.GetNextRunnableTasks(10));
        }
    }
}
