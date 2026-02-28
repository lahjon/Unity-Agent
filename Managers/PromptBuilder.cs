using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AgenticEngine.Managers
{
    public class PromptBuilder : IPromptBuilder
    {
        // ── Constants ────────────────────────────────────────────────

        public const string DefaultSystemPrompt =
            "# STRICT RULE " +
            "Never access, read, write, or reference any files outside the project root directory. " +
            "All operations must stay within ./ " +
            "# STRICT RULE — NO SECRETS IN PROJECT " +
            "Never store personal security information directly in project files. " +
            "This includes API keys, tokens, passwords, secrets, credentials, and any other sensitive data. " +
            "If a task requires storing such values, they MUST be placed in the system AppData directory " +
            "(%LOCALAPPDATA%\\AgenticEngine\\) — never in source code, config files, or any file within the project tree. " +
            "If you encounter hardcoded secrets in the project, flag them to the user and recommend moving them to AppData. " +
            "# TASK: ";

        public const string McpPromptBlock =
            "# MCP VERIFICATION " +
            "Before starting the task, verify the MCP server is running and responding. " +
            "The MCP server is mcp-for-unity-server on http://127.0.0.1:8080/mcp. " +
            "Confirm it is reachable before proceeding. " +
            "# MCP USAGE " +
            "Use the MCP server when the task requires interacting with Unity directly. " +
            "This includes modifying prefabs, accessing scenes, taking screenshots, " +
            "inspecting GameObjects, and any other Unity Editor operations that cannot be " +
            "done through file edits alone. ";

        public const string NoGitWriteBlock =
            "# STRICT RULE — NO GIT WRITE OPERATIONS\n" +
            "You must NEVER execute any git command that writes, pushes, creates, or modifies repository state. " +
            "This includes but is not limited to: git push, git commit, git add, git init, git merge, git rebase, " +
            "git cherry-pick, git revert, git reset, git checkout -b, git branch, git tag, git stash, git clone, " +
            "git pull, git fetch, git remote add, and git mv. " +
            "Read-only git commands such as git status, git log, git diff, and git show are permitted. " +
            "If the task requires a git write operation, refuse and explain that git write operations are disabled.\n\n";

        public const string GameRulesBlock =
            "# GAME PROJECT\n" +
            "This project is a game. When implementing features, keep game development best practices in mind:\n" +
            "- Follow existing game architecture patterns in the project\n" +
            "- Consider performance implications (frame rate, memory, asset loading)\n" +
            "- Maintain consistent art style, UI patterns, and input handling\n" +
            "- Test gameplay interactions and edge cases\n\n";

        public const string PlanOnlyBlock =
            "# PLAN-ONLY MODE — DO NOT EXECUTE\n" +
            "You are in PLAN-ONLY mode. You must NEVER write, edit, create, or delete any files. " +
            "You must NEVER execute any code, run any commands, or make any changes to the project.\n\n" +
            "Instead, your ONLY job is to produce a detailed, actionable prompt that another agent can later execute. " +
            "Follow these steps:\n\n" +
            "## Step 1: Analyze the Task\n" +
            "Read the task description carefully. Explore the codebase to understand:\n" +
            "- The current architecture and relevant files\n" +
            "- Existing patterns and conventions\n" +
            "- Dependencies and constraints\n\n" +
            "## Step 2: Produce the Execution Prompt\n" +
            "Write a complete, self-contained prompt that includes:\n" +
            "- A clear problem statement and objective\n" +
            "- Specific files to modify and what changes to make\n" +
            "- Step-by-step implementation instructions\n" +
            "- Edge cases and acceptance criteria\n" +
            "- Any architectural decisions already made\n\n" +
            "## Output Format\n" +
            "Your final output MUST be wrapped in a markdown code block labeled `EXECUTION_PROMPT`:\n" +
            "```EXECUTION_PROMPT\n" +
            "<your detailed execution prompt here>\n" +
            "```\n\n" +
            "REMEMBER: Do NOT implement anything. Only produce the prompt.\n\n" +
            "---\n";

        public const string ExtendedPlanningBlock =
            "# EXTENDED PLANNING MODE\n" +
            "Before beginning any implementation, you MUST first deeply analyze and enhance the task prompt. " +
            "Follow these steps:\n\n" +
            "## Step 1: Analyze the Task\n" +
            "Read the task description carefully. Identify:\n" +
            "- The core objective and desired outcome\n" +
            "- Any implicit requirements not explicitly stated\n" +
            "- Potential ambiguities or gaps in the requirements\n" +
            "- Technical constraints and dependencies\n\n" +
            "## Step 2: Redefine and Enhance the Prompt\n" +
            "Rewrite the task as a detailed, unambiguous specification. Include:\n" +
            "- A clear problem statement\n" +
            "- Specific acceptance criteria\n" +
            "- Edge cases to handle\n" +
            "- Architectural considerations\n" +
            "- Files and components likely to be affected\n\n" +
            "## Step 3: Create an Implementation Plan\n" +
            "Before writing any code, create a step-by-step plan that covers:\n" +
            "- The order of changes to make\n" +
            "- How to verify each step\n" +
            "- Risks and mitigations\n\n" +
            "## Step 4: Execute\n" +
            "Only after completing steps 1-3, proceed with the implementation following your plan.\n\n" +
            "---\n";

        public const string MessageBusBlockTemplate =
            "# MULTI-AGENT MESSAGE BUS\n" +
            "You are part of a team of concurrent agents working on the same project.\n" +
            "A shared message bus exists at `.agent-bus/` in the project root.\n\n" +
            "## Reading Messages\n" +
            "Read `.agent-bus/_scratchpad.md` to see sibling tasks, recent messages, and claimed areas.\n" +
            "Check it BEFORE starting work on a new file or component.\n\n" +
            "## Posting Messages\n" +
            "Write a JSON file to `.agent-bus/inbox/`.\n" +
            "Filename: `{unix_ms}_{TASK_ID}_{type}.json`\n" +
            "Your task ID is: **{TASK_ID}**\n\n" +
            "Content:\n```json\n" +
            "{\"from\":\"{TASK_ID}\",\"type\":\"finding|request|claim|response|status\"," +
            "\"topic\":\"Brief subject\",\"body\":\"Your message\",\"mentions\":[]}\n```\n\n" +
            "## Message Types\n" +
            "- **finding**: Share a discovery relevant to the team\n" +
            "- **request**: Ask for help or information\n" +
            "- **claim**: Declare responsibility for a file/component (prevents conflicts)\n" +
            "- **response**: Reply to a request\n" +
            "- **status**: Progress update\n\n" +
            "## Rules\n" +
            "- Read the scratchpad BEFORE modifying files\n" +
            "- Post a **claim** for files you plan to modify extensively\n" +
            "- Post a **finding** when you discover something that affects others\n" +
            "- Do NOT modify `_scratchpad.md` — it is engine-managed\n\n";

        public const string SubtaskCoordinatorBlock =
            "# SUBTASK COORDINATOR\n" +
            "You are a parent task that has spawned subtasks. Subtask results are delivered to you\n" +
            "via the message bus at `.agent-bus/inbox/` as JSON files with `\"type\": \"subtask_result\"`.\n\n" +
            "## Reading Subtask Results\n" +
            "Check `.agent-bus/inbox/` for files matching `*_subtask_result.json`.\n" +
            "Each result contains:\n" +
            "- `child_task_id`: The subtask identifier\n" +
            "- `status`: Whether the subtask Completed or Failed\n" +
            "- `summary`: A completion summary with file changes and duration\n" +
            "- `recommendations`: Any follow-up suggestions from the subtask\n" +
            "- `file_changes`: List of files modified with line counts\n\n" +
            "## Decision Protocol\n" +
            "After reading subtask results:\n" +
            "1. Assess whether each subtask succeeded and met its objectives\n" +
            "2. If a subtask failed or has critical recommendations, decide whether to:\n" +
            "   - Retry the work yourself\n" +
            "   - Report the failure in your own output\n" +
            "3. If all subtasks succeeded, integrate their results and finalize your work\n" +
            "4. Summarize what each subtask accomplished in your final output\n\n";

        public const string DecompositionPromptBlock =
            "# TASK DECOMPOSITION MODE\n" +
            "Before implementing anything, you MUST analyze this task and break it into smaller subtasks.\n\n" +
            "## Instructions\n" +
            "1. Read the task description carefully\n" +
            "2. Explore the codebase to understand the scope\n" +
            "3. Break the task into 2-5 independent, actionable subtasks\n" +
            "4. Each subtask should be completable by a single agent session\n" +
            "5. Identify dependencies between subtasks (which must finish before others can start)\n\n" +
            "## Output Format\n" +
            "Output your subtasks as a JSON array inside a ```SUBTASKS``` code block.\n" +
            "Each entry must have:\n" +
            "- `description`: A clear, self-contained prompt for the subtask\n" +
            "- `depends_on`: An array of zero-based indices of subtasks that must complete first (use [] for no dependencies)\n\n" +
            "Example:\n" +
            "```SUBTASKS\n" +
            "[\n" +
            "  {\"description\": \"Add the new data model class with validation\", \"depends_on\": []},\n" +
            "  {\"description\": \"Create the API endpoint for the new model\", \"depends_on\": [0]},\n" +
            "  {\"description\": \"Add unit tests for the model and endpoint\", \"depends_on\": [0, 1]}\n" +
            "]\n" +
            "```\n\n" +
            "IMPORTANT:\n" +
            "- Do NOT implement anything. Only produce the subtask list.\n" +
            "- Do NOT write, edit, or create any files.\n" +
            "- Each subtask description must be detailed enough to execute independently.\n" +
            "- Keep dependencies minimal — prefer parallel subtasks where possible.\n\n" +
            "---\n";

        public const string TeamDecompositionPromptBlock =
            "# TEAM SPAWN MODE\n" +
            "You are a team lead. Your job is to analyze this task and break it into roles for a team of concurrent agents.\n" +
            "Each agent will run independently with its own Claude session, coordinating via a shared message bus.\n\n" +
            "## Instructions\n" +
            "1. Read the task description carefully\n" +
            "2. Explore the codebase to understand the scope and architecture\n" +
            "3. Design a team of 2-5 specialist agents, each with a clear role and responsibility\n" +
            "4. Identify dependencies between team members (which must finish before others can start)\n" +
            "5. Prefer parallel work where possible — only add dependencies when truly required\n\n" +
            "## Output Format\n" +
            "Output your team plan as a JSON array inside a ```TEAM``` code block.\n" +
            "Each entry must have:\n" +
            "- `role`: A short role name (e.g. \"Architect\", \"Backend\", \"Frontend\", \"Tests\", \"Integration\")\n" +
            "- `description`: A clear, self-contained prompt for the agent. Include specific files, patterns, and acceptance criteria.\n" +
            "- `depends_on`: An array of zero-based indices of team members that must complete first (use [] for no dependencies)\n\n" +
            "Example:\n" +
            "```TEAM\n" +
            "[\n" +
            "  {\"role\": \"Architect\", \"description\": \"Design the data models and API interfaces for the new feature. Create interface files and type definitions.\", \"depends_on\": []},\n" +
            "  {\"role\": \"Backend\", \"description\": \"Implement the API endpoints and business logic using the interfaces defined by the Architect.\", \"depends_on\": [0]},\n" +
            "  {\"role\": \"Frontend\", \"description\": \"Build the UI components and connect them to the API endpoints.\", \"depends_on\": [0]},\n" +
            "  {\"role\": \"Tests\", \"description\": \"Write unit and integration tests for the backend and frontend.\", \"depends_on\": [1, 2]}\n" +
            "]\n" +
            "```\n\n" +
            "IMPORTANT:\n" +
            "- Do NOT implement anything. Only produce the team plan.\n" +
            "- Do NOT write, edit, or create any files.\n" +
            "- Each agent description must be detailed enough to execute independently.\n" +
            "- Agents will coordinate via a file-based message bus — mention that they should check for sibling work.\n" +
            "- Keep dependencies minimal — prefer parallel execution where possible.\n\n" +
            "---\n";

        public const string OvernightInitialTemplate =
            "You are running as an OVERNIGHT AUTONOMOUS TASK. This means you will be called repeatedly " +
            "to iterate on the work until it is complete. Follow these instructions carefully:\n\n" +
            "## CRITICAL RESTRICTIONS\n" +
            "These rules are ABSOLUTE and must NEVER be violated:\n" +
            "- **NO GIT COMMANDS.** Do not run git init, git add, git commit, git push, git checkout, " +
            "git branch, git merge, git rebase, git stash, git reset, git clone, or ANY other git command. " +
            "Git operations are forbidden in overnight mode.\n" +
            "- **NO OS-LEVEL MODIFICATIONS.** Do not modify system files, registry, environment variables, " +
            "PATH, system services, scheduled tasks, firewall rules, or anything outside the project directory. " +
            "Do not install, uninstall, or update system-wide packages, tools, or software.\n" +
            "- **STAY INSIDE THE PROJECT.** All file reads, writes, and edits must be within the project root. " +
            "Never access, create, or modify files outside of ./ under any circumstances.\n" +
            "- **NO DESTRUCTIVE OPERATIONS.** Do not delete directories recursively, reformat drives, " +
            "kill system processes, or perform any action that cannot be easily undone.\n\n" +
            "## Step 1: Create .overnight_log.md\n" +
            "Create a file called `.overnight_log.md` in the project root with:\n" +
            "- A **Checklist** of all sub-tasks needed to complete the work\n" +
            "- **Exit Criteria** that define when the task is truly done\n" +
            "- A **Progress Log** section where you append a dated entry each iteration\n\n" +
            "## Step 2: Implement\n" +
            "Work through the checklist step by step. Do as much as you can this iteration.\n\n" +
            "## Step 3: Investigate for flaws\n" +
            "After implementing, review your work critically. Look for bugs, edge cases, " +
            "missing error handling, incomplete features, and anything that doesn't match the requirements.\n\n" +
            "## Step 4: Verify checklist\n" +
            "Go through each checklist item and verify it is truly complete. " +
            "Update `.overnight_log.md` with your findings.\n\n" +
            "## Step 5: Status\n" +
            "End your response with EXACTLY one of these markers on its own line:\n" +
            "STATUS: COMPLETE\n" +
            "STATUS: NEEDS_MORE_WORK\n\n" +
            "Use COMPLETE only when ALL checklist items are done AND all exit criteria are met.\n\n" +
            "## THE TASK:\n";

        public const string OvernightContinuationTemplate =
            "You are continuing an OVERNIGHT AUTONOMOUS TASK (iteration {0}/{1}).\n\n" +
            "## CRITICAL RESTRICTIONS (REMINDER)\n" +
            "- **NO GIT COMMANDS** — git is completely forbidden in overnight mode.\n" +
            "- **NO OS-LEVEL MODIFICATIONS** — do not touch system files, registry, environment variables, " +
            "PATH, services, or install/uninstall any system-wide software.\n" +
            "- **STAY INSIDE THE PROJECT** — all operations must remain within the project root directory.\n" +
            "- **NO DESTRUCTIVE OPERATIONS** — no recursive deletes, no killing processes, nothing irreversible.\n\n" +
            "## Step 1: Read context\n" +
            "Read `.overnight_log.md` to understand what has been done and what remains.\n\n" +
            "## Step 2: Investigate current state\n" +
            "Review the current implementation thoroughly:\n" +
            "- Look for bugs, edge cases, and incomplete work\n" +
            "- Check for code quality issues (dead code, inconsistencies, poor naming)\n" +
            "- Identify potential improvements that are **within the original task scope**\n" +
            "- Look for missing error handling, validation, or robustness issues\n" +
            "- Verify that recent changes didn't break existing functionality\n\n" +
            "## Step 3: Fix, improve, and harden\n" +
            "Address the issues you found. Prioritize in this order:\n" +
            "1. Fix any bugs or broken functionality first\n" +
            "2. Complete remaining unchecked items from the checklist\n" +
            "3. Improve code quality and robustness **within scope** — do not add unrelated features\n" +
            "4. Add missing edge case handling if relevant to the task\n\n" +
            "## Step 4: Suggest improvements\n" +
            "In `.overnight_log.md`, add a **Suggestions** section with ideas for further " +
            "improvements that are within the task scope but that you did not have time to implement. " +
            "Keep suggestions actionable and specific.\n\n" +
            "## Step 5: Verify all checklist items\n" +
            "Go through every checklist item and exit criterion. " +
            "Update `.overnight_log.md` with your progress and any new findings.\n\n" +
            "## Step 6: Status\n" +
            "End your response with EXACTLY one of these markers on its own line:\n" +
            "STATUS: COMPLETE\n" +
            "STATUS: NEEDS_MORE_WORK\n\n" +
            "Use COMPLETE only when ALL checklist items are done AND all exit criteria are met. " +
            "If you found improvements to make in Step 2-3, use NEEDS_MORE_WORK to continue iterating.\n\n" +
            "Continue working on the task now.";

        // ── Prompt Assembly ─────────────────────────────────────────

        public string BuildBasePrompt(string systemPrompt, string description, bool useMcp,
            bool isOvernight, bool extendedPlanning = false, bool noGitWrite = false,
            bool planOnly = false, string projectDescription = "",
            string projectRulesBlock = "",
            bool autoDecompose = false, bool spawnTeam = false,
            bool isGameProject = false)
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            var gameBlock = isGameProject ? GameRulesBlock : "";

            if (isOvernight)
                return descBlock + projectRulesBlock + gameBlock + OvernightInitialTemplate + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            var planOnlyBlock = planOnly ? PlanOnlyBlock : "";
            var gitBlock = noGitWrite ? NoGitWriteBlock : "";
            var decomposeBlock = autoDecompose ? DecompositionPromptBlock : "";
            var teamBlock = spawnTeam ? TeamDecompositionPromptBlock : "";
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + gameBlock + mcpBlock + planningBlock + planOnlyBlock + decomposeBlock + teamBlock + description;
        }

        public string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false)
        {
            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                description += "\n\n# Additional Instructions\n" + task.AdditionalInstructions;
            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsOvernight, task.ExtendedPlanning, task.NoGitWrite, task.PlanOnly, projectDescription, projectRulesBlock, task.AutoDecompose, task.SpawnTeam, isGameProject);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                basePrompt = $"# Task: {task.Summary}\n{basePrompt}";
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
            sb.Append("The user has attached the following image(s). ");
            sb.Append("Use the Read tool to view each image file before proceeding with the task.\n");
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
                   $"Write-Host '[AgenticEngine] Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host '[AgenticEngine] Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host '[AgenticEngine] Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{remoteFlag} --verbose\n" +
                   "if ($LASTEXITCODE -ne 0) { Write-Host \"`n[AgenticEngine] Claude exited with code $LASTEXITCODE\" -ForegroundColor Yellow }\n" +
                   "Write-Host \"`n[AgenticEngine] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
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

        public string BuildOvernightContinuationPrompt(int iteration, int maxIterations)
        {
            return string.Format(OvernightContinuationTemplate, iteration, maxIterations);
        }

        public string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DEPENDENCY CONTEXT");
            sb.AppendLine("The following tasks were completed before this task started. Use their results to inform your work.\n");

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
