using System;
using System.Windows.Threading;
using Spritely.Helpers;
using Spritely.Models;
using Spritely.Services;

namespace Spritely.Managers
{
    /// <summary>
    /// Groups dependencies for <see cref="TaskExecutionManager"/> to reduce constructor parameter count.
    /// </summary>
    public class TaskExecutionServices
    {
        // ── Core infrastructure ──────────────────────────────────────
        public required string ScriptDir { get; init; }
        public required Dispatcher Dispatcher { get; init; }

        // ── Manager dependencies ─────────────────────────────────────
        public required FileLockManager FileLockManager { get; init; }
        public required OutputTabManager OutputTabManager { get; init; }
        public required MessageBusManager MessageBusManager { get; init; }
        public required GitOperationGuard GitOperationGuard { get; init; }

        // ── Service interfaces ───────────────────────────────────────
        public required IGitHelper GitHelper { get; init; }
        public required ICompletionAnalyzer CompletionAnalyzer { get; init; }
        public required IPromptBuilder PromptBuilder { get; init; }
        public required ITaskFactory TaskFactory { get; init; }

        // ── Prompt / project callbacks ───────────────────────────────
        public required Func<string> GetSystemPrompt { get; init; }
        public required Func<AgentTask, string> GetProjectDescription { get; init; }
        public required Func<string, string> GetProjectRulesBlock { get; init; }
        public required Func<string, bool> IsGameProject { get; init; }

        // ── Optional dependencies ────────────────────────────────────
        public TaskPreprocessor? TaskPreprocessor { get; init; }
        public Func<int>? GetTokenLimitRetryMinutes { get; init; }
        public Func<bool>? GetAutoVerify { get; init; }
        public Func<string>? GetSkillsBlock { get; init; }
        public Func<string>? GetOpusEffortLevel { get; init; }
        public FeatureRegistryManager? FeatureRegistryManager { get; init; }
        public FeatureContextResolver? FeatureContextResolver { get; init; }
        public FeatureUpdateAgent? FeatureUpdateAgent { get; init; }
        public HybridSearchManager? HybridSearchManager { get; init; }
    }
}
