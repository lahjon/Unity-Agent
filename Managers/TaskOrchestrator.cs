using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages a directed acyclic graph (DAG) of task dependencies and determines
    /// which tasks are ready to run based on topological order and priority.
    /// Consolidates all scheduling logic previously spread across MainWindow
    /// (CheckDependencyQueuedTasks) and FileLockManager (CheckQueuedTasks).
    /// </summary>
    public class TaskOrchestrator
    {
        private readonly Dictionary<string, TaskNode> _nodes = new();
        private readonly object _sync = new();

        /// <summary>
        /// Fires when one or more tasks become ready to run after a dependency is resolved.
        /// The handler receives each ready task.
        /// </summary>
        public event Action<AgentTask>? TaskReady;

        /// <summary>
        /// Registers a task with its dependency list. If the task has no unresolved
        /// dependencies, it becomes immediately runnable.
        /// </summary>
        public void AddTask(AgentTask task, List<string> dependsOn)
        {
            lock (_sync)
            {
                var node = GetOrCreateNode(task.Id);
                node.Task = task;

                foreach (var depId in dependsOn)
                {
                    var depNode = GetOrCreateNode(depId);
                    depNode.Dependents.Add(task.Id);
                    node.UnresolvedDependencies.Add(depId);
                }
            }
        }

        /// <summary>
        /// Returns up to <paramref name="maxCount"/> tasks whose dependencies are all
        /// resolved, ordered by descending Priority (higher = sooner).
        /// </summary>
        public List<AgentTask> GetNextRunnableTasks(int maxCount)
        {
            lock (_sync)
            {
                return _nodes.Values
                    .Where(n => n.Task != null
                                && n.UnresolvedDependencies.Count == 0
                                && !n.IsResolved)
                    .OrderByDescending(n => (int)n.Task!.PriorityLevel)
                    .ThenByDescending(n => n.Task!.Priority)
                    .Take(maxCount)
                    .Select(n => n.Task!)
                    .ToList();
            }
        }

        /// <summary>
        /// Marks a task as resolved (completed/cancelled/failed) and re-evaluates
        /// the frontier. Fires <see cref="TaskReady"/> for each newly unblocked task.
        /// </summary>
        public void OnTaskCompleted(string taskId)
        {
            List<AgentTask> nowReady;

            lock (_sync)
            {
                if (!_nodes.TryGetValue(taskId, out var node))
                    return;

                node.IsResolved = true;

                nowReady = new List<AgentTask>();

                foreach (var depId in node.Dependents)
                {
                    if (!_nodes.TryGetValue(depId, out var depNode))
                        continue;

                    depNode.UnresolvedDependencies.Remove(taskId);

                    if (depNode.UnresolvedDependencies.Count == 0
                        && depNode.Task != null
                        && !depNode.IsResolved)
                    {
                        nowReady.Add(depNode.Task);
                    }
                }

                // Sort newly ready tasks by priority level then priority (higher first)
                nowReady.Sort((a, b) =>
                {
                    var cmp = ((int)b.PriorityLevel).CompareTo((int)a.PriorityLevel);
                    return cmp != 0 ? cmp : b.Priority.CompareTo(a.Priority);
                });
            }

            foreach (var task in nowReady)
                TaskReady?.Invoke(task);
        }

        /// <summary>
        /// Checks whether adding an edge from → to would create a cycle in the DAG.
        /// Returns true if a cycle would be introduced.
        /// </summary>
        public bool DetectCycle(string fromId, string toId)
        {
            lock (_sync)
            {
                // A cycle exists if there is already a path from toId back to fromId.
                // We do a BFS from toId following Dependents edges.
                var visited = new HashSet<string>();
                var queue = new Queue<string>();
                queue.Enqueue(toId);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current == fromId)
                        return true;

                    if (!visited.Add(current))
                        continue;

                    if (_nodes.TryGetValue(current, out var node))
                    {
                        foreach (var dep in node.Dependents)
                            queue.Enqueue(dep);
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Evaluates all tracked tasks and fires <see cref="TaskReady"/> for any task
        /// whose dependencies are all resolved. Used for bulk re-evaluation (e.g. after
        /// file locks clear).
        /// </summary>
        public void ReevaluateAll()
        {
            List<AgentTask> ready;

            lock (_sync)
            {
                ready = _nodes.Values
                    .Where(n => n.Task != null
                                && n.UnresolvedDependencies.Count == 0
                                && !n.IsResolved)
                    .OrderByDescending(n => (int)n.Task!.PriorityLevel)
                    .ThenByDescending(n => n.Task!.Priority)
                    .Select(n => n.Task!)
                    .ToList();
            }

            foreach (var task in ready)
                TaskReady?.Invoke(task);
        }

        /// <summary>
        /// Removes a task from the orchestrator entirely (e.g. after it moves to history).
        /// Also cleans up any dangling references from other nodes.
        /// </summary>
        public void RemoveTask(string taskId)
        {
            lock (_sync)
            {
                if (!_nodes.TryGetValue(taskId, out var node))
                    return;

                // Remove this task from the Dependents list of its own dependencies
                foreach (var depId in node.UnresolvedDependencies)
                {
                    if (_nodes.TryGetValue(depId, out var depNode))
                        depNode.Dependents.Remove(taskId);
                }

                // Remove this task from the UnresolvedDependencies of its dependents
                foreach (var depId in node.Dependents)
                {
                    if (_nodes.TryGetValue(depId, out var depNode))
                        depNode.UnresolvedDependencies.Remove(taskId);
                }

                _nodes.Remove(taskId);
            }
        }

        /// <summary>
        /// Returns whether the orchestrator is currently tracking the given task.
        /// </summary>
        public bool ContainsTask(string taskId)
        {
            lock (_sync)
            {
                return _nodes.ContainsKey(taskId);
            }
        }

        /// <summary>
        /// Marks a node as resolved without firing events. Used when a task is resolved
        /// externally (e.g. force-started or cancelled) and shouldn't trigger cascading starts.
        /// </summary>
        public void MarkResolved(string taskId)
        {
            lock (_sync)
            {
                if (_nodes.TryGetValue(taskId, out var node))
                    node.IsResolved = true;
            }
        }

        private TaskNode GetOrCreateNode(string id)
        {
            if (!_nodes.TryGetValue(id, out var node))
            {
                node = new TaskNode { Id = id };
                _nodes[id] = node;
            }
            return node;
        }

        private class TaskNode
        {
            public string Id { get; init; } = "";
            public AgentTask? Task { get; set; }
            public bool IsResolved { get; set; }
            public HashSet<string> UnresolvedDependencies { get; } = new();
            public HashSet<string> Dependents { get; } = new();
        }
    }
}
