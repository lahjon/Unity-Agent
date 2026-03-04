using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Spritely.Constants;

namespace Spritely.Managers
{
    public class PromptBuilder : IPromptBuilder
    {
        // ── CLI Model Constants ─────────────────────────────────────

        /// <summary>Model used for exploration tasks (codebase analysis, decomposition, team design).</summary>
        public const string CliSonnetModel = AppConstants.ClaudeSonnet;

        /// <summary>Model used for planning and execution tasks.</summary>
        public const string CliOpusModel = AppConstants.ClaudeOpus;

        /// <summary>
        /// Determines the CLI model ID for a task based on its flags.
        /// Exploration-only tasks (decompose, team design) use Sonnet.
        /// Planning and execution tasks use Opus.
        /// </summary>
        public static string GetCliModelForTask(AgentTask task)
        {
            // Exploration tasks → Sonnet (fast, cost-effective for codebase analysis)
            if (task.AutoDecompose || task.SpawnTeam)
                return CliSonnetModel;

            // Feature mode initial planning = exploration (designs planning team)
            if (task.IsFeatureMode && task.FeatureModePhase == FeatureModePhase.None)
                return CliSonnetModel;

            // Planning team members: explore codebase, post findings (NoGitWrite + subtask + no ExtendedPlanning)
            if (task.NoGitWrite && task.ParentTaskId != null && !task.ExtendedPlanning && !task.PlanOnly)
                return CliSonnetModel;

            // Everything else: planning + execution → Opus
            return CliOpusModel;
        }

        /// <summary>
        /// Returns a human-friendly label for a CLI model ID (e.g. "Opus" or "Sonnet").
        /// </summary>
        public static string GetFriendlyModelName(string modelId)
        {
            if (modelId.Contains("opus", System.StringComparison.OrdinalIgnoreCase)) return "Opus";
            if (modelId.Contains("sonnet", System.StringComparison.OrdinalIgnoreCase)) return "Sonnet";
            if (modelId.Contains("haiku", System.StringComparison.OrdinalIgnoreCase)) return "Haiku";
            return modelId;
        }

        /// <summary>
        /// Determines the CLI model for a feature mode phase process.
        /// </summary>
        public static string GetCliModelForPhase(FeatureModePhase phase)
        {
            return phase switch
            {
                FeatureModePhase.None => CliSonnetModel,               // Team design = exploration
                FeatureModePhase.PlanConsolidation => CliOpusModel,     // Consolidation = planning
                FeatureModePhase.Evaluation => CliOpusModel,            // Evaluation = planning
                _ => CliOpusModel
            };
        }

        // ── Constants ────────────────────────────────────────────────
        // Prompt text is loaded from embedded Prompts/*.md files at startup.

        public static readonly string DefaultSystemPrompt = PromptLoader.Load("DefaultSystemPrompt.md");
        public static readonly string McpPromptBlock = PromptLoader.Load("McpPromptBlock.md");
        public static readonly string NoGitWriteBlock = PromptLoader.Load("NoGitWriteBlock.md");
        public static readonly string NoPushBlock = PromptLoader.Load("NoPushBlock.md");
        public static readonly string GameRulesBlock = PromptLoader.Load("GameRulesBlock.md");
        public static readonly string PlanOnlyBlock = PromptLoader.Load("PlanOnlyBlock.md");
        public static readonly string ExtendedPlanningBlock = PromptLoader.Load("ExtendedPlanningBlock.md");
        public static readonly string MessageBusBlockTemplate = PromptLoader.Load("MessageBusBlockTemplate.md");
        public static readonly string SubtaskCoordinatorBlock = PromptLoader.Load("SubtaskCoordinatorBlock.md");
        public static readonly string DecompositionPromptBlock = PromptLoader.Load("DecompositionPromptBlock.md");
        public static readonly string TeamDecompositionPromptBlock = PromptLoader.Load("TeamDecompositionPromptBlock.md");
        public static readonly string ApplyFixBlock = PromptLoader.Load("ApplyFixBlock.md");
        public static readonly string ConfirmBeforeChangesBlock = PromptLoader.Load("ConfirmBeforeChangesBlock.md");
        public static readonly string AutonomousExecutionBlock = PromptLoader.Load("AutonomousExecutionBlock.md");
        public static readonly string OutputEfficiencyBlock = PromptLoader.Load("OutputEfficiencyBlock.md");
        public static readonly string FailureRecoveryBlock = PromptLoader.Load("FailureRecoveryBlock.md");
        public static readonly string PlanningTeamMemberBlock = PromptLoader.Load("PlanningTeamMemberBlock.md");
        public static readonly string FeatureModeInitialTemplate = PromptLoader.Load("FeatureModeInitialTemplate.md");
        public static readonly string FeatureModePlanConsolidationTemplate = PromptLoader.Load("FeatureModePlanConsolidationTemplate.md");
        public static readonly string FeatureModeEvaluationTemplate = PromptLoader.Load("FeatureModeEvaluationTemplate.md");
        public static readonly string FeatureModeContinuationTemplate = PromptLoader.Load("FeatureModeContinuationTemplate.md");

