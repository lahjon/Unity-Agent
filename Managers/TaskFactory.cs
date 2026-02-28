using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
{
    public class TaskFactory : ITaskFactory
    {
        // ── Validation ───────────────────────────────────────────────

        public bool ValidateTaskInput(string? description)
        {
            return !string.IsNullOrWhiteSpace(description);
        }

        // ── Task Creation ────────────────────────────────────────────

        public AgentTask CreateTask(
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
                AutoDecompose = autoDecompose,
                Model = model,
                MaxIterations = 50,
                ProjectPath = projectPath,
                ParentTaskId = parentTaskId
            };
            if (imagePaths != null)
                task.ImagePaths.AddRange(imagePaths);
            return task;
        }

        // ── Overnight Preparation ────────────────────────────────────

        public void PrepareTaskForOvernightStart(AgentTask task)
        {
            task.SkipPermissions = true;
            task.CurrentIteration = 1;
            task.ConsecutiveFailures = 0;
            task.LastIterationOutputStart = 0;
        }

        // ── Summary Generation ──────────────────────────────────────

        private static readonly string[] SummaryPrefixesToStrip =
        {
            "please ", "can you ", "could you ", "i want you to ", "i need you to ",
            "you should ", "i'd like you to ", "go ahead and "
        };

        public string GenerateLocalSummary(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return "";

            var line = description.Trim();
            if (line.Length == 0)
                return "";

            var lower = line.ToLowerInvariant();
            foreach (var prefix in SummaryPrefixesToStrip)
            {
                if (lower.StartsWith(prefix, StringComparison.Ordinal))
                {
                    line = line[prefix.Length..].TrimStart();
                    break;
                }
            }

            if (line.Length > 0)
                line = char.ToUpper(line[0]) + line[1..];

            return line;
        }

        // ── Project Description Generation ──────────────────────────

        public async Task<(string Short, string Long)> GenerateProjectDescriptionAsync(
            string projectPath, CancellationToken cancellationToken = default)
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

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Reached max turns", StringComparison.OrdinalIgnoreCase))
                    return ("", "");

                var parts = text.Split("---SEPARATOR---", 2, StringSplitOptions.TrimEntries);

                var shortDesc = parts.Length > 0 ? CleanDescriptionSection(parts[0]) : "";
                var longDesc = parts.Length > 1 ? CleanDescriptionSection(parts[1]) : shortDesc;

                if (shortDesc.Length > Constants.AppConstants.MaxShortDescriptionLength)
                    shortDesc = shortDesc[..Constants.AppConstants.MaxShortDescriptionLength];

                return (shortDesc, longDesc);
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) { AppLogger.Debug("TaskFactory", $"Failed to kill process on cancellation: {ex.Message}"); }
                return ("", "");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskFactory", "Failed to generate project description", ex);
                return ("", "");
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static string CleanDescriptionSection(string text)
        {
            var cleaned = text.Trim();
            cleaned = Regex.Replace(cleaned, @"^(?:SECTION\s*\d+\s*[-:–]\s*)?(?:SHORT|LONG)\s+DESCRIPTION\s*[:–\-]?\s*", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"^#{1,3}\s+.*\n", "", RegexOptions.Multiline);
            if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"')
                cleaned = cleaned[1..^1];
            return cleaned.Trim();
        }
    }
}
