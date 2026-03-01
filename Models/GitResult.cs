namespace HappyEngine.Models
{
    /// <summary>
    /// Represents the result of a git command execution with structured output, error, and exit code.
    /// </summary>
    /// <param name="Output">The standard output from the git command</param>
    /// <param name="Error">The standard error output from the git command</param>
    /// <param name="ExitCode">The exit code returned by the git process</param>
    public record GitResult(string Output, string Error, int ExitCode)
    {
        /// <summary>
        /// Gets a value indicating whether the git command executed successfully (exit code 0).
        /// </summary>
        public bool IsSuccess => ExitCode == 0;

        /// <summary>
        /// Gets the trimmed output if successful, otherwise null.
        /// </summary>
        public string? TrimmedOutput => IsSuccess ? Output.Trim() : null;

        /// <summary>
        /// Gets a combined error message including stderr and exit code for failed commands.
        /// </summary>
        public string GetErrorMessage()
        {
            if (IsSuccess) return string.Empty;

            var errorMessage = !string.IsNullOrWhiteSpace(Error) ? Error.Trim() : "Git command failed";
            return $"{errorMessage} (exit code: {ExitCode})";
        }
    }
}