        // ── Prompts formerly in other files (centralized) ────────────

        public static readonly string GameProjectExplorationPrompt = PromptLoader.Load("GameProjectExplorationPrompt.md");
        public static readonly string CodebaseExplorationPrompt = PromptLoader.Load("CodebaseExplorationPrompt.md");

        /// <summary>Template for project analysis suggestions. Use string.Format with {0} = categoryFilter.</summary>
        public static readonly string ProjectSuggestionPromptTemplate = PromptLoader.Load("ProjectSuggestionPromptTemplate.md");

        public static readonly string ChatAssistantSystemPrompt = PromptLoader.Load("ChatAssistantSystemPrompt.md");
        public static readonly string WorkflowDecompositionSystemPrompt = PromptLoader.Load("WorkflowDecompositionSystemPrompt.md");

        /// <summary>Template for result verification. Use string.Format with {0}=taskDescription, {1}=contextTail, {2}=summaryBlock.</summary>
        public static readonly string ResultVerificationPromptTemplate = PromptLoader.Load("ResultVerificationPromptTemplate.md");

        public static readonly string TokenLimitRetryContinuationPrompt = PromptLoader.Load("TokenLimitRetryContinuationPrompt.md");
        public static readonly string FeatureModeIterationPlanningTemplate = PromptLoader.Load("FeatureModeIterationPlanningTemplate.md");

        // ── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the task-specific feature mode log filename.
        /// When taskId is provided, produces ".feature_log_{taskId}.md" so
        /// multiple feature mode tasks on the same project don't collide.
        /// </summary>
        public static string GetFeatureModeLogFilename(string? taskId = null)
            => string.IsNullOrEmpty(taskId) ? ".feature_log.md" : $".feature_log_{taskId}.md";

        private static string InjectFeatureModeLogFilename(string prompt, string? taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return prompt;
            return prompt.Replace(".feature_log.md", GetFeatureModeLogFilename(taskId));
        }

        // ── Prompt Assembly ─────────────────────────────────────────

        public string BuildBasePrompt(string systemPrompt, string description, bool useMcp,
            bool isFeatureMode, bool extendedPlanning = false, bool noGitWrite = false,
            bool planOnly = false, string projectDescription = "",
            string projectRulesBlock = "",
            bool autoDecompose = false, bool spawnTeam = false,
            bool isGameProject = false, string taskId = "",
            bool applyFix = true, bool suppressOutputEfficiency = false)
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            var gameBlock = isGameProject ? GameRulesBlock : "";
            var efficiencyBlock = suppressOutputEfficiency ? "" : OutputEfficiencyBlock;

            if (isFeatureMode)
                return descBlock + projectRulesBlock + gameBlock + efficiencyBlock + FeatureModeInitialTemplate + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            var planOnlyBlock = planOnly ? PlanOnlyBlock : "";
            // Skip git blocks when planOnly is active — PlanOnlyBlock already
            // prohibits all file operations which subsumes git-write restrictions.
            // When noGitWrite is true: fully read-only git (no commit, no push, nothing).
            // When noGitWrite is false: allow commits but never push (push only via git panel).
            var gitBlock = planOnly ? "" : (noGitWrite ? NoGitWriteBlock : NoPushBlock);
            var decomposeBlock = autoDecompose ? DecompositionPromptBlock : "";
            var teamBlock = spawnTeam ? TeamDecompositionPromptBlock : "";
            var applyFixBlock = applyFix ? ApplyFixBlock : ConfirmBeforeChangesBlock;
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + gameBlock + mcpBlock + applyFixBlock + efficiencyBlock + planningBlock + planOnlyBlock + decomposeBlock + teamBlock +
                "# USER PROMPT / TASK\n" + description;
        }

