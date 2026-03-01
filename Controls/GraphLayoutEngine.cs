using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace HappyEngine.Controls
{
    internal class GraphLayoutEngine
    {
        public const double NodeWidth = 180;
        public const double NodeHeight = 72;
        public const double NodePadding = 30;
        public const double HierarchyIndent = 200;
        public const double HierarchyRowGap = 80;

        public void ComputeLayout(
            List<AgentTask> tasks,
            Dictionary<string, Point> nodePositions,
            Dictionary<string, Point> targetPositions,
            HashSet<string> userDraggedNodeIds,
            string? draggingNodeId)
        {
            var taskMap = tasks.ToDictionary(t => t.Id);
            var computed = new Dictionary<string, Point>();
            double currentY = NodePadding;

            // Phase 1: Hierarchy layout (parent tasks with their children)
            var parents = tasks
                .Where(t => t.ChildTaskIds != null && t.ChildTaskIds.Count > 0
                            && t.ChildTaskIds.Any(cid => taskMap.ContainsKey(cid)))
                .ToList();

            var hierarchyIds = new HashSet<string>();

            foreach (var parent in parents)
            {
                hierarchyIds.Add(parent.Id);
                computed[parent.Id] = new Point(NodePadding, currentY);

                double childY = currentY + HierarchyRowGap;
                foreach (var childId in parent.ChildTaskIds)
                {
                    if (!taskMap.ContainsKey(childId)) continue;
                    hierarchyIds.Add(childId);

                    if (!computed.ContainsKey(childId))
                        computed[childId] = new Point(NodePadding + HierarchyIndent, childY);

                    childY += NodeHeight + 24;
                }

                currentY = childY + 20;
            }

            // Place orphan subtasks (ParentTaskId set but parent didn't list them)
            foreach (var task in tasks)
            {
                if (!string.IsNullOrEmpty(task.ParentTaskId) && !hierarchyIds.Contains(task.Id))
                {
                    hierarchyIds.Add(task.Id);
                    if (!computed.ContainsKey(task.Id))
                    {
                        computed[task.Id] = new Point(NodePadding + HierarchyIndent, currentY);
                        currentY += NodeHeight + 24;
                    }
                }
            }

            // Phase 2: Topological layout for remaining (non-hierarchy) tasks
            var orphans = tasks.Where(t => !hierarchyIds.Contains(t.Id)).ToList();

            if (orphans.Count > 0)
            {
                var orphanPlaced = new HashSet<string>();
                var layers = new List<List<AgentTask>>();
                var remaining = new List<AgentTask>(orphans);

                while (remaining.Count > 0)
                {
                    var layer = remaining.Where(t =>
                    {
                        if (orphanPlaced.Contains(t.Id)) return false;
                        if (t.DependencyTaskIds == null || t.DependencyTaskIds.Count == 0)
                            return true;
                        return t.DependencyTaskIds.All(d =>
                            orphanPlaced.Contains(d) || !remaining.Any(r => r.Id == d));
                    }).ToList();

                    if (layer.Count == 0)
                        layer = remaining.Where(t => !orphanPlaced.Contains(t.Id)).ToList();

                    layers.Add(layer);
                    foreach (var t in layer)
                        orphanPlaced.Add(t.Id);
                    remaining = remaining.Where(t => !orphanPlaced.Contains(t.Id)).ToList();
                }

                double orphanStartY = currentY > NodePadding ? currentY + 20 : NodePadding;
                double x = NodePadding;
                for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
                {
                    var layer = layers[layerIdx];
                    double y = orphanStartY;
                    for (int i = 0; i < layer.Count; i++)
                    {
                        var t = layer[i];
                        if (!computed.ContainsKey(t.Id))
                            computed[t.Id] = new Point(x, y);
                        y += NodeHeight + 30;
                    }
                    x += NodeWidth + 80;
                }
            }

            // Apply computed positions — preserve positions for user-dragged nodes
            foreach (var kvp in computed)
            {
                if (userDraggedNodeIds.Contains(kvp.Key) && nodePositions.ContainsKey(kvp.Key))
                {
                    targetPositions[kvp.Key] = nodePositions[kvp.Key];
                }
                else
                {
                    targetPositions[kvp.Key] = kvp.Value;
                    nodePositions[kvp.Key] = kvp.Value;
                }
            }

            // During active drag, keep dragged node at its drag position
            if (draggingNodeId != null && nodePositions.ContainsKey(draggingNodeId))
            {
                targetPositions[draggingNodeId] = nodePositions[draggingNodeId];
            }
        }

        public void CleanupRemovedTasks(
            HashSet<string> currentTaskIds,
            Dictionary<string, Point> nodePositions,
            Dictionary<string, Point> targetPositions,
            HashSet<string> userDraggedNodeIds)
        {
            foreach (var key in nodePositions.Keys.ToList())
                if (!currentTaskIds.Contains(key))
                    nodePositions.Remove(key);

            foreach (var key in targetPositions.Keys.ToList())
                if (!currentTaskIds.Contains(key))
                    targetPositions.Remove(key);

            userDraggedNodeIds.IntersectWith(currentTaskIds);
        }

        public static bool WouldCreateCycle(IEnumerable<AgentTask> tasks, string sourceId, string targetId)
        {
            if (sourceId == targetId) return true;

            var taskMap = tasks.ToDictionary(t => t.Id);
            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(sourceId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == targetId) return true;
                if (!visited.Add(current)) continue;

                if (taskMap.TryGetValue(current, out var task) && task.DependencyTaskIds != null)
                {
                    foreach (var depId in task.DependencyTaskIds)
                        queue.Enqueue(depId);
                }
            }

            return false;
        }
    }
}
