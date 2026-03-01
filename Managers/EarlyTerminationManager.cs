using System;
using System.Collections.Generic;
using System.Linq;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages early termination decisions for tasks based on progress analysis and token budgets.
    /// </summary>
    public class EarlyTerminationManager
    {
        private readonly ProgressAnalyzer _progressAnalyzer;
        private readonly TokenBudgetManager _tokenBudgetManager;
        private readonly Dictionary<string, TerminationState> _taskStates = new();
        private readonly object _lock = new();

        // Termination thresholds
        private const double MinAcceptableVelocity = 5.0; // % progress per iteration
        private const int MaxStallIterations = 3;
        private const double MaxTokenBudgetOverrun = 1.5; // 150% of budget
        private const double CriticalStallConfidence = 0.8;

        public EarlyTerminationManager(
            ProgressAnalyzer progressAnalyzer,
            TokenBudgetManager? tokenBudgetManager = null)
        {
            _progressAnalyzer = progressAnalyzer;
            _tokenBudgetManager = tokenBudgetManager ?? new TokenBudgetManager();
        }

        /// <summary>
        /// Evaluates whether a task should be terminated early.
        /// </summary>
        public TerminationDecision EvaluateTermination(
            AgentTask task,
            string iterationOutput,
            TokenBudgetConfig? budgetConfig = null)
        {
            lock (_lock)
            {
                // Initialize or update task state
                if (!_taskStates.TryGetValue(task.Id, out var state))
                {
                    state = new TerminationState { TaskId = task.Id };
                    _taskStates[task.Id] = state;
                }

                // Extract and record progress metrics
                var metrics = _progressAnalyzer.ExtractMetricsFromOutput(
                    task.Id, iterationOutput, task.CurrentIteration);
                metrics.TokensUsed = task.TotalAllTokens;
                metrics.EstimatedCost = EstimateCost(task);
                _progressAnalyzer.RecordProgress(task.Id, metrics);

                // Update budget tracking
                if (budgetConfig != null)
                {
                    _tokenBudgetManager.UpdateTaskUsage(task.Id, task.TotalAllTokens);
                }

                // Perform various checks
                var checks = new List<TerminationCheck>();

                CheckStallConditions(task.Id, checks);
                CheckVelocity(task.Id, checks);
                CheckTokenBudget(task.Id, budgetConfig, checks);
                CheckIterationLimit(task, checks);
                CheckErrorPatterns(iterationOutput, state, checks);

                // Make termination decision
                var decision = MakeDecision(task, checks, state);

                // Update state
                state.LastEvaluation = DateTime.Now;
                state.ConsecutiveStallIterations = decision.ShouldTerminate ? 0 :
                    checks.Any(c => c.Type == TerminationCheckType.Stalled) ?
                    state.ConsecutiveStallIterations + 1 : 0;

                return decision;
            }
        }

        private void CheckStallConditions(string taskId, List<TerminationCheck> checks)
        {
            var stallResult = _progressAnalyzer.DetectStall(taskId);
            if (stallResult.IsStalled)
            {
                checks.Add(new TerminationCheck
                {
                    Type = TerminationCheckType.Stalled,
                    Severity = stallResult.ConfidenceScore >= CriticalStallConfidence
                        ? CheckSeverity.Critical
                        : CheckSeverity.Warning,
                    Description = stallResult.Recommendation,
                    Evidence = stallResult.Conditions.Select(c =>
                        $"{c.Type}: {c.Description} (confidence: {c.Confidence:P0})").ToList()
                });
            }
        }

        private void CheckVelocity(string taskId, List<TerminationCheck> checks)
        {
            var velocity = _progressAnalyzer.CalculateVelocity(taskId);
            if (velocity.HasSufficientData)
            {
                if (velocity.AverageVelocity < MinAcceptableVelocity)
                {
                    checks.Add(new TerminationCheck
                    {
                        Type = TerminationCheckType.LowVelocity,
                        Severity = velocity.VelocityTrend == VelocityTrend.Decelerating
                            ? CheckSeverity.Critical
                            : CheckSeverity.Warning,
                        Description = $"Progress velocity below threshold ({velocity.AverageVelocity:F1}% per iteration)",
                        Evidence = new List<string>
                        {
                            $"Trend: {velocity.VelocityTrend}",
                            $"Estimated iterations remaining: {velocity.EstimatedIterationsRemaining}"
                        }
                    });
                }
            }
        }

        private void CheckTokenBudget(string taskId, TokenBudgetConfig? config, List<TerminationCheck> checks)
        {
            if (config == null) return;

            var usage = _tokenBudgetManager.GetTaskUsage(taskId);
            var budgetUsed = usage.TotalTokens / (double)config.TotalTokenBudget;

            if (budgetUsed > MaxTokenBudgetOverrun)
            {
                checks.Add(new TerminationCheck
                {
                    Type = TerminationCheckType.BudgetExceeded,
                    Severity = CheckSeverity.Critical,
                    Description = $"Token budget exceeded ({budgetUsed:P0} of allocated budget)",
                    Evidence = new List<string>
                    {
                        $"Used: {usage.TotalTokens:N0} tokens",
                        $"Budget: {config.TotalTokenBudget:N0} tokens",
                        $"Estimated cost: ${usage.EstimatedCost:F2}"
                    }
                });
            }
            else if (budgetUsed > 0.8)
            {
                checks.Add(new TerminationCheck
                {
                    Type = TerminationCheckType.BudgetWarning,
                    Severity = CheckSeverity.Warning,
                    Description = $"Approaching token budget limit ({budgetUsed:P0} used)",
                    Evidence = new List<string> { $"Remaining: {config.TotalTokenBudget - usage.TotalTokens:N0} tokens" }
                });
            }
        }

        private void CheckIterationLimit(AgentTask task, List<TerminationCheck> checks)
        {
            if (task.CurrentIteration >= task.MaxIterations)
            {
                checks.Add(new TerminationCheck
                {
                    Type = TerminationCheckType.IterationLimit,
                    Severity = CheckSeverity.Info,
                    Description = "Maximum iteration limit reached",
                    Evidence = new List<string> { $"Current: {task.CurrentIteration}, Max: {task.MaxIterations}" }
                });
            }
        }

        private void CheckErrorPatterns(string output, TerminationState state, List<TerminationCheck> checks)
        {
            // Check for catastrophic errors
            var catastrophicPatterns = new[]
            {
                "out of memory",
                "stack overflow",
                "maximum call stack",
                "fatal error",
                "unrecoverable error"
            };

            foreach (var pattern in catastrophicPatterns)
            {
                if (output.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(new TerminationCheck
                    {
                        Type = TerminationCheckType.CatastrophicError,
                        Severity = CheckSeverity.Critical,
                        Description = $"Catastrophic error detected: {pattern}",
                        Evidence = new List<string> { "Task cannot continue with this error" }
                    });
                    break;
                }
            }

            // Track error frequency
            var errorCount = System.Text.RegularExpressions.Regex.Matches(
                output, @"(?:error|exception|failed)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

            state.RecentErrorCounts.Add(errorCount);
            if (state.RecentErrorCounts.Count > 5)
                state.RecentErrorCounts.RemoveAt(0);

            // Check if errors are increasing
            if (state.RecentErrorCounts.Count >= 3)
            {
                var trend = state.RecentErrorCounts.TakeLast(3).ToList();
                if (trend[0] < trend[1] && trend[1] < trend[2])
                {
                    checks.Add(new TerminationCheck
                    {
                        Type = TerminationCheckType.IncreasingErrors,
                        Severity = CheckSeverity.Warning,
                        Description = "Error rate is increasing",
                        Evidence = new List<string> { $"Error counts: {string.Join(" -> ", trend)}" }
                    });
                }
            }
        }

        private TerminationDecision MakeDecision(
            AgentTask task,
            List<TerminationCheck> checks,
            TerminationState state)
        {
            var decision = new TerminationDecision
            {
                TaskId = task.Id,
                Iteration = task.CurrentIteration,
                Checks = checks,
                Timestamp = DateTime.Now
            };

            // Critical checks = immediate termination
            var criticalChecks = checks.Where(c => c.Severity == CheckSeverity.Critical).ToList();
            if (criticalChecks.Any())
            {
                decision.ShouldTerminate = true;
                decision.Reason = TerminationReason.CriticalFailure;
                decision.Explanation = $"Critical issues detected: {string.Join(", ", criticalChecks.Select(c => c.Type))}";
                decision.Confidence = 0.95;
                return decision;
            }

            // Multiple warnings = consider termination
            var warnings = checks.Where(c => c.Severity == CheckSeverity.Warning).ToList();
            if (warnings.Count >= 2)
            {
                // Check if stalled for too long
                if (state.ConsecutiveStallIterations >= MaxStallIterations)
                {
                    decision.ShouldTerminate = true;
                    decision.Reason = TerminationReason.PersistentStall;
                    decision.Explanation = $"Task stalled for {state.ConsecutiveStallIterations} consecutive iterations";
                    decision.Confidence = 0.85;
                    return decision;
                }

                // Otherwise warn but continue
                decision.ShouldTerminate = false;
                decision.Reason = TerminationReason.None;
                decision.Explanation = $"Multiple warnings detected, monitoring closely";
                decision.Confidence = 0.6;
                decision.RecommendedAction = "Consider manual review";
                return decision;
            }

            // No significant issues
            decision.ShouldTerminate = false;
            decision.Reason = TerminationReason.None;
            decision.Explanation = "Task progressing normally";
            decision.Confidence = 0.9;
            return decision;
        }

        private double EstimateCost(AgentTask task)
        {
            // Rough cost estimation based on token usage
            // Adjust these rates based on actual pricing
            const double InputTokenRate = 0.003; // per 1K tokens
            const double OutputTokenRate = 0.015; // per 1K tokens

            return (task.InputTokens / 1000.0 * InputTokenRate) +
                   (task.OutputTokens / 1000.0 * OutputTokenRate);
        }

        /// <summary>
        /// Clears termination state for a task.
        /// </summary>
        public void ClearTaskState(string taskId)
        {
            lock (_lock)
            {
                _taskStates.Remove(taskId);
                _progressAnalyzer.ClearTaskHistory(taskId);
                _tokenBudgetManager.ClearTaskUsage(taskId);
            }
        }

        /// <summary>
        /// Gets termination statistics for monitoring.
        /// </summary>
        public TerminationStatistics GetStatistics()
        {
            lock (_lock)
            {
                return new TerminationStatistics
                {
                    ActiveTasks = _taskStates.Count,
                    StalledTasks = _taskStates.Count(kvp => kvp.Value.ConsecutiveStallIterations > 0),
                    TasksNearBudgetLimit = _tokenBudgetManager.GetTasksNearLimit(0.8).Count,
                    TotalTokensConsumed = _tokenBudgetManager.GetTotalTokensUsed(),
                    TotalEstimatedCost = _tokenBudgetManager.GetTotalEstimatedCost()
                };
            }
        }
    }

    // Supporting classes
    internal class TerminationState
    {
        public string TaskId { get; set; } = "";
        public DateTime LastEvaluation { get; set; }
        public int ConsecutiveStallIterations { get; set; }
        public List<int> RecentErrorCounts { get; set; } = new();
    }

    public class TerminationCheck
    {
        public TerminationCheckType Type { get; set; }
        public CheckSeverity Severity { get; set; }
        public string Description { get; set; } = "";
        public List<string> Evidence { get; set; } = new();
    }

    public enum TerminationCheckType
    {
        Stalled,
        LowVelocity,
        BudgetExceeded,
        BudgetWarning,
        IterationLimit,
        CatastrophicError,
        IncreasingErrors
    }

    public enum CheckSeverity
    {
        Info,
        Warning,
        Critical
    }

    public class TerminationDecision
    {
        public string TaskId { get; set; } = "";
        public int Iteration { get; set; }
        public bool ShouldTerminate { get; set; }
        public TerminationReason Reason { get; set; }
        public string Explanation { get; set; } = "";
        public double Confidence { get; set; }
        public string? RecommendedAction { get; set; }
        public List<TerminationCheck> Checks { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    public enum TerminationReason
    {
        None,
        CriticalFailure,
        PersistentStall,
        BudgetExceeded,
        UserRequested
    }

    public class TerminationStatistics
    {
        public int ActiveTasks { get; set; }
        public int StalledTasks { get; set; }
        public int TasksNearBudgetLimit { get; set; }
        public long TotalTokensConsumed { get; set; }
        public double TotalEstimatedCost { get; set; }
    }

    /// <summary>
    /// Manages token budgets for tasks.
    /// </summary>
    public class TokenBudgetManager
    {
        private readonly Dictionary<string, TokenUsage> _taskUsage = new();
        private readonly object _lock = new();

        public void UpdateTaskUsage(string taskId, long totalTokens)
        {
            lock (_lock)
            {
                if (!_taskUsage.TryGetValue(taskId, out var usage))
                {
                    usage = new TokenUsage { TaskId = taskId };
                    _taskUsage[taskId] = usage;
                }

                usage.TotalTokens = totalTokens;
                usage.LastUpdated = DateTime.Now;
                usage.EstimatedCost = EstimateTokenCost(totalTokens);
            }
        }

        public TokenUsage GetTaskUsage(string taskId)
        {
            lock (_lock)
            {
                return _taskUsage.TryGetValue(taskId, out var usage)
                    ? usage
                    : new TokenUsage { TaskId = taskId };
            }
        }

        public List<string> GetTasksNearLimit(double threshold)
        {
            lock (_lock)
            {
                // This would need actual budget limits per task
                // For now, return empty list
                return new List<string>();
            }
        }

        public long GetTotalTokensUsed()
        {
            lock (_lock)
            {
                return _taskUsage.Values.Sum(u => u.TotalTokens);
            }
        }

        public double GetTotalEstimatedCost()
        {
            lock (_lock)
            {
                return _taskUsage.Values.Sum(u => u.EstimatedCost);
            }
        }

        public void ClearTaskUsage(string taskId)
        {
            lock (_lock)
            {
                _taskUsage.Remove(taskId);
            }
        }

        private double EstimateTokenCost(long tokens)
        {
            const double TokenRate = 0.01; // per 1K tokens average
            return tokens / 1000.0 * TokenRate;
        }
    }

    public class TokenUsage
    {
        public string TaskId { get; set; } = "";
        public long TotalTokens { get; set; }
        public double EstimatedCost { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}