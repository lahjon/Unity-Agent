using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HappyEngine.Managers;

namespace HappyEngine
{
    /// <summary>
    /// Thin static facade that delegates to focused injectable services:
    /// <see cref="Managers.TaskFactory"/>, <see cref="Managers.PromptBuilder"/>,
    /// <see cref="Managers.CompletionAnalyzer"/>, and <see cref="Managers.GitHelper"/>.
    /// Preserves backward compatibility for all existing callers and tests.
    /// </summary>
    public static class TaskLauncher
    {
        // ── Service Singletons ──────────────────────────────────────

        private static readonly GitHelper _git = new();
        private static readonly CompletionAnalyzer _completion = new(_git);
        private static readonly Managers.PromptBuilder _prompt = new();
        private static readonly Managers.TaskFactory _factory = new();

        // ── Re-exported Constants ───────────────────────────────────

        public const string DefaultSystemPrompt = Managers.PromptBuilder.DefaultSystemPrompt;
        public const string McpPromptBlock = Managers.PromptBuilder.McpPromptBlock;
        public const string NoGitWriteBlock = Managers.PromptBuilder.NoGitWriteBlock;
        public const string PlanOnlyBlock = Managers.PromptBuilder.PlanOnlyBlock;
        public const string ExtendedPlanningBlock = Managers.PromptBuilder.ExtendedPlanningBlock;
        public const string MessageBusBlockTemplate = Managers.PromptBuilder.MessageBusBlockTemplate;
        public const string SubtaskCoordinatorBlock = Managers.PromptBuilder.SubtaskCoordinatorBlock;
        public const string DecompositionPromptBlock = Managers.PromptBuilder.DecompositionPromptBlock;
        public const string TeamDecompositionPromptBlock = Managers.PromptBuilder.TeamDecompositionPromptBlock;
        public const string OvernightInitialTemplate = Managers.PromptBuilder.OvernightInitialTemplate;
        public const string OvernightContinuationTemplate = Managers.PromptBuilder.OvernightContinuationTemplate;

        // ── TaskFactory Delegates ───────────────────────────────────

        public static bool ValidateTaskInput(string? description)
            => _factory.ValidateTaskInput(description);

        public static AgentTask CreateTask(
            string description,
            string projectPath,
            bool skipPermissions,
            bool remoteSession,
            bool headless,
            bool isOvernight,
            bool ignoreFileLocks,
            bool useMcp,
            bool spawnTeam = false,
            bool extendedPlanning = false,
            bool noGitWrite = false,
            bool planOnly = false,
            bool useMessageBus = false,
            List<string>? imagePaths = null,
            ModelType model = ModelType.ClaudeCode,
            string? parentTaskId = null,
            bool autoDecompose = false)
            => _factory.CreateTask(description, projectPath, skipPermissions, remoteSession,
                headless, isOvernight, ignoreFileLocks, useMcp, spawnTeam, extendedPlanning,
                noGitWrite, planOnly, useMessageBus, imagePaths, model, parentTaskId, autoDecompose);

        public static void PrepareTaskForOvernightStart(AgentTask task)
            => _factory.PrepareTaskForOvernightStart(task);

        public static string GenerateLocalSummary(string description)
            => _factory.GenerateLocalSummary(description);

        public static Task<(string Short, string Long)> GenerateProjectDescriptionAsync(
            string projectPath, CancellationToken cancellationToken = default)
            => _factory.GenerateProjectDescriptionAsync(projectPath, cancellationToken);

        // ── PromptBuilder Delegates ─────────────────────────────────

        public static string BuildBasePrompt(string systemPrompt, string description,
            bool useMcp, bool isOvernight, bool extendedPlanning = false,
            bool noGitWrite = false, bool planOnly = false,
            string projectDescription = "", string projectRulesBlock = "",
            bool autoDecompose = false,
            bool spawnTeam = false,
            bool isGameProject = false,
            string taskId = "")
            => _prompt.BuildBasePrompt(systemPrompt, description, useMcp, isOvernight,
                extendedPlanning, noGitWrite, planOnly, projectDescription,
                projectRulesBlock, autoDecompose, spawnTeam, isGameProject, taskId);

        public static string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false)
            => _prompt.BuildFullPrompt(systemPrompt, task, projectDescription,
                projectRulesBlock, isGameProject);

        public static string BuildPromptWithImages(string basePrompt, List<string> imagePaths)
            => _prompt.BuildPromptWithImages(basePrompt, imagePaths);

