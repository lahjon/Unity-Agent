using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            "- Never access files outside project root (./).\n" +
            "- Never store secrets in project files. Use %LOCALAPPDATA%\\HappyEngine\\ instead. Flag any hardcoded secrets.\n\n";

        public const string McpPromptBlock =
            "# MCP\n" +
            "Server: mcp-for-unity-server @ http://127.0.0.1:8080/mcp (Unity Editor ops).\n" +
            "WORKFLOW: Scene→Prefab (create GameObjects in scene, save as Prefabs). No script generation for scene construction.\n" +
            "BATCH ALL: Use `batch_execute` for multiple ops (10-100x faster). Plan first, execute once.\n\n";

        public const string NoGitWriteBlock =
            "# NO GIT WRITES\n" +
            "Read-only git only (status, log, diff, show). Never commit, push, add, merge, or rebase.\n\n";

        public const string NoPushBlock =
            "# NO GIT PUSH\n" +
            "Never `git push` (any variant). Commits and other git writes allowed. Push only via user's git panel.\n\n";

        public const string GameRulesBlock =
            "# GAME PROJECT\n" +
            "Follow existing architecture. Consider performance (frame rate, memory, asset loading). Maintain consistent UI/input patterns.\n\n";

        public const string PlanOnlyBlock =
            "# PLAN-ONLY MODE\n" +
            "Do NOT write/edit/create/delete files or run commands.\n\n" +
            "Produce a detailed execution prompt for another agent:\n" +
            "1. Analyze — explore codebase, understand architecture, patterns, constraints.\n" +
            "2. Write self-contained prompt: problem statement, files to modify, step-by-step instructions, edge cases, acceptance criteria.\n\n" +
            "Output in ```EXECUTION_PROMPT``` code block. Do NOT implement.\n\n---\n";

        public const string ExtendedPlanningBlock =
            "# EXTENDED PLANNING MODE\n" +
            "Before implementing, you MUST:\n" +
            "1. Analyze: objectives, implicit requirements, ambiguities, constraints.\n" +
            "2. Specify: detailed spec with acceptance criteria, edge cases, affected files.\n" +
            "3. Plan: step-by-step implementation with verification and risks.\n" +
            "4. Execute: implement per plan.\n" +
            "In autonomous mode, proceed through all steps without pausing.\n\n---\n";

        public const string MessageBusBlockTemplate =
            "# MESSAGE BUS\n" +
            "Concurrent agent team. Bus: `{BUS_PATH}`. Your ID: **{TASK_ID}**\n\n" +
            "**Read** `{BUS_PATH}/_scratchpad.md` before modifying files (see sibling tasks/claimed areas).\n\n" +
            "**Post** JSON to `{BUS_PATH}/inbox/` as `{unix_ms}_{TASK_ID}_{type}.json`:\n" +
            "```json\n{\"from\":\"{TASK_ID}\",\"type\":\"finding|request|claim|response|status\",\"topic\":\"...\",\"body\":\"...\",\"mentions\":[]}\n```\n" +
            "Post **claim** before extensive file modifications. Post **finding** for discoveries affecting others. " +
            "Do NOT modify `_scratchpad.md`.\n\n";

        public const string SubtaskCoordinatorBlock =
            "# SUBTASK COORDINATOR\n" +
            "Subtask results arrive at `{BUS_PATH}/inbox/*_subtask_result.json` (fields: " +
            "`child_task_id`, `status`, `summary`, `recommendations`, `file_changes`).\n\n" +
            "After reading: assess success, retry/report failures, integrate successes, summarize each subtask.\n\n";

        public const string DecompositionPromptBlock =
            "# TASK DECOMPOSITION\n" +
            "Break task into 2-5 independent subtasks. Explore codebase first. Do NOT implement or modify files.\n\n" +
            "Output JSON in ```SUBTASKS``` block: `description` (self-contained prompt), `depends_on` (indices, [] if none).\n" +
            "```SUBTASKS\n[{\"description\": \"...\", \"depends_on\": []}, {\"description\": \"...\", \"depends_on\": [0]}]\n```\n" +
            "Prefer parallel. Minimize dependencies.\n\n---\n";

        public const string TeamDecompositionPromptBlock =
            "# TEAM SPAWN MODE\n" +
            "Design 2-5 specialist agents. Explore codebase first.\n" +
            "Agents run concurrently with independent Claude sessions, coordinating via shared message bus.\n" +
            "Do NOT implement or modify files.\n\n" +
            "Output JSON in ```TEAM``` block: `role` (short name), `description` (self-contained prompt with files+criteria), `depends_on` (indices, [] if none).\n" +
            "```TEAM\n[{\"role\": \"Backend\", \"description\": \"...\", \"depends_on\": []}, {\"role\": \"Tests\", \"description\": \"...\", \"depends_on\": [0]}]\n```\n" +
            "Prefer parallel. Minimize dependencies. Check message bus for sibling work.\n\n---\n";

        public const string ApplyFixBlock =
            "# APPLY FIX\n" +
            "Apply fixes directly. Never ask for confirmation — implement and explain.\n\n";

        public const string ConfirmBeforeChangesBlock =
            "# CONFIRM BEFORE CHANGES\n" +
            "Describe the issue and proposed solution. Ask user to confirm before proceeding.\n\n";

        public const string AutonomousExecutionBlock =
            "# AUTONOMOUS EXECUTION MODE\n" +
            "FULLY AUTONOMOUS — part of a larger orchestrated task.\n" +
            "- NEVER ask for user input, approval, confirmation, or review\n" +
            "- NEVER pause for feedback or ask \"Should I proceed?\"\n" +
            "- Make all decisions independently; on ambiguity, choose reasonably and document reasoning\n" +
            "- Execute to completion without user interaction\n" +
            "- Results automatically evaluated by another agent\n\n" +
            "# EXECUTION FOCUS\n" +
            "- Implement task fully per description and acceptance criteria\n" +
            "- Complete all implementation, testing, verification autonomously\n" +
            "- Fix issues encountered during implementation\n" +
            "- Completion summary auto-collected for evaluation\n\n";

        public const string FailureRecoveryBlock =
            "# FAILURE RECOVERY MODE\n" +
            "Previous attempt FAILED. You are a diagnostic recovery agent.\n\n" +
            "## MISSION\n" +
            "1. Analyze failure output and error messages.\n" +
            "2. Identify root cause (compile error, runtime exception, wrong logic, missing dependency, etc.).\n" +
            "3. Apply minimum necessary fix.\n" +
            "4. Verify fix compiles and meets original task requirements.\n\n" +
            "## GUIDELINES\n" +
            "- Fix the specific failure only — don't refactor unrelated code.\n" +
            "- Preserve partial successes; only fix what broke.\n" +
            "- If environmental (missing tool, permission), document blocker clearly.\n" +
            "- Check: syntax errors, missing imports, wrong paths, type mismatches.\n\n";

        public const string PlanningTeamMemberBlock =
            "# PLANNING-ONLY RESTRICTIONS\n" +
            "Planning team member. Post all findings to message bus only.\n" +
            "Output auto-collected — do not write to files.\n\n" +
            "# AUTONOMOUS MODE\n" +
            "FULLY AUTONOMOUS — never ask for input, approval, or confirmation.\n" +
            "Make all decisions independently. On ambiguity, choose reasonably and document reasoning.\n\n" +
            "# AUTO-COMPLETION\n" +
            "After posting findings to bus, stop immediately. Brief final summary, then exit.\n" +
            "Results auto-collected by orchestrator.\n\n";

        public const string FeatureModeInitialTemplate =
            "# FEATURE MODE — PLANNING PHASE\n" +
            "Planning coordinator for iterative feature implementation.\n\n" +
            "## RESTRICTIONS\n" +
            "No git commands. No file modifications. Stay in project root (./).\n\n" +
            "## TASK\n" +
            "Design 2-5 specialist agents to plan the feature below.\n" +
            "Each explores different codebase/architecture aspects.\n\n" +
            "## TEAM DESIGN\n" +
            "- Include **Architect** role (high-level design)\n" +
            "- Include roles per affected codebase area\n" +
            "- Each member explores specific files, patterns, constraints\n" +
            "- Coordinate via shared message bus\n" +
            "- Planning/exploration only — NO implementation\n" +
            "- **CRITICAL**: Each member description MUST include:\n" +
            "  1. \"Do NOT create/modify files or write documentation.\"\n" +
            "  2. \"Post all findings to message bus. Output auto-collected.\"\n" +
            "  3. \"FULLY AUTONOMOUS — never ask for user input.\"\n" +
            "  4. \"Complete analysis and exit. No confirmation prompts.\"\n\n" +
            "## OUTPUT\n" +
            "```TEAM\n[{\"role\": \"Architect\", \"description\": \"Explore codebase and design...\", \"depends_on\": []}]\n```\n\n" +
            "# USER PROMPT / TASK\n";

        public const string FeatureModePlanConsolidationTemplate =
            "# FEATURE MODE — PLAN CONSOLIDATION (iteration {0}/{1})\n" +
            "Consolidate planning team findings into actionable step-by-step implementation plan.\n\n" +
            "## RESTRICTIONS\n" +
            "No git. No file modifications. Stay in project root.\n\n" +
            "## PLANNING TEAM RESULTS\n{2}\n\n" +
            "## TASK\n" +
            "Create detailed step-by-step plan. Each step = self-contained task for an independent agent.\n\n" +
            "## OUTPUT\n" +
            "```FEATURE_STEPS\n" +
            "[{{\"description\": \"Self-contained prompt: what to do, files to modify, acceptance criteria\", \"depends_on\": []}}]\n" +
            "```\n\n" +
            "## PARALLELISM EXAMPLE\n" +
            "Feature with backend API + frontend UI + tests + docs:\n" +
            "- Step 0: Backend API (no deps)\n" +
            "- Step 1: Frontend UI (no deps — mock API)\n" +
            "- Step 2: Backend tests (no deps)\n" +
            "- Step 3: Frontend tests (no deps)\n" +
            "- Step 4: Integration (depends_on: [0,1,2,3])\n\n" +
            "Rules:\n" +
            "- Self-contained steps with specific file paths, functions, changes\n" +
            "- **MAXIMIZE PARALLELISM**: depends_on only for TRUE technical deps (e.g. step B needs types from step A)\n" +
            "- No deps for mere logical ordering — split independent areas into parallel steps\n" +
            "- Final consolidation step depends on all others if needed\n" +
            "- Each step focused, achievable by single agent. Include acceptance criteria\n" +
            "- Add \"Execute autonomously\" to each step\n\n" +
            "# FEATURE REQUEST\n{3}\n";

        public const string FeatureModeEvaluationTemplate =
            "# FEATURE MODE — EVALUATION (iteration {0}/{1})\n" +
            "Evaluate feature implementation results.\n\n" +
            "## RESTRICTIONS\n" +
            "No git. Stay in project root. AUTONOMOUS — no user input.\n\n" +
            "## FEATURE REQUEST\n{2}\n\n" +
            "## IMPLEMENTATION RESULTS\n{3}\n\n" +
            "## TASK\n" +
            "1. Review all step results and examine actual code changes.\n" +
            "2. Check: missing functionality, bugs, integration issues, unhandled edge cases, build errors.\n" +
            "3. Fix issues directly if found.\n" +
            "4. End with exactly:\n" +
            "   - `STATUS: COMPLETE` — fully implemented and working\n" +
            "   - `STATUS: NEEDS_MORE_WORK` — list specific remaining issues\n";

        public const string FeatureModeContinuationTemplate =
            "# FEATURE MODE CONTINUATION (iteration {0}/{1})\n" +
            "Restrictions: No git, no OS mods, stay in project root.\n\n" +
            "## WORKFLOW\n" +
            "1. Read `.feature_log.md` for done/remaining context.\n" +
            "2. Investigate: bugs, edge cases, incomplete work, quality issues.\n" +
            "3. Fix: bugs first → remaining checklist → robustness (within scope).\n" +
            "4. Add **Suggestions** section to `.feature_log.md` with actionable improvements.\n" +
            "5. Verify all checklist items and exit criteria. Update `.feature_log.md`.\n" +
            "6. End with: `STATUS: COMPLETE` or `STATUS: NEEDS_MORE_WORK`\n\n" +
            "Continue working now.";

        // ── Prompts formerly in other files (centralized) ────────────

        public const string GameProjectExplorationPrompt =
            "Game project exploration agent. Explore then produce token-efficient description.\n\n" +
            "STEP 1 — EXPLORE (use tools, do NOT guess):\n" +
            "- List top-level directory structure\n" +
            "- Check for Unity (ProjectSettings/, Assets/), Unreal (*.uproject), other engines\n" +
            "- Read key configs (ProjectSettings/ProjectSettings.asset, *.uproject, project.json, etc.)\n" +
            "- Find main game scripts in Scripts/, Source/, or similar\n" +
            "- Identify game type, genre, key systems\n\n" +
            "STEP 2 — Output EXACTLY:\n\n" +
            "<short>\nOne-line: game genre/type + engine. Max 200 chars. No preamble.\n</short>\n\n" +
            "<long>\nTerse bullet-points (no filler):\n" +
            "- Game: [type/genre + description]\n" +
            "- Engine: [Unity/Unreal/Godot/custom/etc.]\n" +
            "- Platform: [targets if identifiable]\n" +
            "- Key dirs: [Assets/, Scripts/, etc.]\n" +
            "- Systems: [major gameplay systems]\n" +
            "- Tech: [rendering, multiplayer, physics, etc.]\n" +
            "Max 800 chars. Omit empty sections. No preamble.\n</long>\n\n" +
            "RULES:\n" +
            "- Focus on game-specific aspects, not generic code structure.\n" +
            "- Skip binary folders (Library/, Temp/, Builds/).\n" +
            "- Output ONLY <short> and <long> tags.\n" +
            "- Factual summaries, not agent responses.";

        public const string CodebaseExplorationPrompt =
            "Codebase exploration agent. Explore then produce token-efficient description.\n\n" +
            "STEP 1 — EXPLORE (use tools, do NOT guess):\n" +
            "- List top-level directory structure\n" +
            "- Read project configs (.csproj, package.json, Cargo.toml, etc.)\n" +
            "- Read main entry point and key source files\n" +
            "- Identify architecture, patterns, major components\n\n" +
            "STEP 2 — Output EXACTLY:\n\n" +
            "<short>\nOne-line: what it is + tech stack. Max 200 chars. No preamble.\n</short>\n\n" +
            "<long>\nTerse bullet-points (no filler):\n" +
            "- Purpose: [what project does]\n" +
            "- Stack: [languages, frameworks, runtime]\n" +
            "- Architecture: [MVVM/MVC/microservices/etc.]\n" +
            "- Key dirs: [top-level source dirs]\n" +
            "- Components: [major classes/modules + roles]\n" +
            "- Patterns: [DI, async, conventions, etc.]\n" +
            "Max 800 chars. Omit empty sections. No preamble.\n</long>\n\n" +
            "RULES:\n" +
            "- Base on actual files read, not assumptions.\n" +
            "- Output ONLY <short> and <long> tags.\n" +
            "- No conversational text or commentary.\n" +
            "- Factual summaries, not agent responses.";

        /// <summary>Template for project analysis suggestions. Use string.Format with {0} = categoryFilter.</summary>
        public const string ProjectSuggestionPromptTemplate =
            "Project analysis agent. Explore codebase and suggest improvements.\n\n" +
            "STEP 1 — EXPLORE:\n" +
            "- List top-level directory structure\n" +
            "- Read key source files, configs, entry points\n" +
            "- Understand architecture, patterns, current state\n\n" +
            "STEP 2 — SUGGEST:\n" +
            "Focus on: {0}\n\n" +
            "Generate 5-8 actionable suggestions. Each:\n" +
            "- Title: short, starts with action verb (Add/Refactor/Fix/Implement)\n" +
            "- Description: 2-4 sentences as implementation instructions — files to change, code to write, expected outcome. No analytical observations.\n\n" +
            "STEP 3 — OUTPUT:\n" +
            "Output ONLY JSON array: [{{\"title\": \"...\", \"description\": \"...\"}}]\n" +
            "No other text, no markdown fences.";

        public const string ChatAssistantSystemPrompt =
            "Helpful coding assistant in Happy Engine app. Concise, practical suggestions. " +
            "Keep responses short unless asked for detail. User works on software projects, primarily Unity game dev.";

        public const string WorkflowDecompositionSystemPrompt =
            "Workflow decomposition assistant. User describes a multi-step workflow in plain English. " +
            "Break it into discrete tasks with dependency relationships.\n\n" +
            "Respond with ONLY valid JSON array (no markdown fences, no explanation). Each element:\n" +
            "- \"taskName\": short name (max 60 chars)\n" +
            "- \"description\": detailed, actionable task description\n" +
            "- \"dependsOn\": array of taskName strings this depends on ([] if none)\n\n" +
            "Rules:\n" +
            "- Logical order; only reference earlier taskNames\n" +
            "- Concise but descriptive names\n" +
            "- Actionable, specific descriptions\n" +
            "- Identify parallelizable work\n" +
            "- Valid DAG (no cycles)\n\n" +
            "Example:\n" +
            "[{\"taskName\":\"Refactor auth module\",\"description\":\"Refactor auth to use JWT instead of session cookies\",\"dependsOn\":[]}," +
            "{\"taskName\":\"Update API endpoints\",\"description\":\"Update all endpoints for new JWT auth\",\"dependsOn\":[\"Refactor auth module\"]}," +
            "{\"taskName\":\"Run integration tests\",\"description\":\"Run full integration suite to verify endpoints with new auth\",\"dependsOn\":[\"Update API endpoints\"]}]";

        /// <summary>Template for result verification. Use string.Format with {0}=taskDescription, {1}=contextTail, {2}=summaryBlock.</summary>
        public const string ResultVerificationPromptTemplate =
            "Verify AI coding agent's work against requested task.\n\n" +
            "TASK:\n{0}\n\n" +
            "AGENT OUTPUT (tail):\n{1}\n\n" +
            "{2}" +
            "Did the agent accomplish the request?\n\n" +
            "Rules:\n" +
            "- Check core requirements addressed\n" +
            "- Correct changes → PASS; errors/incorrect/missed requirements → FAIL\n" +
            "- On failure/cancel, check if partial work is correct\n" +
            "- Focus on correctness, not style\n\n" +
            "Respond with EXACTLY one line:\n" +
            "PASS|<one-sentence verification summary>\nor\nFAIL|<one-sentence failure description>\n\n" +
            "Examples:\n" +
            "PASS|Auth endpoint added with JWT validation and error handling\n" +
            "FAIL|Migration created but API endpoint not updated for new schema\n\n" +
            "Output ONLY the PASS/FAIL line. Nothing else.";

        public const string TokenLimitRetryContinuationPrompt =
            "Continue where you left off. Previous attempt interrupted by token/rate limit. Pick up from where you stopped.";

        public const string FeatureModeIterationPlanningTemplate =
            "# PREVIOUS ITERATION EVALUATION\n" +
            "Address the identified issues from the previous iteration:\n\n";

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
