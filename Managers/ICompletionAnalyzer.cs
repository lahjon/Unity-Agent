using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spritely.Managers
{
    public interface ICompletionAnalyzer
    {
        bool IsTaskOutputComplete(string[] lines, int recommendationLine);

        string? ExtractRecommendations(string output);

        string FormatCompletionSummary(AgentTaskStatus status, TimeSpan duration,
            List<(string name, int added, int removed)>? fileChanges,
            long inputTokens = 0, long outputTokens = 0,
            long cacheReadTokens = 0, long cacheCreationTokens = 0);

        Task<string> GenerateCompletionSummaryAsync(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration,
            long inputTokens = 0, long outputTokens = 0,
            long cacheReadTokens = 0, long cacheCreationTokens = 0,
            CancellationToken cancellationToken = default);

        Task<ResultVerification?> VerifyResultAsync(
            string outputTail, string taskDescription, string? completionSummary,
            CancellationToken ct = default, string? modelOverride = null);

        bool CheckFeatureModeComplete(string output);

        bool IsTokenLimitError(string output);
    }
}
