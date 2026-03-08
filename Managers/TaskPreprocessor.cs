using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Constants;

namespace Spritely.Managers
{
    /// <summary>
    /// Result of the Haiku pre-processing step: enhanced prompt, short header, and toggle recommendations.
    /// </summary>
    public class PreprocessResult
    {
        public string Header { get; set; } = "";
        public string EnhancedPrompt { get; set; } = "";
        public bool ApplyFix { get; set; } = true;
        public bool ExtendedPlanning { get; set; }
        public bool IsFeatureMode { get; set; }
        public bool AutoDecompose { get; set; }
        public bool SpawnTeam { get; set; }
        public bool UseMcp { get; set; }
        public int Iterations { get; set; } = 2;

        /// <summary>The formatted prompt that was sent to Haiku for preprocessing.</summary>
        public string SentPrompt { get; set; } = "";
    }

    /// <summary>
    /// Runs a fast Haiku pre-processing call before every task to:
    /// 1. Generate a 2-5 word header for display
    /// 2. Enhance/clarify the user prompt
    /// 3. Auto-detect the right toggles based on task scope
    /// </summary>
    public class TaskPreprocessor
    {
        private const string PreprocessJsonSchema =
            """{"type":"object","properties":{"header":{"type":"string"},"enhanced_prompt":{"type":"string"},"apply_fix":{"type":"boolean"},"extended_planning":{"type":"boolean"},"feature_mode":{"type":"boolean"},"auto_decompose":{"type":"boolean"},"spawn_team":{"type":"boolean"},"use_mcp":{"type":"boolean"},"iterations":{"type":"integer"}},"required":["header","enhanced_prompt","apply_fix","extended_planning","feature_mode","auto_decompose","spawn_team","use_mcp","iterations"]}""";

        private static readonly string PreprocessPrompt = PromptLoader.Load("TaskPreprocessorPrompt.md");

        public async Task<PreprocessResult?> PreprocessAsync(
            string taskDescription, CancellationToken ct = default)
        {
            try
            {
                var prompt = string.Format(PreprocessPrompt, taskDescription);
                var sentPrompt = prompt; // Capture for diagnostics

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 3 --json-schema \"{PreprocessJsonSchema.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                // Remove all Claude Code session env vars so the CLI doesn't detect a nested session
                psi.Environment.Remove("CLAUDECODE");
                psi.Environment.Remove("CLAUDE_CODE_SSE_PORT");
                psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

                using var process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();
                AppLogger.Debug("TaskPreprocessor", $"CLI raw output ({text.Length} chars): {(text.Length > 500 ? text[..500] + "..." : text)}");

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                // The CLI with --output-format json + --json-schema puts structured data
                // in "structured_output"; fall back to "result" for plain text responses
                JsonElement data;
                if (root.TryGetProperty("structured_output", out var structured)
                    && structured.ValueKind == JsonValueKind.Object)
                {
                    data = structured;
                    AppLogger.Debug("TaskPreprocessor", "Using structured_output path");
                }
                else if (root.TryGetProperty("result", out var resultElement))
                {
                    AppLogger.Debug("TaskPreprocessor", $"No structured_output; result kind={resultElement.ValueKind}");
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
                    AppLogger.Debug("TaskPreprocessor", "No structured_output or result; using root");
                    data = root;
                }

                var header = data.TryGetProperty("header", out var h) ? h.GetString() ?? "" : "";
                // Fallback: derive header from first 4 words of the task description
                if (string.IsNullOrWhiteSpace(header))
                {
                    AppLogger.Debug("TaskPreprocessor", "Header empty from CLI response, deriving from description");
                    var descWords = taskDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    header = string.Join(' ', descWords.Length > 4 ? descWords[..4] : descWords);
                    if (header.Length > 40)
                        header = header[..40].TrimEnd();
                }
                // Enforce 2-5 words
                var words = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 5)
                    header = string.Join(' ', words[..5]);

                return new PreprocessResult
                {
                    Header = header,
                    EnhancedPrompt = data.TryGetProperty("enhanced_prompt", out var ep) ? ep.GetString() ?? taskDescription : taskDescription,
                    ApplyFix = !data.TryGetProperty("apply_fix", out var af) || af.GetBoolean(),
                    ExtendedPlanning = data.TryGetProperty("extended_planning", out var xp) && xp.GetBoolean(),
                    IsFeatureMode = data.TryGetProperty("feature_mode", out var fm) && fm.GetBoolean(),
                    AutoDecompose = data.TryGetProperty("auto_decompose", out var ad) && ad.GetBoolean(),
                    SpawnTeam = data.TryGetProperty("spawn_team", out var st) && st.GetBoolean(),
                    UseMcp = data.TryGetProperty("use_mcp", out var um) && um.GetBoolean(),
                    Iterations = data.TryGetProperty("iterations", out var iter) ? iter.GetInt32() : 2,
                    SentPrompt = sentPrompt
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Debug("TaskPreprocessor", "Haiku pre-processing failed, using defaults", ex);
                return null;
            }
        }

        /// <summary>
        /// Applies preprocessor results to an AgentTask.
        /// Only overrides toggles the user left at their default value — if the user
        /// explicitly changed a toggle in the UI, that choice is preserved.
        /// </summary>
        public static void ApplyToTask(AgentTask task, PreprocessResult result)
        {
            task.Header = result.Header;
            task.Description = result.EnhancedPrompt;

            // Only override toggles still at their default values.
            // Defaults: ApplyFix=true, everything else=false.
            if (task.ApplyFix)                  // still at default (true)
                task.ApplyFix = result.ApplyFix;
            if (!task.ExtendedPlanning)          // still at default (false)
                task.ExtendedPlanning = result.ExtendedPlanning;
            if (!task.IsFeatureMode)             // still at default (false)
                task.IsFeatureMode = result.IsFeatureMode;
            if (!task.AutoDecompose)             // still at default (false)
                task.AutoDecompose = result.AutoDecompose;
            if (!task.SpawnTeam)                 // still at default (false)
                task.SpawnTeam = result.SpawnTeam;
            if (!task.UseMcp)                    // still at default (false)
                task.UseMcp = result.UseMcp;

            if (result.IsFeatureMode && task.IsFeatureMode)
                task.MaxIterations = Math.Max(2, result.Iterations);
        }

        /// <summary>
        /// Formats the active toggles for display in task output.
        /// </summary>
        public static string FormatActiveToggles(AgentTask task)
        {
            var sb = new StringBuilder();
            sb.AppendLine("── Active Toggles ──────────────────────────");
            sb.AppendLine($"  Apply Fix:         {(task.ApplyFix ? "ON" : "OFF")}");
            sb.AppendLine($"  Extended Planning: {(task.ExtendedPlanning ? "ON" : "OFF")}");
            sb.AppendLine($"  Feature Mode:      {(task.IsFeatureMode ? $"ON (max {task.MaxIterations} iterations)" : "OFF")}");
            sb.AppendLine($"  Auto Decompose:    {(task.AutoDecompose ? "ON" : "OFF")}");
            sb.AppendLine($"  Spawn Team:        {(task.SpawnTeam ? "ON" : "OFF")}");
            sb.AppendLine($"  Use MCP:           {(task.UseMcp ? "ON" : "OFF")}");
            sb.AppendLine($"  Message Bus:       {(task.UseMessageBus ? "ON" : "OFF")}");
            sb.AppendLine($"  Plan Only:         {(task.PlanOnly ? "ON" : "OFF")}");
            sb.AppendLine("─────────────────────────────────────────────");
            return sb.ToString();
        }
    }
}
