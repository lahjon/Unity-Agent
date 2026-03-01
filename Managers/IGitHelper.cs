using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public interface IGitHelper
    {
        Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default);

        Task<string?> CaptureGitHeadAsync(string projectPath,
            CancellationToken cancellationToken = default);

        Task<List<(string name, int added, int removed)>?> GetGitFileChangesAsync(
            string projectPath, string? gitStartHash,
            CancellationToken cancellationToken = default);

        string? CaptureGitHead(string projectPath);

        List<(string name, int added, int removed)>? GetGitFileChanges(
            string projectPath, string? gitStartHash);
    }
}
