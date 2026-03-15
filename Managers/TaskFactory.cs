using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers
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
            bool headless,
            bool isTeamsMode,
            bool ignoreFileLocks,
            bool useMcp,
            bool spawnTeam = false,
            bool extendedPlanning = false,
            bool planOnly = false,
            bool useMessageBus = false,
            List<string>? imagePaths = null,
            ModelType model = ModelType.ClaudeCode,
            string? parentTaskId = null,
            bool autoDecompose = false,
            bool applyFix = true,
            bool useAutoMode = true,
            bool allowFeatureModeInference = true)
        {
            var task = new AgentTask
            {
                Description = description,
                SkipPermissions = skipPermissions,
                Headless = headless,
                IsTeamsMode = isTeamsMode,
                IgnoreFileLocks = ignoreFileLocks,
                UseMcp = useMcp,
                SpawnTeam = spawnTeam,
                ExtendedPlanning = extendedPlanning,
                PlanOnly = planOnly,
                UseMessageBus = useMessageBus,
                AutoDecompose = autoDecompose,
                ApplyFix = applyFix,
                UseAutoMode = useAutoMode,
                AllowTeamsModeInference = allowFeatureModeInference,
                Model = model,
                MaxIterations = 2,
                ProjectPath = projectPath,
                ParentTaskId = parentTaskId
            };
            if (imagePaths != null)
                task.ImagePaths.AddRange(imagePaths);
            return task;
        }

        // ── Teams Mode Preparation ────────────────────────────────────

        public void PrepareTaskForFeatureModeStart(AgentTask task)
        {
            task.SkipPermissions = true;
            task.CurrentIteration = 1;
            task.ConsecutiveFailures = 0;
            task.LastIterationOutputStart = 0;
            task.TeamsModePhase = TeamsModePhase.None;
            task.TeamsPhaseChildIds.Clear();
            if (string.IsNullOrEmpty(task.OriginalTeamsDescription))
                task.OriginalTeamsDescription = task.Description;
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

        /// <summary>JSON schema for structured project description output.</summary>
        private const string DescriptionJsonSchema =
            """{"type":"object","properties":{"short":{"type":"string"},"long":{"type":"string"}},"required":["short","long"]}""";

        public async Task<(string Short, string Long)> GenerateProjectDescriptionAsync(
            string projectPath, CancellationToken cancellationToken = default, bool? isGameProject = null)
        {
            // Check if this is a game project - use parameter if provided, otherwise detect
            var isGame = isGameProject ?? CheckIfGameProject(projectPath);

            Process? process = null;
            try
            {
                var maxTurns = isGame ? 5 : 3;
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --model {Constants.AppConstants.ClaudeSonnet} --max-turns {maxTurns} --output-format json --json-schema \"{DescriptionJsonSchema.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = projectPath
                };
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

                process = new Process { StartInfo = psi };
                process.Start();
                JobObjectManager.Instance.AssignProcess(process);

                string prompt;
                if (isGame)
                {
                    prompt = PromptBuilder.GameProjectExplorationPrompt;
                }
                else
                {
                    prompt = PromptBuilder.CodebaseExplorationPrompt;
                }

                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Reached max turns", StringComparison.OrdinalIgnoreCase))
                    return ("", "");

                string shortDesc = "", longDesc = "";

                // Try structured JSON parse first (constrained output mode)
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;

                    // The CLI with --output-format json + --json-schema puts structured data
                    // in "structured_output"; fall back to "result" for plain text responses
                    JsonElement data;
                    if (root.TryGetProperty("structured_output", out var structured)
                        && structured.ValueKind == JsonValueKind.Object)
                    {
                        data = structured;
                    }
                    else if (root.TryGetProperty("result", out var resultElement))
                    {
                        if (resultElement.ValueKind == JsonValueKind.String)
                        {
                            using var innerDoc = JsonDocument.Parse(resultElement.GetString()!);
                            data = innerDoc.RootElement.Clone();
                        }
                        else
                        {
                            data = resultElement;
                        }
                    }
                    else
                    {
                        data = root;
                    }

                    shortDesc = CleanDescriptionSection(data.GetProperty("short").GetString() ?? "");
                    longDesc = CleanDescriptionSection(data.GetProperty("long").GetString() ?? "");
                }
                catch (JsonException)
                {
                    // Fallback: parse <short>/<long> tags
                    var shortMatch = Regex.Match(text, @"<short>\s*(.*?)\s*</short>", RegexOptions.Singleline);
                    var longMatch = Regex.Match(text, @"<long>\s*(.*?)\s*</long>", RegexOptions.Singleline);

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

        public async Task<string> GenerateClaudeMdAsync(string projectPath, bool isGame, CancellationToken cancellationToken = default)
        {
            Process? process = null;
            try
            {
                var maxTurns = isGame ? 5 : 3;
                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --max-turns {maxTurns}",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = projectPath
                };
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

                process = new Process { StartInfo = psi };
                process.Start();
                JobObjectManager.Instance.AssignProcess(process);

                await process.StandardInput.WriteAsync(PromptBuilder.ClaudeMdGenerationPrompt);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                if (string.IsNullOrWhiteSpace(text) ||
                    text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Reached max turns", StringComparison.OrdinalIgnoreCase))
                    return "";

                // Strip code fences if the model wrapped its output
                text = Regex.Replace(text, @"^```(?:markdown|md)?\s*\n", "", RegexOptions.Multiline);
                text = Regex.Replace(text, @"\n```\s*$", "", RegexOptions.Multiline);

                return text.Trim();
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch { }
                return "";
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TaskFactory", "Failed to generate CLAUDE.md", ex);
                return "";
            }
            finally
            {
                process?.Dispose();
            }
        }

        private bool CheckIfGameProject(string projectPath)
        {
            try
            {
                // Check for Unity project markers
                if (Directory.Exists(Path.Combine(projectPath, "Assets")) &&
                    Directory.Exists(Path.Combine(projectPath, "ProjectSettings")))
                    return true;

                // Check for Unreal project
                if (Directory.GetFiles(projectPath, "*.uproject", SearchOption.TopDirectoryOnly).Any())
                    return true;

                // Check for Godot project
                if (File.Exists(Path.Combine(projectPath, "project.godot")))
                    return true;

                // Check for GameMaker
                if (Directory.GetFiles(projectPath, "*.yyp", SearchOption.TopDirectoryOnly).Any())
                    return true;

                // Check for Construct 3
                if (Directory.GetFiles(projectPath, "*.c3p", SearchOption.TopDirectoryOnly).Any())
                    return true;

                // Check for RPG Maker
                if (File.Exists(Path.Combine(projectPath, "Game.rpgproject")) ||
                    File.Exists(Path.Combine(projectPath, "Game.rvproj2")))
                    return true;
            }
            catch
            {
                // If any check fails, default to false
            }

            return false;
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
