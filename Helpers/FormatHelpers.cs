using System;
using System.IO;
using System.Text.RegularExpressions;
using Spritely.Constants;

namespace Spritely.Helpers
{
    public static class FormatHelpers
    {
        public static string FormatTokenCount(long count)
        {
            if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000) return $"{count / 1_000.0:F1}K";
            return count.ToString();
        }

        /// <summary>
        /// Estimates cost in USD using rates from AppConstants, dispatching on model name.
        /// Falls back to Opus pricing when model is null/unknown (conservative estimate).
        /// </summary>
        public static decimal EstimateCost(long inputTokens, long outputTokens,
            long cacheReadTokens = 0, long cacheCreationTokens = 0, string? model = null)
        {
            var (inputPerM, outputPerM, cacheReadPerM, cacheCreatePerM) = GetRatesForModel(model);

            return (inputTokens * inputPerM
                  + outputTokens * outputPerM
                  + cacheReadTokens * cacheReadPerM
                  + cacheCreationTokens * cacheCreatePerM) / 1_000_000m;
        }

        private static (decimal input, decimal output, decimal cacheRead, decimal cacheCreate) GetRatesForModel(string? model)
        {
            if (model != null)
            {
                if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                    return (AppConstants.HaikuInputPerM, AppConstants.HaikuOutputPerM,
                            AppConstants.HaikuCacheReadPerM, AppConstants.HaikuCacheCreationPerM);

                if (model.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
                    return (AppConstants.SonnetInputPerM, AppConstants.SonnetOutputPerM,
                            AppConstants.SonnetCacheReadPerM, AppConstants.SonnetCacheCreationPerM);
            }

            // Default to Opus (most expensive = conservative)
            return (AppConstants.OpusInputPerM, AppConstants.OpusOutputPerM,
                    AppConstants.OpusCacheReadPerM, AppConstants.OpusCacheCreationPerM);
        }

        public static string FormatCost(decimal cost)
        {
            if (cost >= 1.00m) return $"${cost:F2}";
            if (cost >= 0.01m) return $"${cost:F2}";
            return $"${cost:F3}";
        }

        public static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        private static readonly Regex AnsiRegex = new(
            @"\x1B(?:\[[0-9;]*[a-zA-Z]|\].*?(?:\x07|\x1B\\))",
            RegexOptions.Compiled);

        public static string StripAnsiCodes(string text)
        {
            return AnsiRegex.Replace(text, "");
        }

        /// <summary>
        /// Extracts the content within a fenced code block (e.g. ```SUBTASKS ... ```)
        /// and discards any text before the opening or after the closing delimiter.
        /// Returns null if no matching block is found.
        /// </summary>
        public static string? ExtractCodeBlockContent(string output, string blockName)
        {
            var match = Regex.Match(output, $@"```{Regex.Escape(blockName)}\s*\n([\s\S]*?)```", RegexOptions.Multiline);
            if (!match.Success)
                return null;
            return match.Groups[1].Value.Trim();
        }

        public static string NormalizePath(string path, string? basePath)
        {
            path = path.Replace('/', '\\');
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(basePath))
                path = Path.Combine(basePath, path);
            try { path = Path.GetFullPath(path); } catch { }
            return path.ToLowerInvariant();
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
                   toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
                   toolName.Equals("TodoWrite", StringComparison.OrdinalIgnoreCase);
        }

        public static string? ExtractExecutionPrompt(string output)
        {
            var match = Regex.Match(output, @"```EXECUTION_PROMPT\s*\n(.*?)```", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
    }
}