        public string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false)
        {
            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                description += "\n\n# Additional Instructions\n" + task.AdditionalInstructions;

            // Planning team members and planOnly tasks produce findings/plans as their deliverable —
            // suppressing their output would lose the actual work product.
            var isPlanningMember = task.NoGitWrite && task.ParentTaskId != null && !task.ExtendedPlanning && !task.PlanOnly;
            var suppressEfficiency = isPlanningMember || task.PlanOnly;

            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsFeatureMode, task.ExtendedPlanning, task.NoGitWrite, task.PlanOnly, projectDescription, projectRulesBlock, task.AutoDecompose, task.SpawnTeam, isGameProject, task.Id, task.ApplyFix, suppressEfficiency);
            if (!string.IsNullOrWhiteSpace(task.DependencyContext))
                basePrompt = $"{basePrompt}\n\n{task.DependencyContext}";
            return BuildPromptWithImages(basePrompt, task.ImagePaths);
        }

        public string BuildPromptWithImages(string basePrompt, List<string> imagePaths)
        {
            if (imagePaths.Count == 0)
                return basePrompt;

            var sb = new StringBuilder(basePrompt);
            sb.Append("\n\n# ATTACHED IMAGES\n");
            sb.Append("View each image with the Read tool before proceeding.\n");
            foreach (var img in imagePaths)
                sb.Append($"- {img}\n");
            return sb.ToString();
        }

        public string BuildMessageBusBlock(string taskId, string projectPath,
            List<(string id, string summary)> siblings)
        {
            var safeProjectName = MessageBusManager.GetSafeProjectName(projectPath);
            var busPath = Path.Combine(MessageBusManager.AppDataBusRoot, safeProjectName);

            var block = MessageBusBlockTemplate
                .Replace("{TASK_ID}", taskId)
                .Replace("{BUS_PATH}", busPath);

            if (siblings.Count > 0)
            {
                var sb = new StringBuilder(block);
                sb.AppendLine("## Current Sibling Tasks");
                foreach (var (id, summary) in siblings)
                    sb.AppendLine($"- **{id}**: {summary}");
                sb.AppendLine();
                return sb.ToString();
            }
            return block;
        }

        // ── Command & Script Building ────────────────────────────────

        public string BuildClaudeCommand(bool skipPermissions, bool remoteSession, string? modelId = null)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            var modelFlag = !string.IsNullOrEmpty(modelId) ? $" --model {modelId}" : "";
            return $"claude -p{skipFlag}{remoteFlag}{modelFlag} --verbose --output-format stream-json";
        }

        public string BuildPowerShellScript(string projectPath, string promptFilePath,
            string claudeCmd)
        {
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | {claudeCmd}\n";
        }

        public string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath,
            bool skipPermissions, bool remoteSession, string? modelId = null)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            var modelFlag = !string.IsNullOrEmpty(modelId) ? $" --model {modelId}" : "";
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Write-Host '[Spritely] Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host '[Spritely] Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host '[Spritely] Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{remoteFlag}{modelFlag} --verbose\n" +
                   "if ($LASTEXITCODE -ne 0) { Write-Host \"`n[Spritely] Claude exited with code $LASTEXITCODE\" -ForegroundColor Yellow }\n" +
                   "Write-Host \"`n[Spritely] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
                   "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')\n";
        }

        public ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless)
        {
            if (headless)
            {
                return new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -NoExit -File \"{ps1FilePath}\"",
                    UseShellExecute = true
                };
            }

            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{ps1FilePath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        public string BuildFeatureModeContinuationPrompt(int iteration, int maxIterations, string taskId = "")
        {
            var prompt = string.Format(FeatureModeContinuationTemplate, iteration, maxIterations);
            return OutputEfficiencyBlock + InjectFeatureModeLogFilename(prompt, taskId);
        }

        public string BuildFeatureModePlanConsolidationPrompt(int iteration, int maxIterations, string teamResults, string featureDescription)
        {
            return OutputEfficiencyBlock + string.Format(FeatureModePlanConsolidationTemplate, iteration, maxIterations, teamResults, featureDescription);
        }

        public string BuildFeatureModeEvaluationPrompt(int iteration, int maxIterations, string featureDescription, string implementationResults)
        {
            return OutputEfficiencyBlock + string.Format(FeatureModeEvaluationTemplate, iteration, maxIterations, featureDescription, implementationResults);
        }

        public string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DEPENDENCY CONTEXT");
            sb.AppendLine("Completed prerequisite tasks:\n");

            var depIndex = 0;
            foreach (var depId in depIds)
            {
                var dep = activeTasks.FirstOrDefault(t => t.Id == depId)
                       ?? historyTasks.FirstOrDefault(t => t.Id == depId);
                if (dep == null) continue;

                depIndex++;
                var title = !string.IsNullOrWhiteSpace(dep.Summary) ? dep.Summary : "Untitled";
                sb.AppendLine($"## Dependency #{depIndex}: #{dep.Id} — \"{title}\"");
                sb.AppendLine($"**Task:** {dep.Description}");
                sb.AppendLine($"**Status:** {dep.Status}");

                if (!string.IsNullOrWhiteSpace(dep.CompletionSummary))
                    sb.AppendLine($"**Changes:**\n{dep.CompletionSummary}");

                sb.AppendLine();
            }

            return depIndex > 0 ? sb.ToString() : "";
        }
    }
}
