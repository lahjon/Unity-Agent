using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public class GitHelper : IGitHelper
    {
        public async Task<string?> RunGitCommandAsync(string workingDirectory, string arguments,
            CancellationToken cancellationToken = default)
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
                    CreateNoWindow = true
                };
                process = Process.Start(psi);
                if (process == null) return null;
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
    }
}
