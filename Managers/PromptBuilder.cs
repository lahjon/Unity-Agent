using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace HappyEngine.Managers
{
    public class PromptBuilder : IPromptBuilder
    {
        // ── CLI Model Constants ─────────────────────────────────────

        /// <summary>Model used for exploration tasks (codebase analysis, decomposition, team design).</summary>
        public const string CliSonnetModel = "claude-sonnet-4-20250514";

        /// <summary>Model used for planning and execution tasks.</summary>
        public const string CliOpusModel = "claude-opus-4-20250514";

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

        public const string DefaultSystemPrompt =
            "# RULES\n" +
            "- Never access files outside the project root (./).\n" +
            "- Never store secrets (API keys, tokens, passwords) in project files. " +
            "Use %LOCALAPPDATA%\\HappyEngine\\ instead. Flag any hardcoded secrets found.\n\n";

        public const string McpPromptBlock =
            "# MCP\n" +
            "MCP server: mcp-for-unity-server at http://127.0.0.1:8080/mcp. " +
            "Verify it is reachable before starting. " +
            "Use MCP for Unity Editor operations (prefabs, scenes, screenshots, GameObjects) " +
            "that cannot be done through file edits alone.\n\n";

        public const string NoGitWriteBlock =
            "# NO GIT WRITES\n" +
            "Never modify repository state (commit, push, add, merge, rebase). Read-only git only (status, log, diff, show).\n\n";

        public const string NoPushBlock =
            "# NO GIT PUSH\n" +
            "Never run `git push` or `git push --force` or any push variant. You may commit and use other git write operations, but pushing is strictly forbidden. Push should only be done manually by the user through the git panel.\n\n";

        public const string GameRulesBlock =
            "# GAME PROJECT\n" +
            "This is a game project. Follow existing architecture patterns. " +
            "Consider performance (frame rate, memory, asset loading). " +
            "Maintain consistent UI patterns and input handling.\n\n";

        public const string PlanOnlyBlock =
            "# PLAN-ONLY MODE\n" +
            "Do NOT write, edit, create, or delete any files. Do NOT run any commands.\n\n" +
            "Your job: produce a detailed execution prompt for another agent.\n" +
            "1. Analyze the task — explore the codebase, understand architecture, patterns, and constraints.\n" +
            "2. Write a self-contained prompt with: problem statement, files to modify, step-by-step instructions, edge cases, and acceptance criteria.\n\n" +
            "Output in a ```EXECUTION_PROMPT``` code block. Do NOT implement anything.\n\n---\n";

        public const string ExtendedPlanningBlock =
            "# EXTENDED PLANNING MODE\n" +
            "Before implementing, you MUST:\n" +
            "1. Analyze: Identify objectives, implicit requirements, ambiguities, and constraints.\n" +
            "2. Specify: Rewrite as a detailed spec with acceptance criteria, edge cases, and affected files.\n" +
            "3. Plan: Create a step-by-step implementation plan with verification and risks.\n" +
            "4. Execute: Implement following your plan.\n\n---\n";

        public const string MessageBusBlockTemplate =
            "# MESSAGE BUS\n" +
            "You are part of a concurrent agent team. Shared bus: `.agent-bus/`. Your task ID: **{TASK_ID}**\n\n" +
            "**Read** `.agent-bus/_scratchpad.md` before modifying any files to see sibling tasks and claimed areas.\n\n" +
            "**Post** JSON to `.agent-bus/inbox/` as `{unix_ms}_{TASK_ID}_{type}.json`:\n" +
            "```json\n{\"from\":\"{TASK_ID}\",\"type\":\"finding|request|claim|response|status\",\"topic\":\"...\",\"body\":\"...\",\"mentions\":[]}\n```\n" +
            "Post **claim** before extensively modifying files. Post **finding** for discoveries affecting others. " +
            "Do NOT modify `_scratchpad.md`.\n\n";

        public const string SubtaskCoordinatorBlock =
            "# SUBTASK COORDINATOR\n" +
            "You spawned subtasks. Results arrive at `.agent-bus/inbox/*_subtask_result.json` with fields: " +
            "`child_task_id`, `status`, `summary`, `recommendations`, `file_changes`.\n\n" +
            "After reading results: assess success, retry or report failures, integrate successes, " +
            "and summarize what each subtask accomplished.\n\n";

        public const string DecompositionPromptBlock =
            "# TASK DECOMPOSITION\n" +
            "Analyze the task and break it into 2-5 independent subtasks. Explore the codebase first.\n" +
            "Do NOT implement anything or modify any files.\n\n" +
            "Output as JSON in a ```SUBTASKS``` block. Each entry: `description` (self-contained prompt) and `depends_on` (indices, [] if none).\n" +
            "```SUBTASKS\n[{\"description\": \"...\", \"depends_on\": []}, {\"description\": \"...\", \"depends_on\": [0]}]\n```\n" +
            "Prefer parallel subtasks. Minimize dependencies.\n\n---\n";

        public const string TeamDecompositionPromptBlock =
            "# TEAM SPAWN MODE\n" +
            "Design a team of 2-5 specialist agents for this task. Explore the codebase first.\n" +
            "Agents run concurrently with independent Claude sessions, coordinating via a shared message bus.\n" +
            "Do NOT implement anything or modify any files.\n\n" +
            "Output as JSON in a ```TEAM``` block. Each entry: `role` (short name), `description` (self-contained prompt with files and criteria), `depends_on` (indices, [] if none).\n" +
            "```TEAM\n[{\"role\": \"Backend\", \"description\": \"...\", \"depends_on\": []}, {\"role\": \"Tests\", \"description\": \"...\", \"depends_on\": [0]}]\n```\n" +
            "Prefer parallel execution. Minimize dependencies. Agents should check the message bus for sibling work.\n\n---\n";

        public const string ApplyFixBlock =
            "# APPLY FIX\n" +
            "Apply fixes directly without asking for confirmation. Never ask \"Want me to apply the fix?\" — just implement and explain.\n\n";

        public const string ConfirmBeforeChangesBlock =
            "# CONFIRM BEFORE CHANGES\n" +
            "Describe the issue and proposed solution before changing code. Ask the user to confirm before proceeding.\n\n";

        public const string FailureRecoveryBlock =
            "# FAILURE RECOVERY MODE\n" +
            "A previous attempt at this task FAILED. You are a diagnostic recovery agent.\n\n" +
            "## YOUR MISSION\n" +
            "1. Analyze the failure output and error messages from the previous attempt.\n" +
            "2. Identify the root cause of the failure (compilation error, runtime exception, incorrect logic, missing dependency, etc.).\n" +
            "3. Fix the issue by making the minimum necessary changes.\n" +
            "4. Verify your fix compiles and addresses the original task requirements.\n\n" +
            "## GUIDELINES\n" +
            "- Focus on fixing the specific failure, not refactoring or improving unrelated code.\n" +
            "- If the previous attempt partially succeeded, preserve that work and only fix what broke.\n" +
            "- If the error is environmental (missing tool, permission issue), document the blocker clearly.\n" +
            "- Check for common failure patterns: syntax errors, missing imports, wrong file paths, type mismatches.\n\n";

        public const string PlanningTeamMemberBlock =
            "# PLANNING-ONLY RESTRICTIONS\n" +
            "You are a planning team member.\n" +
            "Post all findings and recommendations to the message bus only.\n" +
            "Your output and completion summary are automatically collected — do not write them to files.\n\n" +
            "# AUTO-COMPLETION\n" +
            "When you have finished your exploration and posted findings to the message bus, " +
            "you MUST stop immediately. Provide a brief final summary of your findings and then exit.\n" +
            "Do NOT ask the user for confirmation, approval, or next steps.\n" +
            "Do NOT re-verify, re-post, or loop back to check your own work.\n" +
            "Do NOT say things like \"Want me to apply the fix?\" or \"Shall I continue?\".\n" +
            "Your results are automatically collected by the orchestrator — just complete your analysis and stop.\n\n";

        public const string FeatureModeInitialTemplate =
            "# FEATURE MODE — PLANNING PHASE\n" +
            "You are the planning coordinator for an iterative feature implementation.\n\n" +
            "## RESTRICTIONS\n" +
            "- **No git commands** of any kind.\n" +
            "- **No file modifications** — this is a planning-only phase.\n" +
            "- **Stay in project root** — never access files outside ./\n\n" +
            "## YOUR TASK\n" +
            "Design a team of 2-5 specialist agents to thoroughly plan the implementation of the feature described below.\n" +
            "Each team member should explore different aspects of the codebase and architecture relevant to this feature.\n\n" +
            "## TEAM DESIGN GUIDELINES\n" +
            "- Include an **Architect** role that produces the high-level design\n" +
            "- Include roles for different areas of the codebase affected\n" +
            "- Each member should explore specific files, patterns, and constraints\n" +
            "- Members coordinate via the shared message bus\n" +
            "- NO member should implement anything — planning and exploration only\n" +
            "- **CRITICAL**: Each member's description MUST include the instruction: " +
            "\"Do NOT create or modify any files. Do NOT write ARCHITECTURE.md or any documentation files. " +
            "Post all findings to the message bus. Your output is collected automatically.\"\n\n" +
            "## OUTPUT\n" +
            "Output a team definition as JSON in a ```TEAM``` block:\n" +
            "```TEAM\n[{\"role\": \"Architect\", \"description\": \"Explore the codebase and design...\", \"depends_on\": []}]\n```\n\n" +
            "# USER PROMPT / TASK\n";

        public const string FeatureModePlanConsolidationTemplate =
            "# FEATURE MODE — PLAN CONSOLIDATION (iteration {0}/{1})\n" +
            "You are consolidating the planning team's findings into an actionable step-by-step implementation plan.\n\n" +
            "## RESTRICTIONS\n" +
            "- **No git commands** of any kind.\n" +
            "- **No file modifications** — produce a plan only.\n" +
            "- **Stay in project root** — never access files outside ./\n\n" +
            "## PLANNING TEAM RESULTS\n{2}\n\n" +
            "## YOUR TASK\n" +
            "Based on the planning team's findings, create a detailed step-by-step implementation plan.\n" +
            "Each step should be a self-contained task that an independent agent can execute.\n\n" +
            "## OUTPUT FORMAT\n" +
            "Output the plan as JSON in a ```FEATURE_STEPS``` block:\n" +
            "```FEATURE_STEPS\n" +
            "[{{\"description\": \"Self-contained task prompt with: what to do, which files to modify, acceptance criteria\", \"depends_on\": []}}]\n" +
            "```\n\n" +
            "Rules:\n" +
            "- Each step must be fully self-contained with enough context for an independent agent\n" +
            "- Include specific file paths, function names, and detailed changes needed\n" +
            "- Use depends_on (0-indexed) for steps that must wait for earlier steps\n" +
            "- Prefer parallel steps where possible\n" +
            "- Each step should be focused and achievable by a single agent session\n" +
            "- Include acceptance criteria for each step\n\n" +
            "# FEATURE REQUEST\n{3}\n";

        public const string FeatureModeEvaluationTemplate =
            "# FEATURE MODE — EVALUATION (iteration {0}/{1})\n" +
            "You are evaluating the results of a feature implementation iteration.\n\n" +
            "## RESTRICTIONS\n" +
            "- **No git commands** of any kind.\n" +
            "- **Stay in project root** — never access files outside ./\n\n" +
            "## FEATURE REQUEST\n{2}\n\n" +
            "## IMPLEMENTATION RESULTS\n{3}\n\n" +
            "## YOUR TASK\n" +
            "1. Review all implementation results from the step tasks above.\n" +
            "2. Verify the feature has been fully implemented by examining the actual code changes.\n" +
            "3. Check for:\n" +
            "   - Missing functionality from the original request\n" +
            "   - Bugs or issues introduced\n" +
            "   - Integration problems between steps\n" +
            "   - Edge cases not handled\n" +
            "   - Build or compilation errors\n" +
            "4. If issues are found, fix them directly.\n" +
            "5. End with exactly one of:\n" +
            "   - `STATUS: COMPLETE` — if the feature is fully implemented and working\n" +
            "   - `STATUS: NEEDS_MORE_WORK` — if there are remaining issues that need another iteration\n\n" +
            "If NEEDS_MORE_WORK, list the specific issues that need to be addressed in the next iteration.\n";

        public const string FeatureModeContinuationTemplate =
            "# FEATURE MODE CONTINUATION (iteration {0}/{1})\n" +
            "Restrictions: No git, no OS modifications, stay in project root, no destructive operations.\n\n" +
            "## WORKFLOW\n" +
            "1. Read `.feature_log.md` for context on what's done and remaining.\n" +
            "2. Investigate: Look for bugs, edge cases, incomplete work, code quality issues, broken functionality.\n" +
            "3. Fix and improve: Bugs first, then remaining checklist items, then robustness (within scope only).\n" +
            "4. Add a **Suggestions** section to `.feature_log.md` with actionable improvement ideas within scope.\n" +
            "5. Verify all checklist items and exit criteria. Update `.feature_log.md`.\n" +
            "6. End with exactly: `STATUS: COMPLETE` or `STATUS: NEEDS_MORE_WORK`\n\n" +
            "Continue working now.";

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
            bool applyFix = true)
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            var gameBlock = isGameProject ? GameRulesBlock : "";

            if (isFeatureMode)
                return descBlock + projectRulesBlock + gameBlock + FeatureModeInitialTemplate + description;

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
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + gameBlock + mcpBlock + applyFixBlock + planningBlock + planOnlyBlock + decomposeBlock + teamBlock +
                "# USER PROMPT / TASK\n" + description;
        }

        public string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false)
        {
            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                description += "\n\n# Additional Instructions\n" + task.AdditionalInstructions;
            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsFeatureMode, task.ExtendedPlanning, task.NoGitWrite, task.PlanOnly, projectDescription, projectRulesBlock, task.AutoDecompose, task.SpawnTeam, isGameProject, task.Id, task.ApplyFix);
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

        public string BuildMessageBusBlock(string taskId,
            List<(string id, string summary)> siblings)
        {
            var block = MessageBusBlockTemplate.Replace("{TASK_ID}", taskId);

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
                   $"Write-Host '[HappyEngine] Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host '[HappyEngine] Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host '[HappyEngine] Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{remoteFlag}{modelFlag} --verbose\n" +
                   "if ($LASTEXITCODE -ne 0) { Write-Host \"`n[HappyEngine] Claude exited with code $LASTEXITCODE\" -ForegroundColor Yellow }\n" +
                   "Write-Host \"`n[HappyEngine] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
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
            return InjectFeatureModeLogFilename(prompt, taskId);
        }

        public string BuildFeatureModePlanConsolidationPrompt(int iteration, int maxIterations, string teamResults, string featureDescription)
        {
            return string.Format(FeatureModePlanConsolidationTemplate, iteration, maxIterations, teamResults, featureDescription);
        }

        public string BuildFeatureModeEvaluationPrompt(int iteration, int maxIterations, string featureDescription, string implementationResults)
        {
            return string.Format(FeatureModeEvaluationTemplate, iteration, maxIterations, featureDescription, implementationResults);
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
