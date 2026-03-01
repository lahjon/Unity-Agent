using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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

        public async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(workingDirectory, arguments, null, cancellationToken);
        }

        public async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, CancellationToken cancellationToken = default)
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
                if (process == null) return null;

                if (standardInput != null)
                {
                    await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                using var timeoutCts = new CancellationTokenSource(5000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (OperationCanceledException)
            {
                try { if (process is { HasExited: false }) process.Kill(true); } catch (Exception ex) { AppLogger.Debug("GitHelper", $"Failed to kill cancelled git process: {ex.Message}"); }
                AppLogger.Warn("GitHelper", $"Git command timed out or cancelled: git {arguments}");
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("GitHelper", "Async git command failed", ex);
                return null;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public async Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default)
        {
            return await RunGitCommandAsync(projectPath, "rev-parse HEAD", cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> CommitSecureAsync(string workingDirectory, string message,
            string? pathSpec = null, CancellationToken cancellationToken = default)
        {
            // Validate the commit message doesn't contain null bytes which could be used for injection
            if (message.Contains('\0'))
            {
                AppLogger.Warn("GitHelper", "Commit message contains null bytes, rejecting for security");
                return null;
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
            var numstatOutput = await RunGitCommandAsync(projectPath, $"diff {diffRef} --numstat", cancellationToken).ConfigureAwait(false);
            if (numstatOutput == null) return null;

            var files = new List<(string name, int added, int removed)>();
            foreach (var line in numstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
    }
}
