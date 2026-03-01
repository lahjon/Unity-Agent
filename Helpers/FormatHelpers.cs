using System.Text.RegularExpressions;

namespace HappyEngine.Helpers
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
        /// Estimates cost in USD based on token usage and model pricing.
        /// Prices per million tokens (as of 2025):
        ///   Sonnet 4:   $3 input, $15 output, $0.30 cache read, $3.75 cache creation
        ///   Haiku 3.5:  $0.80 input, $4 output, $0.08 cache read, $1.00 cache creation
        /// Claude Code tasks default to Sonnet pricing since that's the typical model.
        /// </summary>
        public static decimal EstimateCost(long inputTokens, long outputTokens,
            long cacheReadTokens = 0, long cacheCreationTokens = 0)
        {
            // Sonnet 4 pricing (per million tokens)
            const decimal inputPricePerM = 3.00m;
            const decimal outputPricePerM = 15.00m;
            const decimal cacheReadPricePerM = 0.30m;
            const decimal cacheCreationPricePerM = 3.75m;

            return (inputTokens * inputPricePerM
                  + outputTokens * outputPricePerM
                  + cacheReadTokens * cacheReadPricePerM
                  + cacheCreationTokens * cacheCreationPricePerM) / 1_000_000m;
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
    }
}
