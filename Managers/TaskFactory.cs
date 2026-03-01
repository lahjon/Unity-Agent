using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
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
                    Arguments = "-p --max-turns 10 --output-format text",
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
                    "You are a codebase exploration agent. Explore this project's source code, then produce a token-efficient description.\n\n" +
                    "STEP 1 — EXPLORE (use your tools, do NOT guess):\n" +
                    "- List the top-level directory structure\n" +
                    "- Read project config files (.csproj, package.json, Cargo.toml, etc.)\n" +
                    "- Read the main entry point and key source files\n" +
                    "- Identify the architecture, patterns, and major components\n\n" +
                    "STEP 2 — Output EXACTLY this format:\n\n" +
                    "<short>\nOne-line summary: what it is + tech stack. Max 200 chars. No preamble.\n</short>\n\n" +
                    "<long>\nCompact bullet-point summary using this structure (keep each line terse, no filler words):\n" +
                    "- Purpose: [what the project does]\n" +
                    "- Stack: [languages, frameworks, runtime]\n" +
                    "- Architecture: [pattern, e.g. MVVM, MVC, microservices]\n" +
                    "- Key dirs: [top-level source directories]\n" +
                    "- Components: [major classes/modules and their roles]\n" +
                    "- Patterns: [notable conventions, DI, async, etc.]\n" +
                    "Max 800 characters total. Omit any section that has nothing notable. No preamble.\n</long>\n\n" +
                    "RULES:\n" +
                    "- Base descriptions on actual files read, not assumptions.\n" +
                    "- Output ONLY <short> and <long> tags. Nothing else.\n" +
                    "- No conversational text, preamble, or commentary.\n" +
                    "- Write factual project summaries, not agent responses.");
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Reached max turns", StringComparison.OrdinalIgnoreCase))
                    return ("", "");

                // Parse <short> and <long> tags
                var shortMatch = Regex.Match(text, @"<short>\s*(.*?)\s*</short>", RegexOptions.Singleline);
                var longMatch = Regex.Match(text, @"<long>\s*(.*?)\s*</long>", RegexOptions.Singleline);

                string shortDesc, longDesc;
                if (shortMatch.Success)
                {
                    shortDesc = CleanDescriptionSection(shortMatch.Groups[1].Value);
                    longDesc = longMatch.Success ? CleanDescriptionSection(longMatch.Groups[1].Value) : shortDesc;
                }
                else
                {
                    // Fallback: try old separator format
                    var parts = text.Split("---SEPARATOR---", 2, StringSplitOptions.TrimEntries);
                    shortDesc = parts.Length > 0 ? CleanDescriptionSection(parts[0]) : "";
                    longDesc = parts.Length > 1 ? CleanDescriptionSection(parts[1]) : shortDesc;
                }

                if (shortDesc.Length > Constants.AppConstants.MaxShortDescriptionLength)
                    shortDesc = shortDesc[..Constants.AppConstants.MaxShortDescriptionLength];

                if (longDesc.Length > Constants.AppConstants.MaxLongDescriptionLength)
                    longDesc = longDesc[..Constants.AppConstants.MaxLongDescriptionLength];

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

        private static readonly Regex[] DescriptionPreamblePatterns =
        {
            // Section labels: "SECTION 1 - SHORT DESCRIPTION:"
            new(@"^(?:SECTION\s*\d+\s*[-:–]\s*)?(?:SHORT|LONG)\s+DESCRIPTION\s*[:–\-]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Markdown headers
            new(@"^#{1,3}\s+.*\n", RegexOptions.Multiline | RegexOptions.Compiled),
            // AI preamble: "Based on my exploration,", "After reviewing the codebase,", "Here is the description:", etc.
            new(@"^(?:based on (?:my|the) (?:exploration|review|analysis|reading)[^.]*[.,]\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^(?:after (?:exploring|reviewing|analyzing|reading|examining)[^.]*[.,]\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^(?:(?:here(?:'s| is| are)|I(?:'ve| have) (?:found|discovered|identified|analyzed|explored))[^:]*[:.]?\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^(?:from (?:my|the) (?:exploration|review|analysis)[^:]*[:.]?\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^(?:having (?:explored|reviewed|analyzed|examined)[^,]*,\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^(?:upon (?:exploration|review|analysis)[^,]*,\s*)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // "This is a..." / "The project is a..." opening filler (only when followed by description content)
            new(@"^(?:this (?:project |codebase )?is (?:a |an )?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static readonly Regex[] DescriptionPostamblePatterns =
        {
            // "Let me know if..." / "Feel free to..." trailing text
            new(@"\s*(?:let me know|feel free|if you (?:need|want|have)|don't hesitate|please let).*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static string CleanDescriptionSection(string text)
        {
            var cleaned = text.Trim();

            foreach (var pattern in DescriptionPreamblePatterns)
                cleaned = pattern.Replace(cleaned, "");

            foreach (var pattern in DescriptionPostamblePatterns)
                cleaned = pattern.Replace(cleaned, "");

            // Strip surrounding quotes
            if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"')
                cleaned = cleaned[1..^1];

            // Ensure first character is capitalized after stripping
            cleaned = cleaned.Trim();
            if (cleaned.Length > 0 && char.IsLower(cleaned[0]))
                cleaned = char.ToUpper(cleaned[0]) + cleaned[1..];

            return cleaned;
        }
    }
}