        public static string BuildMessageBusBlock(string taskId,
            List<(string id, string summary)> siblings)
            => _prompt.BuildMessageBusBlock(taskId, siblings);

        public static string BuildClaudeCommand(bool skipPermissions, bool remoteSession)
            => _prompt.BuildClaudeCommand(skipPermissions, remoteSession);

        public static string BuildPowerShellScript(string projectPath,
            string promptFilePath, string claudeCmd)
            => _prompt.BuildPowerShellScript(projectPath, promptFilePath, claudeCmd);

        public static string BuildHeadlessPowerShellScript(string projectPath,
            string promptFilePath, bool skipPermissions, bool remoteSession)
            => _prompt.BuildHeadlessPowerShellScript(projectPath, promptFilePath,
                skipPermissions, remoteSession);

        public static ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless)
            => _prompt.BuildProcessStartInfo(ps1FilePath, headless);

        public static string BuildOvernightContinuationPrompt(int iteration, int maxIterations, string taskId = "")
            => _prompt.BuildOvernightContinuationPrompt(iteration, maxIterations, taskId);

        public static string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks)
            => _prompt.BuildDependencyContext(depIds, activeTasks, historyTasks);

        // ── CompletionAnalyzer Delegates ────────────────────────────

        internal static bool IsTaskOutputComplete(string[] lines, int recommendationLine)
            => _completion.IsTaskOutputComplete(lines, recommendationLine);

        public static string? ExtractRecommendations(string output)
            => _completion.ExtractRecommendations(output);

        public static Task<ResultVerification?> VerifyResultAsync(
            string outputTail, string taskDescription, string? completionSummary,
            CancellationToken ct = default)
            => _completion.VerifyResultAsync(outputTail, taskDescription, completionSummary, ct);

        public static string FormatCompletionSummary(AgentTaskStatus status,
            TimeSpan duration, List<(string name, int added, int removed)>? fileChanges)
            => _completion.FormatCompletionSummary(status, duration, fileChanges);

        public static string GenerateCompletionSummary(string projectPath,
            string? gitStartHash, AgentTaskStatus status, TimeSpan duration)
            => _completion.GenerateCompletionSummary(projectPath, gitStartHash, status, duration);

        public static Task<string> GenerateCompletionSummaryAsync(string projectPath,
            string? gitStartHash, AgentTaskStatus status, TimeSpan duration,
            CancellationToken cancellationToken = default)
            => _completion.GenerateCompletionSummaryAsync(projectPath, gitStartHash,
                status, duration, cancellationToken);

        public static bool CheckOvernightComplete(string output)
            => _completion.CheckOvernightComplete(output);

        public static bool IsTokenLimitError(string output)
            => _completion.IsTokenLimitError(output);

        // ── GitHelper Delegates ─────────────────────────────────────

        public static Task<string?> RunGitCommandAsync(string workingDirectory,
            string arguments, CancellationToken cancellationToken = default)
            => _git.RunGitCommandAsync(workingDirectory, arguments, cancellationToken);

        public static Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default)
            => _git.CaptureGitHeadAsync(projectPath, cancellationToken);

        public static string? CaptureGitHead(string projectPath)
            => _git.CaptureGitHead(projectPath);

        public static List<(string name, int added, int removed)>? GetGitFileChanges(
            string projectPath, string? gitStartHash)
            => _git.GetGitFileChanges(projectPath, gitStartHash);

        public static Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(
            string projectPath, string? gitStartHash,
            CancellationToken cancellationToken = default)
            => _git.GetGitFileChangesAsync(projectPath, gitStartHash, cancellationToken);

        // ── Utilities (remain on facade) ────────────────────────────

        public static string StripAnsi(string text) => Helpers.FormatHelpers.StripAnsiCodes(text);

        public static string? ExtractExecutionPrompt(string output)
        {
            var match = Regex.Match(output, @"```EXECUTION_PROMPT\s*\n(.*?)```", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        public static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        }

        public static bool IsFileModifyTool(string? toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            return toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizePath(string path, string? basePath = null)
        {
            path = path.Replace('/', '\\');
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(basePath))
                path = Path.Combine(basePath, path);
            try { path = Path.GetFullPath(path); } catch (Exception ex) { Managers.AppLogger.Debug("TaskLauncher", $"Path normalization failed for '{path}'", ex); }
            return path.ToLowerInvariant();
        }
    }
}
