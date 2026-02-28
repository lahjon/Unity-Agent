using System.Text.RegularExpressions;

namespace AgenticEngine.Helpers
{
    public static class FormatHelpers
    {
        public static string FormatTokenCount(long count)
        {
            if (count >= 1_000_000) return $"{count / 1_000_000.0:F1}M";
            if (count >= 1_000) return $"{count / 1_000.0:F1}K";
            return count.ToString();
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
