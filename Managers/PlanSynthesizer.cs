using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers
{
    /// <summary>
    /// Merges findings from 3 parallel perspective agents (Architecture, Testing, Edge Cases)
    /// into a single consolidated plan using a Haiku API call.
    /// </summary>
    public class PlanSynthesizer
    {
        private readonly ClaudeService _claudeService;

        private static readonly string SynthesisTemplate = PromptLoader.Load("Teams/PlanSynthesisTemplate.md");

        private const string SynthesisSchema = """
            {
                "type": "object",
                "properties": {
                    "approach": {
                        "type": "string",
                        "description": "Consolidated implementation approach merging all perspectives"
                    },
                    "key_files": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "Critical files that need modification"
                    },
                    "risks": {
                        "type": "array",
                        "items": { "type": "string" },
                        "description": "Top risks identified across all perspectives"
                    },
                    "test_requirements": {
                        "type": "string",
                        "description": "Testing strategy synthesized from testing perspective"
                    },
                    "edge_case_mitigations": {
                        "type": "string",
                        "description": "Key edge cases and their mitigations"
                    }
                },
                "required": ["approach", "key_files", "risks", "test_requirements", "edge_case_mitigations"]
            }
            """;

        public PlanSynthesizer(ClaudeService claudeService)
        {
            _claudeService = claudeService;
        }

        /// <summary>
        /// Synthesizes 3 perspective outputs into a consolidated plan via Haiku.
        /// Returns formatted synthesis text, or null on failure.
        /// </summary>
        public async Task<string?> SynthesizeAsync(
            string architectureFindings,
            string testingFindings,
            string edgeCaseFindings,
            string featureDescription,
            CancellationToken ct = default)
        {
            if (!_claudeService.IsConfigured)
            {
                AppLogger.Warn("PlanSynthesizer", "Claude API not configured — skipping synthesis");
                return FallbackSynthesis(architectureFindings, testingFindings, edgeCaseFindings);
            }

            var prompt = string.Format(SynthesisTemplate,
                featureDescription, architectureFindings, testingFindings, edgeCaseFindings);

            try
            {
                var result = await _claudeService.SendStructuredHaikuAsync(prompt, SynthesisSchema, ct);
                if (result == null)
                {
                    AppLogger.Warn("PlanSynthesizer", "Haiku returned null — using fallback synthesis");
                    return FallbackSynthesis(architectureFindings, testingFindings, edgeCaseFindings);
                }

                return FormatSynthesisResult(result.Value);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Warn("PlanSynthesizer", $"Synthesis failed: {ex.Message}");
                return FallbackSynthesis(architectureFindings, testingFindings, edgeCaseFindings);
            }
        }

        private static string FormatSynthesisResult(JsonElement result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# SYNTHESIZED PLAN (merged from Architecture + Testing + Edge Cases)");
            sb.AppendLine();

            if (result.TryGetProperty("approach", out var approach))
            {
                sb.AppendLine("## Approach");
                sb.AppendLine(approach.GetString());
                sb.AppendLine();
            }

            if (result.TryGetProperty("key_files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Key Files");
                foreach (var file in files.EnumerateArray())
                    sb.AppendLine($"- {file.GetString()}");
                sb.AppendLine();
            }

            if (result.TryGetProperty("risks", out var risks) && risks.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("## Risks");
                foreach (var risk in risks.EnumerateArray())
                    sb.AppendLine($"- {risk.GetString()}");
                sb.AppendLine();
            }

            if (result.TryGetProperty("test_requirements", out var tests))
            {
                sb.AppendLine("## Test Requirements");
                sb.AppendLine(tests.GetString());
                sb.AppendLine();
            }

            if (result.TryGetProperty("edge_case_mitigations", out var edgeCases))
            {
                sb.AppendLine("## Edge Case Mitigations");
                sb.AppendLine(edgeCases.GetString());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Simple concatenation fallback when Haiku API is unavailable.
        /// </summary>
        private static string FallbackSynthesis(string architecture, string testing, string edgeCases)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# SYNTHESIZED PLAN (concatenated — API unavailable)");
            sb.AppendLine();
            sb.AppendLine("## Architecture Findings");
            sb.AppendLine(TruncateIfNeeded(architecture, 3000));
            sb.AppendLine();
            sb.AppendLine("## Testing Findings");
            sb.AppendLine(TruncateIfNeeded(testing, 3000));
            sb.AppendLine();
            sb.AppendLine("## Edge Case Findings");
            sb.AppendLine(TruncateIfNeeded(edgeCases, 3000));
            return sb.ToString();
        }

        private static string TruncateIfNeeded(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return "(no findings)";
            return text.Length <= maxChars ? text : text[..maxChars] + "\n[truncated]";
        }
    }
}
