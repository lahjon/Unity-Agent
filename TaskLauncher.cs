using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityAgent
{
    public static class TaskLauncher
    {
        // ── Constants ────────────────────────────────────────────────

        public const string DefaultSystemPrompt =
            "# STRICT RULE " +
            "Never access, read, write, or reference any files outside the project root directory. " +
            "All operations must stay within ./ " +
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
            "## Step 2: Investigate\n" +
            "Review the current implementation for flaws, bugs, incomplete work, " +
            "and anything that doesn't match the original requirements.\n\n" +
            "## Step 3: Fix and improve\n" +
            "Address the issues you found. Continue working through unchecked items.\n\n" +
            "## Step 4: Verify all checklist items\n" +
            "Go through every checklist item and exit criterion. " +
            "Update `.overnight_log.md` with your progress.\n\n" +
            "## Step 5: Status\n" +
            "End your response with EXACTLY one of these markers on its own line:\n" +
            "STATUS: COMPLETE\n" +
            "STATUS: NEEDS_MORE_WORK\n\n" +
            "Use COMPLETE only when ALL checklist items are done AND all exit criteria are met.\n\n" +
            "Continue working on the task now.";

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
            List<string>? imagePaths = null)
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
                MaxIterations = 50,
                ProjectPath = projectPath
            };
            if (imagePaths != null)
                task.ImagePaths.AddRange(imagePaths);
            return task;
        }

        // ── Prompt Building ──────────────────────────────────────────

        public static string BuildBasePrompt(string systemPrompt, string description, bool useMcp, bool isOvernight, bool extendedPlanning = false)
        {
            if (isOvernight)
                return OvernightInitialTemplate + description;

            var mcpBlock = useMcp ? McpPromptBlock : "";
            var planningBlock = extendedPlanning ? ExtendedPlanningBlock : "";
            return systemPrompt + mcpBlock + planningBlock + description;
        }

        public static string BuildFullPrompt(string systemPrompt, AgentTask task)
        {
            var basePrompt = BuildBasePrompt(systemPrompt, task.Description, task.UseMcp, task.IsOvernight, task.ExtendedPlanning);
            if (!string.IsNullOrWhiteSpace(task.Summary))
                basePrompt = $"# Task: {task.Summary}\n{basePrompt}";
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
                   $"$prompt = Get-Content -Raw -LiteralPath '{promptFilePath}'\n" +
                   $"claude -p{skipFlag}{remoteFlag}{teamFlag} $prompt\n" +
                   "Write-Host \"`n[UnityAgent] Process finished. Press any key to close...\" -ForegroundColor Cyan\n" +
                   "$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')\n";
        }

        public static ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless)
        {
            if (headless)
            {
                return new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoExit -File \"{ps1FilePath}\"",
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

        public static async System.Threading.Tasks.Task<string> GenerateSummaryAsync(string description)
        {
            try
            {
                // Truncate very long descriptions for the summary call
                var input = description.Length > 500 ? description[..500] : description;

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "-p --max-turns 1 --output-format text",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");

                var process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(
                    $"Create a short title (3-8 words) for this task. Reply with ONLY the title, nothing else.\n\nTask: {input}");
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var summary = StripAnsi(output).Trim();
                // Strip quotes if the model wrapped it
                if (summary.StartsWith('"') && summary.EndsWith('"'))
                    summary = summary[1..^1].Trim();
                // Sanity: cap at 60 chars
                if (summary.Length > 60)
                    summary = summary[..60];

                return string.IsNullOrWhiteSpace(summary) ? "" : summary;
            }
            catch
            {
                return "";
            }
        }

        // ── Utilities ────────────────────────────────────────────────

        public static string StripAnsi(string text)
        {
            return Regex.Replace(text, @"\x1B(?:\[[0-9;]*[a-zA-Z]|\].*?(?:\x07|\x1B\\))", "");
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
            try { path = Path.GetFullPath(path); } catch { }
            return path.ToLowerInvariant();
        }
    }
}
