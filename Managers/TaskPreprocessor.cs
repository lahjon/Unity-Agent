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

        private const string PreprocessPrompt = """
You are a task pre-processor for an AI coding assistant. Analyze the user's task and produce:

1. **header**: A 2-5 word title summarizing the task (e.g. "Fix Auth Bug", "Add Dark Mode", "Refactor DB Layer", "Update Unit Tests")
2. **enhanced_prompt**: An improved, clearer version of the user's prompt. Keep the original intent but make it more precise and actionable. Do NOT add requirements the user didn't ask for. If the prompt is already clear, keep it mostly as-is.
3. **Toggle recommendations** based on task scope:

TOGGLE RULES (apply these heuristics):
- apply_fix (default: true) — Almost all tasks should be apply_fix=true. This is the standard "make changes and apply them" mode. Only set false for pure research/exploration tasks that don't need code changes.
- extended_planning (default: false) — Set true ONLY for tasks that require significant architectural thinking: large refactors, new system design, complex multi-file features. Simple bug fixes, small features, and code changes do NOT need this.
- feature_mode (default: false) — Set true ONLY for large multi-step features that need iterative implementation with verification cycles. Most tasks do NOT need this. Only for things like "implement full authentication system" or "build new dashboard page".
- auto_decompose (default: false) — Set true ONLY for very large tasks that should be broken into independent subtasks. Rarely needed.
- spawn_team (default: false) — Set true ONLY when auto_decompose is true AND the subtasks are truly independent and parallelizable.
- use_mcp (default: false) — Set true ONLY if the task explicitly mentions Unity, game engine interaction, or MCP tools.
- iterations (default: 2) — Number of feature mode iterations. Only relevant when feature_mode=true. Use 2-3 for medium features, 4-5 for large ones.

IMPORTANT: Most tasks are simple fixes, small features, or code changes. Default to apply_fix=true with everything else false. Only escalate toggles when the task clearly warrants it.

USER TASK:
{0}
""";

        public async Task<PreprocessResult?> PreprocessAsync(
            string taskDescription, CancellationToken ct = default)
        {
            try
            {
                var prompt = string.Format(PreprocessPrompt, taskDescription);

                var psi = new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = $"-p --output-format json --model {AppConstants.ClaudeHaiku} --max-turns 1 --output-schema '{PreprocessJsonSchema}'",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                psi.Environment.Remove("CLAUDECODE");

                using var process = new Process { StartInfo = psi };
                process.Start();

                await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
                process.StandardInput.Close();

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                var text = Helpers.FormatHelpers.StripAnsiCodes(output).Trim();

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                var header = root.GetProperty("header").GetString() ?? "";
                // Enforce 2-5 words
                var words = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 5)
                    header = string.Join(' ', words[..5]);

                return new PreprocessResult
                {
                    Header = header,
                    EnhancedPrompt = root.GetProperty("enhanced_prompt").GetString() ?? taskDescription,
                    ApplyFix = root.GetProperty("apply_fix").GetBoolean(),
                    ExtendedPlanning = root.GetProperty("extended_planning").GetBoolean(),
                    IsFeatureMode = root.GetProperty("feature_mode").GetBoolean(),
                    AutoDecompose = root.GetProperty("auto_decompose").GetBoolean(),
                    SpawnTeam = root.GetProperty("spawn_team").GetBoolean(),
                    UseMcp = root.GetProperty("use_mcp").GetBoolean(),
                    Iterations = root.TryGetProperty("iterations", out var iter) ? iter.GetInt32() : 2
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
        /// Applies preprocessor results to an AgentTask, overriding toggle values.
        /// Does NOT override toggles that the user explicitly set in the UI.
        /// </summary>
        public static void ApplyToTask(AgentTask task, PreprocessResult result)
        {
            task.Header = result.Header;
            task.Description = result.EnhancedPrompt;
            task.ApplyFix = result.ApplyFix;
            task.ExtendedPlanning = result.ExtendedPlanning;
            task.IsFeatureMode = result.IsFeatureMode;
            task.AutoDecompose = result.AutoDecompose;
            task.SpawnTeam = result.SpawnTeam;
            task.UseMcp = result.UseMcp;
            if (result.IsFeatureMode)
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
            sb.AppendLine($"  No Git Write:      {(task.NoGitWrite ? "ON" : "OFF")}");
            sb.AppendLine($"  Message Bus:       {(task.UseMessageBus ? "ON" : "OFF")}");
            sb.AppendLine($"  Remote Session:    {(task.RemoteSession ? "ON" : "OFF")}");
            sb.AppendLine($"  Plan Only:         {(task.PlanOnly ? "ON" : "OFF")}");
            sb.AppendLine("─────────────────────────────────────────────");
            return sb.ToString();
        }
    }
}
