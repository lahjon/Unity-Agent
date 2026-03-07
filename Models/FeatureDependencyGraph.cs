using System.Collections.Generic;
using System.Linq;

namespace Spritely.Models
{
    /// <summary>
    /// Computed dependency graph built from all features' <see cref="FeatureEntry.DependsOn"/> lists.
    /// Not persisted — rebuilt on demand from loaded features.
    /// </summary>
    public class FeatureDependencyGraph
    {
        /// <summary>Feature ID → set of feature IDs it directly depends on.</summary>
        public Dictionary<string, HashSet<string>> DirectDependencies { get; } = new();

        /// <summary>Feature ID → set of feature IDs that directly depend on it.</summary>
        public Dictionary<string, HashSet<string>> Dependents { get; } = new();

        /// <summary>Feature ID → all transitive dependencies (computed via BFS).</summary>
        public Dictionary<string, HashSet<string>> TransitiveDependencies { get; } = new();

        /// <summary>Cycles detected in the graph. Each cycle is a list of feature IDs forming a loop.</summary>
        public List<List<string>> Cycles { get; } = new();

        /// <summary>
        /// Builds the full dependency graph from a list of feature entries.
        /// Ignores dangling references (DependsOn IDs not found in the feature list).
        /// </summary>
        public static FeatureDependencyGraph Build(List<FeatureEntry> features)
        {
            var graph = new FeatureDependencyGraph();
            var knownIds = new HashSet<string>(features.Select(f => f.Id));

            // Initialize all nodes
            foreach (var f in features)
            {
                graph.DirectDependencies[f.Id] = new HashSet<string>();
                graph.Dependents[f.Id] = new HashSet<string>();
            }

            // Populate direct edges (skip dangling references)
            foreach (var f in features)
            {
                foreach (var depId in f.DependsOn)
                {
                    if (!knownIds.Contains(depId) || depId == f.Id)
                        continue;

                    graph.DirectDependencies[f.Id].Add(depId);
                    graph.Dependents[depId].Add(f.Id);
                }
            }

            // Compute transitive dependencies via BFS per node
            foreach (var featureId in knownIds)
            {
                graph.TransitiveDependencies[featureId] = ComputeTransitive(
                    featureId, graph.DirectDependencies);
            }

            // Detect cycles using DFS with coloring
            DetectCycles(graph, knownIds);

            return graph;
        }

        /// <summary>Returns all transitive dependencies for a feature, or empty if not found.</summary>
        public HashSet<string> GetAllDependencies(string featureId)
            => TransitiveDependencies.GetValueOrDefault(featureId) ?? new HashSet<string>();

        /// <summary>Returns all features that transitively depend on this one.</summary>
        public HashSet<string> GetAllDependents(string featureId)
        {
            var result = new HashSet<string>();
            var queue = new Queue<string>();

            if (Dependents.TryGetValue(featureId, out var direct))
            {
                foreach (var d in direct)
                    queue.Enqueue(d);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!result.Add(current))
                    continue;

                if (Dependents.TryGetValue(current, out var next))
                {
                    foreach (var d in next)
                    {
                        if (!result.Contains(d))
                            queue.Enqueue(d);
                    }
                }
            }

            return result;
        }

        /// <summary>Returns whether this feature is part of any dependency cycle.</summary>
        public bool IsInCycle(string featureId)
            => Cycles.Any(c => c.Contains(featureId));

        /// <summary>
        /// Returns a topological ordering of feature IDs (dependencies before dependents).
        /// Features in cycles are placed at the end in arbitrary order.
        /// </summary>
        public List<string> TopologicalSort()
        {
            var result = new List<string>();
            var visited = new HashSet<string>();
            var inStack = new HashSet<string>();

            void Visit(string id)
            {
                if (visited.Contains(id))
                    return;
                if (inStack.Contains(id))
                    return; // cycle — skip

                inStack.Add(id);

                if (DirectDependencies.TryGetValue(id, out var deps))
                {
                    foreach (var dep in deps)
                        Visit(dep);
                }

                inStack.Remove(id);
                visited.Add(id);
                result.Add(id);
            }

            foreach (var id in DirectDependencies.Keys.OrderBy(k => k))
                Visit(id);

            return result;
        }

        private static HashSet<string> ComputeTransitive(
            string startId, Dictionary<string, HashSet<string>> directDeps)
        {
            var result = new HashSet<string>();
            var queue = new Queue<string>();

            if (directDeps.TryGetValue(startId, out var initial))
            {
                foreach (var d in initial)
                    queue.Enqueue(d);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!result.Add(current))
                    continue;

                if (directDeps.TryGetValue(current, out var next))
                {
                    foreach (var d in next)
                    {
                        if (!result.Contains(d))
                            queue.Enqueue(d);
                    }
                }
            }

            return result;
        }

        private static void DetectCycles(FeatureDependencyGraph graph, HashSet<string> allIds)
        {
            // White=0, Gray=1, Black=2
            var color = new Dictionary<string, int>();
            foreach (var id in allIds)
                color[id] = 0;

            var path = new List<string>();

            void Dfs(string id)
            {
                color[id] = 1; // Gray
                path.Add(id);

                if (graph.DirectDependencies.TryGetValue(id, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        if (color[dep] == 1)
                        {
                            // Found cycle — extract it from path
                            var cycleStart = path.IndexOf(dep);
                            if (cycleStart >= 0)
                            {
                                var cycle = path.GetRange(cycleStart, path.Count - cycleStart);
                                cycle.Add(dep); // close the loop
                                graph.Cycles.Add(cycle);
                            }
                        }
                        else if (color[dep] == 0)
                        {
                            Dfs(dep);
                        }
                    }
                }

                path.RemoveAt(path.Count - 1);
                color[id] = 2; // Black
            }

            foreach (var id in allIds.OrderBy(i => i))
            {
                if (color[id] == 0)
                    Dfs(id);
            }
        }
    }
}
