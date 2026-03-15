using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Spritely.Constants;
using Spritely.Models;

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
            if (task.IsTeamsMode && task.TeamsModePhase == TeamsModePhase.None)
                return CliSonnetModel;

            // Planning team members: explore codebase, post findings (subtask + no ExtendedPlanning)
            if (task.ParentTaskId != null && !task.ExtendedPlanning && !task.PlanOnly)
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
        /// Determines the CLI model for a teams mode phase process.
        /// </summary>
        public static string GetCliModelForPhase(TeamsModePhase phase)
        {
            return phase switch
            {
                TeamsModePhase.None => CliSonnetModel,               // Team design = exploration
                TeamsModePhase.PlanConsolidation => CliOpusModel,     // Consolidation = planning
                TeamsModePhase.Evaluation => CliOpusModel,            // Evaluation = planning
                _ => CliOpusModel
            };
        }

        // ── Constants ────────────────────────────────────────────────
        // Prompt text is loaded from embedded Prompts/*.md files at startup.

        // ── Core prompts ──
        public static readonly string DefaultSystemPrompt = PromptLoader.Load("Core/DefaultSystemPrompt.md");
        public static readonly string McpPromptBlock = PromptLoader.Load("Core/McpPromptBlock.md");
        public static readonly string NoGitWriteBlock = PromptLoader.Load("Core/NoGitWriteBlock.md");
        public static readonly string NoPushBlock = PromptLoader.Load("Core/NoPushBlock.md");
        public static readonly string GameRulesBlock = PromptLoader.Load("Core/GameRulesBlock.md");
        public static readonly string PlanOnlyBlock = PromptLoader.Load("Core/PlanOnlyBlock.md");
        public static readonly string ExtendedPlanningBlock = PromptLoader.Load("Core/ExtendedPlanningBlock.md");
        public static readonly string ApplyFixBlock = PromptLoader.Load("Core/ApplyFixBlock.md");
        public static readonly string ConfirmBeforeChangesBlock = PromptLoader.Load("Core/ConfirmBeforeChangesBlock.md");
        public static readonly string AutonomousExecutionBlock = PromptLoader.Load("Core/AutonomousExecutionBlock.md");
        public static readonly string OutputEfficiencyBlock = PromptLoader.Load("Core/OutputEfficiencyBlock.md");

        // ── Teams prompts ──
        public static readonly string MessageBusBlockTemplate = PromptLoader.Load("Teams/MessageBusBlockTemplate.md");
        public static readonly string SubtaskCoordinatorBlock = PromptLoader.Load("Teams/SubtaskCoordinatorBlock.md");
        public static readonly string DecompositionPromptBlock = PromptLoader.Load("Teams/DecompositionPromptBlock.md");
        public static readonly string TeamDecompositionPromptBlock = PromptLoader.Load("Teams/TeamDecompositionPromptBlock.md");
        public static readonly string PlanningTeamMemberBlock = PromptLoader.Load("Teams/PlanningTeamMemberBlock.md");
        public static readonly string TeamsModeInitialTemplate = PromptLoader.Load("Teams/TeamsModeInitialTemplate.md");
        public static readonly string TeamsModePlanConsolidationTemplate = PromptLoader.Load("Teams/TeamsModePlanConsolidationTemplate.md");
        public static readonly string TeamsModeEvaluationTemplate = PromptLoader.Load("Teams/TeamsModeEvaluationTemplate.md");
        public static readonly string TeamsModeContinuationTemplate = PromptLoader.Load("Teams/TeamsModeContinuationTemplate.md");

        // ── Prompts formerly in other files (centralized) ────────────

        // ── Task prompts ──
        public static readonly string GameProjectExplorationPrompt = PromptLoader.Load("Tasks/GameProjectExplorationPrompt.md");
        public static readonly string CodebaseExplorationPrompt = PromptLoader.Load("Tasks/CodebaseExplorationPrompt.md");
        public static readonly string ClaudeMdGenerationPrompt = PromptLoader.Load("Tasks/ClaudeMdGenerationPrompt.md");

        /// <summary>Template for project analysis suggestions. Use string.Format with {0} = categoryFilter.</summary>
        public static readonly string ProjectSuggestionPromptTemplate = PromptLoader.Load("Tasks/ProjectSuggestionPromptTemplate.md");

        public static readonly string ChatAssistantSystemPrompt = PromptLoader.Load("Core/ChatAssistantSystemPrompt.md");
        public static readonly string WorkflowDecompositionSystemPrompt = PromptLoader.Load("Teams/WorkflowDecompositionSystemPrompt.md");

        /// <summary>Template for result verification. Use string.Format with {0}=taskDescription, {1}=contextTail, {2}=summaryBlock.</summary>
        public static readonly string ResultVerificationPromptTemplate = PromptLoader.Load("Tasks/ResultVerificationPromptTemplate.md");

        /// <summary>Template for semantic commit messages. Use string.Format with {0}=taskDescription, {1}=numstat, {2}=patch.</summary>
        public static readonly string SemanticCommitMessagePromptTemplate = PromptLoader.Load("Tasks/SemanticCommitMessagePrompt.md");

        public static readonly string TokenLimitRetryContinuationPrompt = PromptLoader.Load("Core/TokenLimitRetryContinuationPrompt.md");
        public static readonly string TeamsModeIterationPlanningTemplate = PromptLoader.Load("Teams/TeamsModeIterationPlanningTemplate.md");

        // Synthesis perspective prompts
        public static readonly string SynthesisArchitecturePerspective = PromptLoader.Load("Teams/SynthesisArchitecturePerspective.md");
        public static readonly string SynthesisTestingPerspective = PromptLoader.Load("Teams/SynthesisTestingPerspective.md");
        public static readonly string SynthesisEdgeCasesPerspective = PromptLoader.Load("Teams/SynthesisEdgeCasesPerspective.md");

        /// <summary>Perspective prompt blocks indexed by perspective (0=Architecture, 1=Testing, 2=EdgeCases).</summary>
        public static readonly string[] SynthesisPerspectives =
        {
            SynthesisArchitecturePerspective,
            SynthesisTestingPerspective,
            SynthesisEdgeCasesPerspective
        };

        public static readonly string[] SynthesisPerspectiveNames = { "Architecture", "Testing", "Edge Cases" };

        // ── Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the task-specific teams mode log filename.
        /// When taskId is provided, produces ".teams_log_{taskId}.md" so
        /// multiple teams mode tasks on the same project don't collide.
        /// </summary>
        public static string GetTeamsModeLogFilename(string? taskId = null)
            => string.IsNullOrEmpty(taskId) ? ".teams_log.md" : $".teams_log_{taskId}.md";

        private static string InjectFeatureModeLogFilename(string prompt, string? taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return prompt;
            return prompt.Replace(".teams_log.md", GetTeamsModeLogFilename(taskId));
        }

        // ── Prompt Assembly ─────────────────────────────────────────

        public string BuildBasePrompt(string systemPrompt, string description, bool useMcp,
            bool isTeamsMode, bool extendedPlanning = false,
            bool planOnly = false, string projectDescription = "",
            string projectRulesBlock = "",
            bool autoDecompose = false, bool spawnTeam = false,
            bool isGameProject = false, string taskId = "",
            bool applyFix = true, bool suppressOutputEfficiency = false,
            string skillsBlock = "", string featureContextBlock = "",
            string crossProjectHintsBlock = "")
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            var featureBlock = !string.IsNullOrWhiteSpace(featureContextBlock) ? featureContextBlock + "\n" : "";
            var crossBlock = !string.IsNullOrWhiteSpace(crossProjectHintsBlock) ? crossProjectHintsBlock + "\n" : "";
            var gameBlock = isGameProject ? GameRulesBlock : "";
            var efficiencyBlock = suppressOutputEfficiency ? "" : OutputEfficiencyBlock;

            if (isTeamsMode)
                return descBlock + projectRulesBlock + featureBlock + crossBlock + skillsBlock + gameBlock + efficiencyBlock + TeamsModeInitialTemplate + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            var planOnlyBlock = planOnly ? PlanOnlyBlock : "";
            // Skip git blocks when planOnly is active — PlanOnlyBlock already
            // prohibits all file operations which subsumes git-write restrictions.
            var gitBlock = planOnly ? "" : NoGitWriteBlock;
            var decomposeBlock = autoDecompose ? DecompositionPromptBlock : "";
            var teamBlock = spawnTeam ? TeamDecompositionPromptBlock : "";
            var applyFixBlock = applyFix ? ApplyFixBlock : ConfirmBeforeChangesBlock;
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + featureBlock + crossBlock + skillsBlock + gameBlock + mcpBlock + applyFixBlock + efficiencyBlock + planningBlock + planOnlyBlock + decomposeBlock + teamBlock +
                "# USER PROMPT / TASK\n" + description;
        }

        public string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false, string skillsBlock = "",
            string featureContextBlock = "", string pendingChangesBlock = "",
            PromptVariant? evolutionVariant = null,
            string crossProjectHintsBlock = "")
        {
            // Apply prompt evolution variant if one is active for this task
            if (evolutionVariant != null && !string.IsNullOrWhiteSpace(evolutionVariant.MutatedText))
            {
                if (systemPrompt.Contains(evolutionVariant.OriginalText))
                    systemPrompt = systemPrompt.Replace(evolutionVariant.OriginalText, evolutionVariant.MutatedText);
            }

            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                description += "\n\n# Additional Instructions\n" + task.AdditionalInstructions;

            // Planning team members and planOnly tasks produce findings/plans as their deliverable —
            // suppressing their output would lose the actual work product.
            var isPlanningMember = task.ParentTaskId != null && !task.ExtendedPlanning && !task.PlanOnly;
            var suppressEfficiency = isPlanningMember || task.PlanOnly;

            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsTeamsMode, task.ExtendedPlanning, task.PlanOnly, projectDescription, projectRulesBlock, task.AutoDecompose, task.SpawnTeam, isGameProject, task.Id, task.ApplyFix, suppressEfficiency, skillsBlock, featureContextBlock, crossProjectHintsBlock);

            // Inject pending changes block before the task description so the AI
            // is aware of in-progress uncommitted work in the repository.
            if (!string.IsNullOrWhiteSpace(pendingChangesBlock))
                basePrompt = $"{basePrompt}\n\n{pendingChangesBlock}";

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

        public string BuildClaudeCommand(bool skipPermissions, string? modelId = null, bool planMode = false, string? effortLevel = null)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var modelFlag = !string.IsNullOrEmpty(modelId) ? $" --model {modelId}" : "";
            var planFlag = planMode ? " --plan" : "";
            var effortFlag = !string.IsNullOrEmpty(effortLevel) && effortLevel != "high" ? $" --effort {effortLevel}" : "";
            return $"claude -p{skipFlag}{modelFlag}{planFlag}{effortFlag} --verbose --output-format stream-json";
        }

        public string BuildPowerShellScript(string projectPath, string promptFilePath,
            string claudeCmd)
        {
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | {claudeCmd}\n";
        }

        public string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath,
            bool skipPermissions, string? modelId = null, string? effortLevel = null)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var modelFlag = !string.IsNullOrEmpty(modelId) ? $" --model {modelId}" : "";
            var effortFlag = !string.IsNullOrEmpty(effortLevel) && effortLevel != "high" ? $" --effort {effortLevel}" : "";
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Write-Host 'Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host 'Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host 'Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{modelFlag}{effortFlag} --verbose\n" +
                   "if ($LASTEXITCODE -ne 0) { Write-Host \"`nClaude exited with code $LASTEXITCODE\" -ForegroundColor Yellow }\n" +
                   "Write-Host \"`nProcess finished. Press any key to close...\" -ForegroundColor Cyan\n" +
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

        public string BuildTeamsModeContinuationPrompt(int iteration, int maxIterations, string taskId = "")
        {
            var prompt = string.Format(TeamsModeContinuationTemplate, iteration, maxIterations);
            return OutputEfficiencyBlock + InjectFeatureModeLogFilename(prompt, taskId);
        }

        public string BuildTeamsModePlanConsolidationPrompt(int iteration, int maxIterations, string teamResults, string featureDescription)
        {
            return OutputEfficiencyBlock + string.Format(TeamsModePlanConsolidationTemplate, iteration, maxIterations, teamResults, featureDescription);
        }

        public string BuildTeamsModeEvaluationPrompt(int iteration, int maxIterations, string featureDescription, string implementationResults)
        {
            return OutputEfficiencyBlock + string.Format(TeamsModeEvaluationTemplate, iteration, maxIterations, featureDescription, implementationResults);
        }

        public string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DEPENDENCY CONTEXT");
            sb.AppendLine("Completed prerequisite tasks — use these cached results directly instead of re-reading the files:\n");

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

                if (!string.IsNullOrWhiteSpace(dep.Recommendations))
                    sb.AppendLine($"**Recommendations:**\n{dep.Recommendations}");

                // Inject the tail of the dependency's output so the downstream task
                // can see what was actually done without re-reading modified files.
                var depOutput = dep.OutputBuilder?.ToString();
                if (!string.IsNullOrWhiteSpace(depOutput))
                {
                    const int maxTailChars = 3_000;
                    var tail = depOutput.Length > maxTailChars
                        ? depOutput[^maxTailChars..]
                        : depOutput;
                    tail = Helpers.FormatHelpers.StripAnsiCodes(tail).Trim();
                    if (tail.Length > 0)
                    {
                        sb.AppendLine($"**Result output (tail):**");
                        sb.AppendLine($"```\n{tail}\n```");
                    }
                }

                sb.AppendLine();
            }

            return depIndex > 0 ? sb.ToString() : "";
        }
    }
}
