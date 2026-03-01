using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public class GitHelper : IGitHelper
    {
        /// <summary>
        /// Escapes a shell argument to prevent injection attacks.
        /// Wraps in single quotes and escapes any internal single quotes.
        /// </summary>
        private static string EscapeShellArgument(string arg)
        {
            // For Windows, we need to handle quotes differently
            // Double quotes with escaped internal double quotes
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        public async Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(workingDirectory, arguments, TimeSpan.FromSeconds(30), cancellationToken);
        }

        public async Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(workingDirectory, arguments, null, timeout, cancellationToken);
        }

        public async Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(workingDirectory, arguments, standardInput, TimeSpan.FromSeconds(30), cancellationToken);
        }

        public async Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = standardInput != null,
                    CreateNoWindow = true
                };
                process = Process.Start(psi);
                if (process == null) return new GitResult("", "Failed to start git process", -1);

                if (standardInput != null)
                {
                    await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                // Read both stdout and stderr concurrently to prevent deadlocks
                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                // Wait for process to exit
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

                // Get both outputs - they should complete quickly after process exits
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                return new GitResult(output, error, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) { AppLogger.Debug("GitHelper", $"Failed to kill cancelled git process: {ex.Message}"); }
                AppLogger.Warn("GitHelper", $"Git command timed out or cancelled: git {arguments}");
                return new GitResult("", "Git command timed out or was cancelled", -1);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("GitHelper", "Async git command failed", ex);
                return new GitResult("", $"Git command failed: {ex.Message}", -1);
            }
            finally
            {
                process?.Dispose();
            }
        }

        public async Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default)
        {
            var result = await RunGitCommandAsync(projectPath, "rev-parse HEAD", cancellationToken).ConfigureAwait(false);
            return result.TrimmedOutput;
        }

        public async Task<GitResult> CommitSecureAsync(string workingDirectory, string message,
            string? pathSpec = null, CancellationToken cancellationToken = default)
        {
            // Validate the commit message doesn't contain null bytes which could be used for injection
            if (message.Contains('\0'))
            {
                AppLogger.Warn("GitHelper", "Commit message contains null bytes, rejecting for security");
                return new GitResult("", "Commit message contains null bytes", -1);
            }

            // Build the git commit command
            // Always use -F - to read message from stdin, preventing any shell interpretation
            var arguments = "commit -F -";

            // Add pathspec if provided
            // Note: pathSpec should contain properly escaped individual file paths
            if (!string.IsNullOrEmpty(pathSpec))
            {
                arguments += $" -- {pathSpec}";
            }

            // Pass the message via stdin to completely avoid shell interpretation
            return await RunGitCommandAsync(workingDirectory, arguments, message, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(
            string projectPath, string? gitStartHash,
            CancellationToken cancellationToken = default)
        {
            var diffRef = gitStartHash ?? "HEAD";
            var result = await RunGitCommandAsync(projectPath, $"diff {diffRef} --numstat", cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess || string.IsNullOrEmpty(result.Output)) return null;

            var files = new List<(string name, int added, int removed)>();
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added = parts[0] == "-" ? 0 : int.TryParse(parts[0], out var a) ? a : 0;
                var removed = parts[1] == "-" ? 0 : int.TryParse(parts[1], out var r) ? r : 0;
                files.Add((parts[2], added, removed));
            }
            return files;
        }

        /// <summary>
        /// Escapes a file path for safe use in git command-line arguments.
        /// Public static method so it can be used by other components.
        /// </summary>
        public static string EscapeGitPath(string path)
        {
            // Convert backslashes to forward slashes for git
            path = path.Replace('\\', '/');
            // Use the internal escape method
            return EscapeShellArgument(path);
        }

        /// <summary>
        /// Validates that a string is a valid git commit hash (SHA-1).
        /// Valid hashes are 7-40 hexadecimal characters.
        /// </summary>
        /// <param name="hash">The hash string to validate</param>
        /// <returns>True if the hash is valid, false otherwise</returns>
        public bool IsValidGitHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return false;

            // Git hashes are SHA-1, which can be abbreviated to 7-40 characters
            // and consist only of hexadecimal characters (0-9, a-f)
            var gitHashRegex = new Regex(@"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase);
            return gitHashRegex.IsMatch(hash);
        }
    }
}
