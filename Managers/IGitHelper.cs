using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public interface IGitHelper
    {
        Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default);

        Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            string? standardInput, CancellationToken cancellationToken = default);

        /// <summary>
        /// Securely commits changes with a message using stdin to prevent shell injection.
        /// </summary>
        /// <param name="workingDirectory">The git repository directory</param>
        /// <param name="message">The commit message (will be passed via stdin)</param>
        /// <param name="pathSpec">Optional file paths to commit (if null, commits all staged changes)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The git command output, or null if failed</returns>
        Task<string?> CommitSecureAsync(string workingDirectory, string message,
            string? pathSpec = null, CancellationToken cancellationToken = default);

        Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default);

        Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(
            string projectPath, string? gitStartHash,
            CancellationToken cancellationToken = default);
    }
}
