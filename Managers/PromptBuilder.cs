using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace HappyEngine.Managers
{
    public class PromptBuilder : IPromptBuilder
    {
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
            "Never execute git commands that modify repository state (commit, push, add, merge, rebase, etc.). " +
            "Read-only commands (status, log, diff, show) are allowed. " +
            "Refuse any task requiring git write operations.\n\n";

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

        public const string FeatureModeInitialTemplate =
            "# FEATURE MODE AUTONOMOUS TASK\n" +
            "You will be called repeatedly to iterate until complete.\n\n" +
            "## RESTRICTIONS\n" +
            "- **No git commands** of any kind.\n" +
            "- **No OS modifications** (system files, registry, env vars, PATH, services, packages).\n" +
            "- **Stay in project root** — never access files outside ./\n" +
            "- **No destructive operations** (recursive deletes, killing processes, etc.).\n\n" +
            "## WORKFLOW\n" +
            "1. Create `.feature_log.md` with: checklist of sub-tasks, exit criteria, progress log section.\n" +
            "2. Implement — work through the checklist.\n" +
            "3. Review — look for bugs, edge cases, missing handling.\n" +
            "4. Verify — confirm each checklist item is complete. Update `.feature_log.md`.\n" +
            "5. End with exactly: `STATUS: COMPLETE` or `STATUS: NEEDS_MORE_WORK`\n\n" +
            "# USER PROMPT / TASK\n" +
            "The following is the actual task from the user. Everything above is system configuration.\n\n";

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
            bool isGameProject = false, string taskId = "")
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            var gameBlock = isGameProject ? GameRulesBlock : "";

            if (isFeatureMode)
                return descBlock + projectRulesBlock + gameBlock + InjectFeatureModeLogFilename(FeatureModeInitialTemplate, taskId) + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            var planOnlyBlock = planOnly ? PlanOnlyBlock : "";
            var gitBlock = noGitWrite ? NoGitWriteBlock : "";
            var decomposeBlock = autoDecompose ? DecompositionPromptBlock : "";
            var teamBlock = spawnTeam ? TeamDecompositionPromptBlock : "";
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + gameBlock + mcpBlock + planningBlock + planOnlyBlock + decomposeBlock + teamBlock +
                "# USER PROMPT / TASK\nThe following is the actual task from the user. Everything above is system configuration.\n\n" + description;
        }

        public string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false)
        {
            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                description += "\n\n# Additional Instructions\n" + task.AdditionalInstructions;
            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsFeatureMode, task.ExtendedPlanning, task.NoGitWrite, task.PlanOnly, projectDescription, projectRulesBlock, task.AutoDecompose, task.SpawnTeam, isGameProject, task.Id);
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

        public string BuildClaudeCommand(bool skipPermissions, bool remoteSession)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            return $"claude -p{skipFlag}{remoteFlag} --verbose --output-format stream-json $prompt";
        }

        public string BuildPowerShellScript(string projectPath, string promptFilePath,
            string claudeCmd)
        {
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"$prompt = Get-Content -Raw -LiteralPath '{promptFilePath}'\n" +
                   $"{claudeCmd}\n";
        }

        public string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath,
            bool skipPermissions, bool remoteSession)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Write-Host '[HappyEngine] Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host '[HappyEngine] Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host '[HappyEngine] Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{remoteFlag} --verbose\n" +
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
