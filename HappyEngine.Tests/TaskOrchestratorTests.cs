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

        // ── Diamond dependency patterns ──────────────────────────────────

        [Fact]
        public void Diamond_ReversedCompletionOrder_DFiresAfterBothPaths()
        {
            //    A
            //   / \
            //  B   C
            //   \ /
            //    D
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("a");
            readyTasks.Clear();

            // Complete C first (reversed from the other diamond test)
            orch.OnTaskCompleted("c");
            Assert.Empty(readyTasks); // D not ready yet

            orch.OnTaskCompleted("b");
            Assert.Single(readyTasks);
            Assert.Equal("d", readyTasks[0].Id);
        }

        [Fact]
        public void Diamond_DFiresExactlyOnce_NotPerPath()
        {
            //    A
            //   / \
            //  B   C
            //   \ /
            //    D
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            int dReadyCount = 0;
            orch.TaskReady += t => { if (t.Id == "d") dReadyCount++; };

            orch.OnTaskCompleted("a");
            orch.OnTaskCompleted("b");
            orch.OnTaskCompleted("c");

            // D must fire exactly once, not once per parent
            Assert.Equal(1, dReadyCount);
        }

        [Fact]
        public void DoubleDiamond_FullPropagation()
        {
            // Double diamond: A→{B,C}→D→{E,F}→G
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });
            orch.AddTask(MakeTask("e"), new List<string> { "d" });
            orch.AddTask(MakeTask("f"), new List<string> { "d" });
            orch.AddTask(MakeTask("g"), new List<string> { "e", "f" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Phase 1: Complete A → B,C become ready
            orch.OnTaskCompleted("a");
            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "b");
            Assert.Contains(readyTasks, t => t.Id == "c");
            readyTasks.Clear();

            // Phase 2: Complete B,C → D becomes ready
            orch.OnTaskCompleted("b");
            Assert.Empty(readyTasks);
            orch.OnTaskCompleted("c");
            Assert.Single(readyTasks);
            Assert.Equal("d", readyTasks[0].Id);
            readyTasks.Clear();

            // Phase 3: Complete D → E,F become ready
            orch.OnTaskCompleted("d");
            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "e");
            Assert.Contains(readyTasks, t => t.Id == "f");
            readyTasks.Clear();

            // Phase 4: Complete E,F → G becomes ready
            orch.OnTaskCompleted("e");
            Assert.Empty(readyTasks);
            orch.OnTaskCompleted("f");
            Assert.Single(readyTasks);
            Assert.Equal("g", readyTasks[0].Id);
        }

        [Fact]
        public void Diamond_WithPriority_ReadyTasksSortedCorrectly()
        {
            //    A
            //   / \
            //  B   C  (B=High, C=Critical)
            //   \ /
            //    D
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTaskWithPriority("b", TaskPriority.High), new List<string> { "a" });
            orch.AddTask(MakeTaskWithPriority("c", TaskPriority.Critical), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete A → Both B and C fire, C (Critical) should come first
            orch.OnTaskCompleted("a");
            Assert.Equal(2, readyTasks.Count);
            Assert.Equal("c", readyTasks[0].Id); // Critical first
            Assert.Equal("b", readyTasks[1].Id); // High second
        }

        [Fact]
        public void WideFanIn_SingleTaskDependsOnMany()
        {
            // 5 independent tasks → single joiner
            var orch = new TaskOrchestrator();
            for (int i = 0; i < 5; i++)
                orch.AddTask(MakeTask($"src{i}"), new List<string>());

            var deps = Enumerable.Range(0, 5).Select(i => $"src{i}").ToList();
            orch.AddTask(MakeTask("join"), deps);

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete 4 of 5 → join must NOT fire
            for (int i = 0; i < 4; i++)
            {
                orch.OnTaskCompleted($"src{i}");
                Assert.DoesNotContain(readyTasks, t => t.Id == "join");
            }

            // Complete last → join fires
            orch.OnTaskCompleted("src4");
            Assert.Contains(readyTasks, t => t.Id == "join");
        }

        [Fact]
        public void Diamond_InterleavedWithUnrelated_OnlyCorrectTasksFire()
        {
            //    A         X (independent)
            //   / \
            //  B   C
            //   \ /
            //    D
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });
            orch.AddTask(MakeTask("x"), new List<string>()); // independent

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Completing X should NOT affect diamond at all
            orch.OnTaskCompleted("x");
            Assert.Empty(readyTasks); // no tasks newly unblocked

            // Now proceed with diamond
            orch.OnTaskCompleted("a");
            Assert.Equal(2, readyTasks.Count);
            Assert.DoesNotContain(readyTasks, t => t.Id == "d");
        }

        // ── Cycle detection - circular chains ────────────────────────────

        [Fact]
        public void DetectCycle_LongCircularChain_ReturnsTrue()
        {
            // A → B → C → D → E, adding E → A would form a 5-node cycle
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "b" });
            orch.AddTask(MakeTask("d"), new List<string> { "c" });
            orch.AddTask(MakeTask("e"), new List<string> { "d" });

            Assert.True(orch.DetectCycle("e", "a"));
        }

        [Fact]
        public void DetectCycle_CycleInSubgraph_DetectedCorrectly()
        {
            // Independent subgraph: X → Y → Z
            // Main chain: A → B
            // Adding Z → X would create cycle in subgraph but not affect main chain
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("x"), new List<string>());
            orch.AddTask(MakeTask("y"), new List<string> { "x" });
            orch.AddTask(MakeTask("z"), new List<string> { "y" });

            Assert.True(orch.DetectCycle("z", "x"));   // cycle in subgraph
            Assert.False(orch.DetectCycle("b", "x"));   // no cross-subgraph cycle
        }

        [Fact]
        public void DetectCycle_DiamondWithBackEdge_ReturnsTrue()
        {
            //    A
            //   / \
            //  B   C
            //   \ /
            //    D
            // Adding D → A would create a cycle through the diamond
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            Assert.True(orch.DetectCycle("d", "a"));
            // Also verify mid-diamond back edges
            Assert.True(orch.DetectCycle("d", "b"));
            Assert.True(orch.DetectCycle("d", "c"));
            Assert.True(orch.DetectCycle("b", "a"));
        }

        [Fact]
        public void DetectCycle_MultiplePotentialPaths_ChecksAll()
        {
            // A → B, A → C, B → D, C → D
            // Adding D → A creates cycle through both B and C paths
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            Assert.True(orch.DetectCycle("d", "a"));
            // Non-cycle edge: adding new task E → D is fine
            Assert.False(orch.DetectCycle("e", "d"));
        }

        [Fact]
        public void DetectCycle_AlreadyResolvedNode_StillDetectsCycle()
        {
            // Even if A is resolved, the graph structure still has the edge
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });

            orch.OnTaskCompleted("a");

            // The edge a→b still exists in the graph, so b→a would still be a cycle
            Assert.True(orch.DetectCycle("b", "a"));
        }

        // ── Priority ordering - simultaneous readiness ───────────────────

        [Fact]
        public void Priority_AllFourLevels_BecomeReadySimultaneously()
        {
            // Single dependency unblocks tasks of all 4 priority levels
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            orch.AddTask(dep, new List<string>());
            orch.AddTask(MakeTaskWithPriority("low", TaskPriority.Low), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("norm", TaskPriority.Normal), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("high", TaskPriority.High), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("crit", TaskPriority.Critical), new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep");

            Assert.Equal(4, readyTasks.Count);
            Assert.Equal("crit", readyTasks[0].Id);
            Assert.Equal("high", readyTasks[1].Id);
            Assert.Equal("norm", readyTasks[2].Id);
            Assert.Equal("low", readyTasks[3].Id);
        }

        [Fact]
        public void Priority_SameLevelDifferentSecondary_OrderedBySecondary()
        {
            // All tasks are High priority but have different secondary priority values
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            orch.AddTask(dep, new List<string>());
            orch.AddTask(MakeTaskWithPriority("p1", TaskPriority.High, 1), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("p5", TaskPriority.High, 5), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("p3", TaskPriority.High, 3), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("p10", TaskPriority.High, 10), new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep");

            Assert.Equal(4, readyTasks.Count);
            Assert.Equal("p10", readyTasks[0].Id);
            Assert.Equal("p5", readyTasks[1].Id);
            Assert.Equal("p3", readyTasks[2].Id);
            Assert.Equal("p1", readyTasks[3].Id);
        }

        [Fact]
        public void Priority_MixedLevelsAndSecondary_LevelTakesPrecedence()
        {
            var orch = new TaskOrchestrator();
            var dep = MakeTask("dep");
            orch.AddTask(dep, new List<string>());

            // Low level with high secondary should still come after High level with low secondary
            orch.AddTask(MakeTaskWithPriority("low_p99", TaskPriority.Low, 99), new List<string> { "dep" });
            orch.AddTask(MakeTaskWithPriority("high_p1", TaskPriority.High, 1), new List<string> { "dep" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("dep");

            Assert.Equal(2, readyTasks.Count);
            Assert.Equal("high_p1", readyTasks[0].Id);  // High level first despite low secondary
            Assert.Equal("low_p99", readyTasks[1].Id);   // Low level last despite high secondary
        }

        [Fact]
        public void GetNextRunnableTasks_ExcludesResolvedTasks()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTaskWithPriority("a", TaskPriority.Critical), new List<string>());
            orch.AddTask(MakeTaskWithPriority("b", TaskPriority.Normal), new List<string>());
            orch.AddTask(MakeTaskWithPriority("c", TaskPriority.High), new List<string>());

            orch.OnTaskCompleted("a"); // resolved

            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Equal(2, runnable.Count);
            Assert.DoesNotContain(runnable, t => t.Id == "a");
            Assert.Equal("c", runnable[0].Id);  // High before Normal
            Assert.Equal("b", runnable[1].Id);
        }

        // ── ReevaluateAll - comprehensive scenarios ──────────────────────

        [Fact]
        public void ReevaluateAll_AfterPartialCompletion_FiresCorrectSubset()
        {
            // A → B, C → D, E (independent)
            // Complete A and C, then reevaluate
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string>());
            orch.AddTask(MakeTask("d"), new List<string> { "c" });
            orch.AddTask(MakeTask("e"), new List<string>());

            orch.OnTaskCompleted("a"); // unblocks B
            orch.OnTaskCompleted("c"); // unblocks D

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            // B, D, and E should all be ready (A and C are resolved)
            Assert.Equal(3, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "b");
            Assert.Contains(readyTasks, t => t.Id == "d");
            Assert.Contains(readyTasks, t => t.Id == "e");
        }

        [Fact]
        public void ReevaluateAll_CalledTwice_FiresEachTime()
        {
            // ReevaluateAll is not idempotent - it fires for all currently-ready tasks each call
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string>());

            var readyCount = 0;
            orch.TaskReady += _ => readyCount++;

            orch.ReevaluateAll();
            Assert.Equal(2, readyCount);

            orch.ReevaluateAll();
            Assert.Equal(4, readyCount); // fires again for same tasks
        }

        [Fact]
        public void ReevaluateAll_MixedResolvedAndBlocked_OnlyFiresForReady()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("resolved"), new List<string>());
            orch.AddTask(MakeTask("ready"), new List<string>());
            orch.AddTask(MakeTask("blocked"), new List<string> { "dep" });

            orch.MarkResolved("resolved");

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            Assert.Single(readyTasks);
            Assert.Equal("ready", readyTasks[0].Id);
        }

        [Fact]
        public void ReevaluateAll_ComplexDiamond_OnlyLeafReady()
        {
            //    A (resolved)
            //   / \
            //  B   C  (B resolved, C not)
            //   \ /
            //    D    (blocked by C)
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            orch.OnTaskCompleted("a");
            orch.OnTaskCompleted("b");
            // C is not completed, so D is still blocked

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            // Only C should be ready (not resolved, no unresolved deps since A is done)
            Assert.Single(readyTasks);
            Assert.Equal("c", readyTasks[0].Id);
        }

        [Fact]
        public void ReevaluateAll_EmptyOrchestrator_DoesNotThrow()
        {
            var orch = new TaskOrchestrator();
            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            Assert.Empty(readyTasks);
        }

        // ── Edge cases ───────────────────────────────────────────────────

        [Fact]
        public void OnTaskCompleted_CalledTwice_FiresAgainForUnresolvedDependents()
        {
            // OnTaskCompleted is NOT idempotent: if a dependent is still unresolved,
            // it will fire TaskReady again on repeated calls because the dependent
            // still meets the "unresolved deps == 0 && !IsResolved" criteria.
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("dep"), new List<string>());
            orch.AddTask(MakeTask("child"), new List<string> { "dep" });

            int childReadyCount = 0;
            orch.TaskReady += t => { if (t.Id == "child") childReadyCount++; };

            orch.OnTaskCompleted("dep");
            Assert.Equal(1, childReadyCount);

            orch.OnTaskCompleted("dep"); // second completion
            // Fires again because child is still !IsResolved with 0 unresolved deps
            Assert.Equal(2, childReadyCount);
        }

        [Fact]
        public void OnTaskCompleted_PlaceholderDependency_UnblocksChild()
        {
            // Add child with dependency "phantom" that was never AddTask'd as a real task
            // (only exists as a placeholder node). Completing "phantom" should unblock child.
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("child"), new List<string> { "phantom" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.OnTaskCompleted("phantom");

            Assert.Single(readyTasks);
            Assert.Equal("child", readyTasks[0].Id);
        }

        [Fact]
        public void AddTask_AfterDependencyAlreadyCompleted_TaskStillBlocked()
        {
            // Complete dep1 first, then add a task depending on it
            // The task should remain blocked because OnTaskCompleted already ran
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("dep1"), new List<string>());
            orch.OnTaskCompleted("dep1");

            // Now add a task depending on dep1
            orch.AddTask(MakeTask("late"), new List<string> { "dep1" });

            // "late" has dep1 in UnresolvedDependencies but dep1 is already resolved
            // This is a known characteristic: the task won't auto-unblock
            // ReevaluateAll or checking GetNextRunnableTasks shows it's still blocked
            var runnable = orch.GetNextRunnableTasks(10);
            Assert.DoesNotContain(runnable, t => t.Id == "late");
        }

        [Fact]
        public void DeepChain_PropagatesOneStepAtATime()
        {
            // Chain of 10 tasks: t0 → t1 → t2 → ... → t9
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("t0"), new List<string>());
            for (int i = 1; i < 10; i++)
                orch.AddTask(MakeTask($"t{i}"), new List<string> { $"t{i - 1}" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Each completion should only unblock the immediate successor
            for (int i = 0; i < 9; i++)
            {
                readyTasks.Clear();
                orch.OnTaskCompleted($"t{i}");

                Assert.Single(readyTasks);
                Assert.Equal($"t{i + 1}", readyTasks[0].Id);
            }
        }

        [Fact]
        public void RemoveTask_InMiddleOfDiamond_UnblocksFinalTask()
        {
            //    A
            //   / \
            //  B   C
            //   \ /
            //    D
            // Removing B should unblock D (only C remains as dependency)
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());
            orch.AddTask(MakeTask("b"), new List<string> { "a" });
            orch.AddTask(MakeTask("c"), new List<string> { "a" });
            orch.AddTask(MakeTask("d"), new List<string> { "b", "c" });

            orch.OnTaskCompleted("a");

            // Remove B - this cleans up B from D's unresolved deps
            orch.RemoveTask("b");

            // D now only depends on C
            orch.OnTaskCompleted("c");

            // After C completes, D should be runnable (B dep was removed)
            var runnable = orch.GetNextRunnableTasks(10);
            Assert.Contains(runnable, t => t.Id == "d");
        }

        [Fact]
        public void RemoveTask_ThenReevaluate_WorksCorrectly()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("dep"), new List<string>());
            orch.AddTask(MakeTask("child"), new List<string> { "dep" });

            // Remove dep → child's dependency is cleaned up
            orch.RemoveTask("dep");

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            // child should now be ready since its only dep was removed
            Assert.Single(readyTasks);
            Assert.Equal("child", readyTasks[0].Id);
        }

        [Fact]
        public void MarkResolved_ThenOnTaskCompleted_NoDoubleResolve()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("dep"), new List<string>());
            orch.AddTask(MakeTask("child"), new List<string> { "dep" });

            // MarkResolved doesn't unblock dependents
            orch.MarkResolved("dep");
            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // OnTaskCompleted after MarkResolved should still work
            // (node is already resolved but dependents haven't been unblocked)
            orch.OnTaskCompleted("dep");

            Assert.Single(readyTasks);
            Assert.Equal("child", readyTasks[0].Id);
        }

        [Fact]
        public void ContainsTask_AfterRemoval_ReturnsFalse()
        {
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("task1"), new List<string>());

            Assert.True(orch.ContainsTask("task1"));

            orch.RemoveTask("task1");

            Assert.False(orch.ContainsTask("task1"));
        }

        [Fact]
        public void AddTask_DuplicateId_OverwritesTask()
        {
            var orch = new TaskOrchestrator();
            var task1 = MakeTaskWithPriority("same", TaskPriority.Low);
            var task2 = MakeTaskWithPriority("same", TaskPriority.Critical);

            orch.AddTask(task1, new List<string>());
            orch.AddTask(task2, new List<string>());

            var runnable = orch.GetNextRunnableTasks(10);
            // Should contain the task with updated priority
            Assert.Contains(runnable, t => t.Id == "same" && t.PriorityLevel == TaskPriority.Critical);
        }

        [Fact]
        public void ComplexDAG_MultipleRootsMultipleSinks()
        {
            // Two independent roots, merging into a shared middle, then splitting to two sinks
            //  R1   R2
            //   \  / \
            //    M1   M2
            //   / \   |
            //  S1  S2 S3
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("r1"), new List<string>());
            orch.AddTask(MakeTask("r2"), new List<string>());
            orch.AddTask(MakeTask("m1"), new List<string> { "r1", "r2" });
            orch.AddTask(MakeTask("m2"), new List<string> { "r2" });
            orch.AddTask(MakeTask("s1"), new List<string> { "m1" });
            orch.AddTask(MakeTask("s2"), new List<string> { "m1" });
            orch.AddTask(MakeTask("s3"), new List<string> { "m2" });

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            // Complete R2 → M2 unblocked (M1 still waiting on R1)
            orch.OnTaskCompleted("r2");
            Assert.Single(readyTasks);
            Assert.Equal("m2", readyTasks[0].Id);
            readyTasks.Clear();

            // Complete R1 → M1 unblocked (both roots done)
            orch.OnTaskCompleted("r1");
            Assert.Single(readyTasks);
            Assert.Equal("m1", readyTasks[0].Id);
            readyTasks.Clear();

            // Complete M2 → S3 unblocked
            orch.OnTaskCompleted("m2");
            Assert.Single(readyTasks);
            Assert.Equal("s3", readyTasks[0].Id);
            readyTasks.Clear();

            // Complete M1 → S1, S2 unblocked
            orch.OnTaskCompleted("m1");
            Assert.Equal(2, readyTasks.Count);
            Assert.Contains(readyTasks, t => t.Id == "s1");
            Assert.Contains(readyTasks, t => t.Id == "s2");
        }

        [Fact]
        public void OnTaskCompleted_NeverRegisteredTask_DoesNotThrow()
        {
            // Completing a task that was never added (not even as a placeholder)
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTask("a"), new List<string>());

            var exception = Record.Exception(() => orch.OnTaskCompleted("never_existed"));
            Assert.Null(exception);
        }

        [Fact]
        public void ReevaluateAll_WithPriority_AfterChainCompletion()
        {
            // Setup: chain A→B, and independent tasks with varied priorities
            var orch = new TaskOrchestrator();
            orch.AddTask(MakeTaskWithPriority("a", TaskPriority.Normal), new List<string>());
            orch.AddTask(MakeTaskWithPriority("b", TaskPriority.Critical), new List<string> { "a" });
            orch.AddTask(MakeTaskWithPriority("x", TaskPriority.Low), new List<string>());
            orch.AddTask(MakeTaskWithPriority("y", TaskPriority.High), new List<string>());

            orch.OnTaskCompleted("a"); // unblocks B

            var readyTasks = new List<AgentTask>();
            orch.TaskReady += t => readyTasks.Add(t);

            orch.ReevaluateAll();

            // Should fire for B (Critical), Y (High), X (Low) - in priority order
            // A is resolved so it's excluded
            Assert.Equal(3, readyTasks.Count);
            Assert.Equal("b", readyTasks[0].Id);    // Critical
            Assert.Equal("y", readyTasks[1].Id);     // High
            Assert.Equal("x", readyTasks[2].Id);     // Low
        }
    }
}
