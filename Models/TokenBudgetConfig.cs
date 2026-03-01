using System.Collections.Generic;

namespace HappyEngine.Models
{
    /// <summary>
    /// Configuration for token budget management and optimization levels.
    /// </summary>
    public class TokenBudgetConfig
    {
        /// <summary>
        /// Total token budget for the entire task.
        /// </summary>
        public long TotalTokenBudget { get; set; } = 500_000;

        /// <summary>
        /// Token budgets allocated to specific phases.
        /// </summary>
        public Dictionary<FeatureModePhase, long> PhaseBudgets { get; set; } = new()
        {
            [FeatureModePhase.TeamPlanning] = 100_000,
            [FeatureModePhase.PlanConsolidation] = 50_000,
            [FeatureModePhase.Execution] = 250_000,
            [FeatureModePhase.Evaluation] = 100_000
        };

        /// <summary>
        /// The optimization level to apply.
        /// </summary>
        public OptimizationLevel Level { get; set; } = OptimizationLevel.Balanced;

        /// <summary>
        /// Progress threshold percentages for different optimization levels.
        /// </summary>
        public Dictionary<OptimizationLevel, ProgressThresholds> Thresholds { get; set; } = new()
        {
            [OptimizationLevel.None] = new ProgressThresholds
            {
                MinProgressPerIteration = 0,
                MaxStallIterations = 10,
                TokenBurnRateLimit = double.MaxValue
            },
            [OptimizationLevel.Conservative] = new ProgressThresholds
            {
                MinProgressPerIteration = 5,
                MaxStallIterations = 5,
                TokenBurnRateLimit = 100_000
            },
            [OptimizationLevel.Balanced] = new ProgressThresholds
            {
                MinProgressPerIteration = 10,
                MaxStallIterations = 3,
                TokenBurnRateLimit = 50_000
            },
            [OptimizationLevel.Aggressive] = new ProgressThresholds
            {
                MinProgressPerIteration = 15,
                MaxStallIterations = 2,
                TokenBurnRateLimit = 25_000
            }
        };

        /// <summary>
        /// Whether to enable automatic early termination.
        /// </summary>
        public bool EnableEarlyTermination { get; set; } = true;

        /// <summary>
        /// Whether to enable smart truncation.
        /// </summary>
        public bool EnableSmartTruncation { get; set; } = true;

        /// <summary>
        /// Whether to enable dynamic model selection.
        /// </summary>
        public bool EnableDynamicModelSelection { get; set; } = true;

        /// <summary>
        /// Whether to enable context deduplication.
        /// </summary>
        public bool EnableContextDeduplication { get; set; } = true;

        /// <summary>
        /// Whether to enable iteration memory.
        /// </summary>
        public bool EnableIterationMemory { get; set; } = true;

        /// <summary>
        /// Maximum tokens per child result in feature mode.
        /// </summary>
        public int MaxTokensPerChildResult { get; set; } = 2000;

        /// <summary>
        /// Maximum total tokens for all child results.
        /// </summary>
        public int MaxTotalChildResultTokens { get; set; } = 10000;

        /// <summary>
        /// Gets the current progress thresholds based on optimization level.
        /// </summary>
        public ProgressThresholds GetCurrentThresholds()
        {
            return Thresholds.TryGetValue(Level, out var threshold)
                ? threshold
                : Thresholds[OptimizationLevel.Balanced];
        }

        /// <summary>
        /// Gets the phase budget for a specific phase.
        /// </summary>
        public long GetPhaseBudget(FeatureModePhase phase)
        {
            return PhaseBudgets.TryGetValue(phase, out var budget)
                ? budget
                : TotalTokenBudget / 4; // Default to 1/4 of total
        }

        /// <summary>
        /// Creates a default configuration.
        /// </summary>
        public static TokenBudgetConfig CreateDefault()
        {
            return new TokenBudgetConfig();
        }

        /// <summary>
        /// Creates a configuration for small tasks.
        /// </summary>
        public static TokenBudgetConfig CreateSmall()
        {
            return new TokenBudgetConfig
            {
                TotalTokenBudget = 100_000,
                PhaseBudgets = new Dictionary<FeatureModePhase, long>
                {
                    [FeatureModePhase.TeamPlanning] = 20_000,
                    [FeatureModePhase.PlanConsolidation] = 10_000,
                    [FeatureModePhase.Execution] = 50_000,
                    [FeatureModePhase.Evaluation] = 20_000
                },
                Level = OptimizationLevel.Aggressive
            };
        }

        /// <summary>
        /// Creates a configuration for large tasks.
        /// </summary>
        public static TokenBudgetConfig CreateLarge()
        {
            return new TokenBudgetConfig
            {
                TotalTokenBudget = 1_000_000,
                PhaseBudgets = new Dictionary<FeatureModePhase, long>
                {
                    [FeatureModePhase.TeamPlanning] = 200_000,
                    [FeatureModePhase.PlanConsolidation] = 100_000,
                    [FeatureModePhase.Execution] = 500_000,
                    [FeatureModePhase.Evaluation] = 200_000
                },
                Level = OptimizationLevel.Conservative
            };
        }
    }

    /// <summary>
    /// Optimization levels for token usage.
    /// </summary>
    public enum OptimizationLevel
    {
        /// <summary>
        /// No optimization - use all features without restrictions.
        /// </summary>
        None,

        /// <summary>
        /// Conservative optimization - light touch to save tokens.
        /// </summary>
        Conservative,

        /// <summary>
        /// Balanced optimization - good trade-off between savings and quality.
        /// </summary>
        Balanced,

        /// <summary>
        /// Aggressive optimization - maximum token savings.
        /// </summary>
        Aggressive
    }

    /// <summary>
    /// Progress thresholds for different optimization levels.
    /// </summary>
    public class ProgressThresholds
    {
        /// <summary>
        /// Minimum progress percentage expected per iteration.
        /// </summary>
        public double MinProgressPerIteration { get; set; }

        /// <summary>
        /// Maximum consecutive iterations with stall before termination.
        /// </summary>
        public int MaxStallIterations { get; set; }

        /// <summary>
        /// Maximum tokens to spend per 10% of progress.
        /// </summary>
        public double TokenBurnRateLimit { get; set; }
    }
}