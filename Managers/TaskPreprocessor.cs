using System;
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
        public bool IsTeamsMode { get; set; }
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
            """{"type":"object","properties":{"header":{"type":"string"},"enhanced_prompt":{"type":"string"},"apply_fix":{"type":"boolean"},"extended_planning":{"type":"boolean"},"teams_mode":{"type":"boolean"},"auto_decompose":{"type":"boolean"},"spawn_team":{"type":"boolean"},"use_mcp":{"type":"boolean"},"iterations":{"type":"integer"}},"required":["header","enhanced_prompt","apply_fix","extended_planning","teams_mode","auto_decompose","spawn_team","use_mcp","iterations"]}""";

        private static readonly string PreprocessPrompt = PromptLoader.Load("TaskPreprocessorPrompt.md");

        private readonly ClaudeService? _claudeService;

        public TaskPreprocessor(ClaudeService? claudeService = null)
        {
            _claudeService = claudeService;
        }

        public async Task<PreprocessResult?> PreprocessAsync(
            string taskDescription, CancellationToken ct = default)
        {
            try
            {
                var prompt = string.Format(PreprocessPrompt, taskDescription);
                var sentPrompt = prompt;

                JsonElement data;

                // Prefer direct API call; fall back to CLI if ClaudeService unavailable
                if (_claudeService?.IsConfigured == true)
                {
                    AppLogger.Debug("TaskPreprocessor", $"Using direct API call. Prompt length: {prompt.Length}");
                    var result = await _claudeService.SendStructuredHaikuAsync(prompt, PreprocessJsonSchema, ct);
                    if (result is null)
                    {
                        AppLogger.Debug("TaskPreprocessor", "Direct API returned null, falling back to CLI");
                        var cliResult = await FeatureSystemCliRunner.RunAsync(
                            prompt, PreprocessJsonSchema, "TaskPreprocessor", TimeSpan.FromMinutes(2), ct);
                        if (cliResult is null) return null;
                        data = cliResult.Value;
                    }
                    else
                    {
                        data = result.Value;
                    }
                }
                else
                {
                    AppLogger.Debug("TaskPreprocessor", "ClaudeService not available, using CLI");
                    var cliResult = await FeatureSystemCliRunner.RunAsync(
                        prompt, PreprocessJsonSchema, "TaskPreprocessor", TimeSpan.FromMinutes(2), ct);
                    if (cliResult is null) return null;
                    data = cliResult.Value;
                }

                var header = data.TryGetProperty("header", out var h) ? h.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(header))
                {
                    var descWords = taskDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    header = string.Join(' ', descWords.Length > 4 ? descWords[..4] : descWords);
                    if (header.Length > 40)
                        header = header[..40].TrimEnd();
                }
                var words = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 5)
                    header = string.Join(' ', words[..5]);

                return new PreprocessResult
                {
                    Header = header,
                    EnhancedPrompt = data.TryGetProperty("enhanced_prompt", out var ep) ? ep.GetString() ?? taskDescription : taskDescription,
                    ApplyFix = !data.TryGetProperty("apply_fix", out var af) || af.GetBoolean(),
                    ExtendedPlanning = data.TryGetProperty("extended_planning", out var xp) && xp.GetBoolean(),
                    IsTeamsMode = data.TryGetProperty("teams_mode", out var fm) && fm.GetBoolean(),
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
            if (!task.IsTeamsMode && task.AllowTeamsModeInference)
                task.IsTeamsMode = result.IsTeamsMode;
            if (!task.AutoDecompose)             // still at default (false)
                task.AutoDecompose = result.AutoDecompose;
            if (!task.SpawnTeam)                 // still at default (false)
                task.SpawnTeam = result.SpawnTeam;
            if (!task.UseMcp)                    // still at default (false)
                task.UseMcp = result.UseMcp;

            if (result.IsTeamsMode && task.IsTeamsMode)
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
            sb.AppendLine($"  Teams Mode:      {(task.IsTeamsMode ? $"ON (max {task.MaxIterations} iterations)" : "OFF")}");
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
