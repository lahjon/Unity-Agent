using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HappyEngine.Models;

namespace HappyEngine.Managers
{
    public interface IGitHelper
    {
        Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default);

        Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default);

        Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, CancellationToken cancellationToken = default);

        Task<GitResult> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, TimeSpan timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// Securely commits changes with a message using stdin to prevent shell injection.
        /// </summary>
        /// <param name="workingDirectory">The git repository directory</param>
        /// <param name="message">The commit message (will be passed via stdin)</param>
        /// <param name="pathSpec">Optional file paths to commit (if null, commits all staged changes)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The git command result with output, error, and exit code</returns>
        Task<GitResult> CommitSecureAsync(string workingDirectory, string message,
            string? pathSpec = null, CancellationToken cancellationToken = default);

        Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default);

        Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(
            string projectPath, string? gitStartHash,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that a string is a valid git commit hash (SHA-1).
        /// Valid hashes are 7-40 hexadecimal characters.
        /// </summary>
        /// <param name="hash">The hash string to validate</param>
        /// <returns>True if the hash is valid, false otherwise</returns>
        bool IsValidGitHash(string? hash);
    }
}
