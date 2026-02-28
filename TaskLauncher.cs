using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine
{
    public static class TaskLauncher
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
            "# GAME CREATION RULES\n" +
            "This task involves creating a new game. Before writing any code, you MUST read the game rules file:\n" +
            "Read the file `Games/GAME_RULES.md` — it contains the exact interface, theme colors, UI patterns, " +
            "button templates, and registration steps you must follow.\n" +
            "Do NOT deviate from the patterns defined in that file. Follow them exactly.\n\n";

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

        public static string BuildMessageBusBlock(string taskId, List<(string id, string summary)> siblings)
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

        // ── Game Detection ──────────────────────────────────────────

        private static readonly string[] GameKeywords = { "game", "minigame", "mini-game" };

        public static bool IsGameCreationTask(string description)
        {
            var lower = description.ToLowerInvariant();
            bool mentionsGame = false;
            foreach (var kw in GameKeywords)
            {
                if (lower.Contains(kw)) { mentionsGame = true; break; }
            }
            if (!mentionsGame) return false;

            return lower.Contains("create") || lower.Contains("add") ||
                   lower.Contains("build") || lower.Contains("make") ||
                   lower.Contains("implement") || lower.Contains("new");
        }

        // ── Validation ───────────────────────────────────────────────

        public static bool ValidateTaskInput(string? description)
        {
            return !string.IsNullOrWhiteSpace(description);
        }

        // ── Task Creation ────────────────────────────────────────────

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
            ModelType model = ModelType.ClaudeCode)
        {
            var task = new AgentTask
            {
                Description = description,
                SkipPermissions = skipPermissions,
                RemoteSession = remoteSession,
                Headless = headless,
                IsOvernight = isOvernight,
                IgnoreFileLocks = ignoreFileLocks,
                UseMcp = useMcp,
                SpawnTeam = spawnTeam,
                ExtendedPlanning = extendedPlanning,
                NoGitWrite = noGitWrite,
                PlanOnly = planOnly,
                UseMessageBus = useMessageBus,
                Model = model,
                MaxIterations = 50,
                ProjectPath = projectPath
            };
            if (imagePaths != null)
                task.ImagePaths.AddRange(imagePaths);
            return task;
        }

        // ── Prompt Building ──────────────────────────────────────────

        public static string BuildBasePrompt(string systemPrompt, string description, bool useMcp, bool isOvernight, bool extendedPlanning = false, bool noGitWrite = false, bool planOnly = false, string projectDescription = "", string projectRulesBlock = "", bool isGameProject = false)
        {
            var descBlock = "";
            if (!string.IsNullOrWhiteSpace(projectDescription))
                descBlock = $"# PROJECT CONTEXT\n{projectDescription}\n\n";

            if (isOvernight)
                return descBlock + projectRulesBlock + OvernightInitialTemplate + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            var planOnlyBlock = planOnly ? PlanOnlyBlock : "";
            var gitBlock = noGitWrite ? NoGitWriteBlock : "";
            var gameBlock = (isGameProject || IsGameCreationTask(description)) ? GameRulesBlock : "";
            return descBlock + systemPrompt + gitBlock + projectRulesBlock + mcpBlock + planningBlock + planOnlyBlock + gameBlock + description;
        }

        public static string BuildFullPrompt(string systemPrompt, AgentTask task, string projectDescription = "", string projectRulesBlock = "", bool isGameProject = false)
        {
            var description = !string.IsNullOrEmpty(task.StoredPrompt) ? task.StoredPrompt : task.Description;
            var basePrompt = BuildBasePrompt(systemPrompt, description, task.UseMcp, task.IsOvernight, task.ExtendedPlanning, task.NoGitWrite, task.PlanOnly, projectDescription, projectRulesBlock, isGameProject);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                basePrompt = $"# Task: {task.Summary}\n{basePrompt}";
            if (!string.IsNullOrWhiteSpace(task.DependencyContext))
                basePrompt = $"{basePrompt}\n\n{task.DependencyContext}";
            return BuildPromptWithImages(basePrompt, task.ImagePaths);
        }

        public static string BuildPromptWithImages(string basePrompt, List<string> imagePaths)
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

        // ── Command & Script Building ────────────────────────────────

        public static string BuildClaudeCommand(bool skipPermissions, bool remoteSession, bool spawnTeam = false)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            var teamFlag = spawnTeam ? " --spawn-team" : "";
            return $"claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose --output-format stream-json $prompt";
        }

        public static string BuildPowerShellScript(string projectPath, string promptFilePath, string claudeCmd)
        {
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"$prompt = Get-Content -Raw -LiteralPath '{promptFilePath}'\n" +
                   $"{claudeCmd}\n";
        }

        public static string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath, bool skipPermissions, bool remoteSession, bool spawnTeam = false)
        {
            var skipFlag = skipPermissions ? " --dangerously-skip-permissions" : "";
            var remoteFlag = remoteSession ? " --remote" : "";
            var teamFlag = spawnTeam ? " --spawn-team" : "";
            return "$env:CLAUDECODE = $null\n" +
                   $"Set-Location -LiteralPath '{projectPath}'\n" +
                   $"Write-Host '[AgenticEngine] Project: {projectPath}' -ForegroundColor DarkGray\n" +
                   $"Write-Host '[AgenticEngine] Prompt:  {promptFilePath}' -ForegroundColor DarkGray\n" +
                   "Write-Host '[AgenticEngine] Starting Claude...' -ForegroundColor Cyan\n" +
                   "Write-Host ''\n" +
                   $"Get-Content -Raw -LiteralPath '{promptFilePath}' | claude -p{skipFlag}{remoteFlag}{teamFlag} --verbose\n" +
                   "if ($LASTEXITCODE -ne 0) { Write-Host \"`n[AgenticEngine] Claude exited with code $LASTEXITCODE\" -ForegroundColor Yellow }\n" +
                   "Write-Host \"`n[AgenticEngine] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
                   "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')\n";
        }

        public static ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless)
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

        // ── Overnight Helpers ────────────────────────────────────────

        public static void PrepareTaskForOvernightStart(AgentTask task)
        {
            task.SkipPermissions = true;
            task.CurrentIteration = 1;
            task.ConsecutiveFailures = 0;
            task.LastIterationOutputStart = 0;
        }

        public static string BuildOvernightContinuationPrompt(int iteration, int maxIterations)
        {
            return string.Format(OvernightContinuationTemplate, iteration, maxIterations);
        }

        public static bool CheckOvernightComplete(string output)
        {
            var lines = output.Split('\n');
            var start = Math.Max(0, lines.Length - 50);
            for (var i = lines.Length - 1; i >= start; i--)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == "STATUS: COMPLETE") return true;
                if (trimmed == "STATUS: NEEDS_MORE_WORK") return false;
            }
            return false;
        }

        public static bool IsTokenLimitError(string output)
        {
            var tail = output.Length > 3000 ? output[^3000..] : output;
            var lower = tail.ToLowerInvariant();
            return lower.Contains("rate limit") ||
                   lower.Contains("token limit") ||
                   lower.Contains("overloaded") ||
                   lower.Contains("529") ||
                   lower.Contains("capacity") ||
                   lower.Contains("too many requests");
        }

        // ── Summary Generation ──────────────────────────────────────

        private static readonly string[] SummaryPrefixesToStrip =
        {
            "please ", "can you ", "could you ", "i want you to ", "i need you to ",
            "you should ", "i'd like you to ", "go ahead and "
        };

        /// <summary>
        /// Generates a short title from the task description using a local heuristic
        /// (first line, stripped of filler prefixes, truncated at a word boundary).
        /// No external process is spawned.
        /// </summary>
        public static string GenerateLocalSummary(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return "";

            // Take first line only
            var line = description.Split('\n', '\r')[0].Trim();
            if (line.Length == 0)
                return "";

            // Strip common instruction prefixes
            var lower = line.ToLowerInvariant();
            foreach (var prefix in SummaryPrefixesToStrip)
            {
                if (lower.StartsWith(prefix, StringComparison.Ordinal))
                {
                    line = line[prefix.Length..].TrimStart();
                    break;
                }
            }

            // Capitalize first letter
            if (line.Length > 0)
                line = char.ToUpper(line[0]) + line[1..];

            // Truncate at word boundary within 40 chars
            if (line.Length > 40)
            {
                var cut = line[..40];
                var lastSpace = cut.LastIndexOf(' ');
                line = lastSpace > 15 ? cut[..lastSpace] : cut;
            }

            return line;
        }

        // ── Project Description Generation ──────────────────────────

        public static async System.Threading.Tasks.Task<(string Short, string Long)> GenerateProjectDescriptionAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "-p --max-turns 15 --output-format text",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = projectPath
                };
                psi.Environment.Remove("CLAUDECODE");

                process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(
                    "You are a codebase exploration agent. Your job is to thoroughly explore this project's actual source code before writing any description.\n\n" +
                    "STEP 1 — EXPLORE (use your tools, do NOT guess):\n" +
                    "- List the top-level directory structure\n" +
                    "- Read project config files (.csproj, package.json, Cargo.toml, etc.)\n" +
                    "- Read the main entry point and key source files\n" +
                    "- Identify the architecture, patterns, and major components from the actual code\n" +
                    "- Check for README or docs if they exist\n\n" +
                    "STEP 2 — After you have explored the codebase, respond with EXACTLY two sections separated by '---SEPARATOR---':\n\n" +
                    "SECTION 1 - SHORT DESCRIPTION (1-2 sentences, max 150 chars):\n" +
                    "A brief summary of what this project is and its tech stack, based on what you actually read.\n\n" +
                    "SECTION 2 - LONG DESCRIPTION (1-2 paragraphs):\n" +
                    "A detailed summary covering: project purpose, tech stack, architecture, " +
                    "key directories/files, main components, and any important patterns or conventions — all based on the actual code you explored.\n\n" +
                    "IMPORTANT: Do NOT describe the project based on assumptions or the project name alone. " +
                    "Base your descriptions entirely on what you found by reading the actual files.\n" +
                    "Output ONLY the two sections with the separator in your final response. No labels, no headers.");
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                var text = StripAnsi(output).Trim();

                // Detect error outputs (e.g. "Error: Reached max turns")
                if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Reached max turns", StringComparison.OrdinalIgnoreCase))
                    return ("", "");

                var parts = text.Split("---SEPARATOR---", 2, StringSplitOptions.TrimEntries);

                var shortDesc = parts.Length > 0 ? CleanDescriptionSection(parts[0]) : "";
                var longDesc = parts.Length > 1 ? CleanDescriptionSection(parts[1]) : shortDesc;

                // Cap short description at 200 chars
                if (shortDesc.Length > 200)
                    shortDesc = shortDesc[..200];

                return (shortDesc, longDesc);
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch { }
                return ("", "");
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Warn("TaskLauncher", "Failed to generate project description", ex);
                return ("", "");
            }
            finally
            {
                process?.Dispose();
            }
        }

        // ── Completion Summary ──────────────────────────────────────

        public static string? CaptureGitHead(string projectPath)
        {
            return RunGitCommand(projectPath, "rev-parse HEAD");
        }

        public static List<(string name, int added, int removed)>? GetGitFileChanges(string projectPath, string? gitStartHash)
        {
            var diffRef = gitStartHash ?? "HEAD";
            var numstatOutput = RunGitCommand(projectPath, $"diff {diffRef} --numstat");
            if (numstatOutput == null) return null;

            var files = new List<(string name, int added, int removed)>();
            foreach (var line in numstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added = parts[0] == "-" ? 0 : int.TryParse(parts[0], out var a) ? a : 0;
                var removed = parts[1] == "-" ? 0 : int.TryParse(parts[1], out var r) ? r : 0;
                files.Add((parts[2], added, removed));
            }
            return files;
        }

        public static string FormatCompletionSummary(
            AgentTaskStatus status,
            TimeSpan duration,
            List<(string name, int added, int removed)>? fileChanges)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine(" TASK COMPLETION SUMMARY");
            sb.AppendLine("───────────────────────────────────────────");
            sb.AppendLine($" Status: {status}");
            sb.AppendLine($" Duration: {(int)duration.TotalMinutes}m {duration.Seconds}s");

            if (fileChanges != null)
            {
                var totalAdded = 0;
                var totalRemoved = 0;
                foreach (var (_, added, removed) in fileChanges)
                {
                    totalAdded += added;
                    totalRemoved += removed;
                }

                sb.AppendLine($" Files modified: {fileChanges.Count}");
                sb.AppendLine($" Lines changed: +{totalAdded} / -{totalRemoved}");

                if (fileChanges.Count > 0)
                {
                    sb.AppendLine("───────────────────────────────────────────");
                    sb.AppendLine(" Modified files:");
                    foreach (var (name, added, removed) in fileChanges)
                        sb.AppendLine($"   {name} | +{added} -{removed}");
                }
            }
            else
            {
                sb.AppendLine(" Files modified: (git not available)");
            }

            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }

        // ── Recommendation Detection ────────────────────────────────

        private static readonly string[] RecommendationHeaders = {
            "next steps", "next step", "recommendations", "recommended next",
            "suggestions", "suggested next", "follow-up", "follow up",
            "remaining work", "future improvements", "action items",
            "things to consider", "to do next", "what's next"
        };

        // Phrases that indicate the task was fully completed
        private static readonly string[] CompletionIndicators = {
            "task is complete", "completed all", "all changes have been",
            "implementation is complete", "successfully completed",
            "everything is set up", "changes are complete",
            "all tasks are done", "have been implemented",
            "is now complete", "are now complete",
            "this completes", "that completes",
            "finished implementing", "all requested changes",
            "completed the", "i've made all", "i have made all",
            "everything is working", "everything works",
            "all the changes", "successfully implemented",
            "has been completed", "have been completed"
        };

        // Phrases that indicate the task is NOT done and needs more work
        private static readonly string[] IncompletionIndicators = {
            "remaining work", "couldn't complete", "wasn't able to",
            "could not complete", "still needs", "still need to",
            "not yet implemented", "not yet complete", "incomplete",
            "blocked by", "unable to complete", "failed to complete",
            "needs to be done", "need to be done", "todo", "to-do",
            "unfinished", "not finished", "partially"
        };

        /// <summary>
        /// Checks whether the output around a recommendation header indicates
        /// the task was fully completed (completion signals found, no incompletion signals).
        /// When true, the recommendations are optional suggestions and should be suppressed.
        /// </summary>
        internal static bool IsTaskOutputComplete(string[] lines, int recommendationLine)
        {
            var searchStart = Math.Max(0, recommendationLine - 20);
            var searchEnd = Math.Min(lines.Length, recommendationLine + 15);

            bool hasCompletion = false;
            bool hasIncompletion = false;

            for (int i = searchStart; i < searchEnd; i++)
            {
                var lower = lines[i].ToLowerInvariant();

                if (!hasCompletion)
                {
                    foreach (var indicator in CompletionIndicators)
                    {
                        if (lower.Contains(indicator)) { hasCompletion = true; break; }
                    }
                }

                if (!hasIncompletion)
                {
                    foreach (var indicator in IncompletionIndicators)
                    {
                        if (lower.Contains(indicator)) { hasIncompletion = true; break; }
                    }
                }
            }

            // Task is complete only if we found completion signals AND no incompletion signals
            return hasCompletion && !hasIncompletion;
        }

        public static string? ExtractRecommendations(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            // Strip the completion summary block if present
            var summaryIdx = output.LastIndexOf("═══════════════════════════════════════════", StringComparison.Ordinal);
            var text = summaryIdx > 0 ? output[..summaryIdx] : output;

            // Search the last ~4000 chars where recommendations typically appear
            var searchText = text.Length > 4000 ? text[^4000..] : text;
            var lines = searchText.Split('\n');

            var startLine = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var lower = lines[i].ToLowerInvariant().Trim();
                if (string.IsNullOrEmpty(lower)) continue;

                foreach (var header in RecommendationHeaders)
                {
                    if (lower.Contains(header))
                    {
                        startLine = i;
                        break;
                    }
                }
                if (startLine >= 0) break;
            }

            if (startLine < 0) return null;

            // If the output indicates the task is fully complete, the recommendations
            // are just optional suggestions — suppress the continue button.
            if (IsTaskOutputComplete(lines, startLine))
                return null;

            // Capture from the recommendation header, limit to ~10 lines
            var endLine = Math.Min(startLine + 10, lines.Length);
            var captured = new List<string>();
            for (int i = startLine; i < endLine; i++)
            {
                var line = lines[i].TrimEnd();
                if (string.IsNullOrEmpty(line) && captured.Count > 0 && string.IsNullOrEmpty(captured[^1]))
                    break; // stop at double blank
                captured.Add(line);
            }

            // Trim trailing blanks
            while (captured.Count > 0 && string.IsNullOrWhiteSpace(captured[^1]))
                captured.RemoveAt(captured.Count - 1);

            if (captured.Count == 0) return null;
            var result = string.Join("\n", captured).Trim();
            return result.Length > 0 ? result : null;
        }

        public static string GenerateCompletionSummary(string projectPath, string? gitStartHash, AgentTaskStatus status, TimeSpan duration)
        {
            try
            {
                var fileChanges = GetGitFileChanges(projectPath, gitStartHash);
                return FormatCompletionSummary(status, duration, fileChanges);
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("TaskLauncher", $"Failed to get git file changes for completion summary: {ex.Message}");
                return FormatCompletionSummary(status, duration, null);
            }
        }

        private static string? RunGitCommand(string workingDirectory, string arguments)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);
                if (process == null) return null;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                    Managers.AppLogger.Warn("TaskLauncher", $"Git command timed out: git {arguments}");
                    return null;
                }
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("TaskLauncher", $"Git command failed: {ex.Message}");
                return null;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public static async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken cancellationToken = default)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);
                if (process == null) return null;
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                using var timeoutCts = new CancellationTokenSource(5000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch { }
                Managers.AppLogger.Warn("TaskLauncher", $"Git command timed out or cancelled: git {arguments}");
                return null;
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("TaskLauncher", $"Async git command failed: {ex.Message}");
                return null;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public static async Task<string?> CaptureGitHeadAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(projectPath, "rev-parse HEAD", cancellationToken).ConfigureAwait(false);
        }

        public static async Task<string> GenerateCompletionSummaryAsync(string projectPath, string? gitStartHash, AgentTaskStatus status, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            try
            {
                var fileChanges = await GetGitFileChangesAsync(projectPath, gitStartHash, cancellationToken).ConfigureAwait(false);
                return FormatCompletionSummary(status, duration, fileChanges);
            }
            catch (OperationCanceledException)
            {
                return FormatCompletionSummary(status, duration, null);
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Debug("TaskLauncher", $"Failed to get git file changes for completion summary: {ex.Message}");
                return FormatCompletionSummary(status, duration, null);
            }
        }

        public static async Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(string projectPath, string? gitStartHash, CancellationToken cancellationToken = default)
        {
            var diffRef = gitStartHash ?? "HEAD";
            var numstatOutput = await RunGitCommandAsync(projectPath, $"diff {diffRef} --numstat", cancellationToken).ConfigureAwait(false);
            if (numstatOutput == null) return null;

            var files = new List<(string name, int added, int removed)>();
            foreach (var line in numstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                int.TryParse(parts[0], out var added);
                int.TryParse(parts[1], out var removed);
                files.Add((parts[2], added, removed));
            }
            return files.Count > 0 ? files : null;
        }

        // ── Dependency Context ───────────────────────────────────────

        public static string BuildDependencyContext(List<string> depIds, IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks)
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

        // ── Utilities ────────────────────────────────────────────────

        public static string StripAnsi(string text)
        {
            return Regex.Replace(text, @"\x1B(?:\[[0-9;]*[a-zA-Z]|\].*?(?:\x07|\x1B\\))", "");
        }

        private static string CleanDescriptionSection(string text)
        {
            var cleaned = text.Trim();
            // Strip common section label patterns Claude might include
            cleaned = Regex.Replace(cleaned, @"^(?:SECTION\s*\d+\s*[-:–]\s*)?(?:SHORT|LONG)\s+DESCRIPTION\s*[:–\-]?\s*", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^#{1,3}\s+.*\n", "", RegexOptions.Multiline);
            // Strip surrounding quotes
            if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"')
                cleaned = cleaned[1..^1];
            return cleaned.Trim();
        }

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
            try { path = Path.GetFullPath(path); } catch (Exception ex) { Managers.AppLogger.Debug("TaskLauncher", $"Path normalization failed for '{path}': {ex.Message}"); }
            return path.ToLowerInvariant();
        }
    }
}
