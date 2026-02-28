using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgenticEngine.Managers
{
    public interface ICompletionAnalyzer
    {
        bool IsTaskOutputComplete(string[] lines, int recommendationLine);

        string? ExtractRecommendations(string output);

        Task<ContinueVerification?> VerifyContinueNeededAsync(
            string outputTail, string? recommendations, string taskDescription,
            CancellationToken ct = default);

        string FormatCompletionSummary(AgentTaskStatus status, TimeSpan duration,
            List<(string name, int added, int removed)>? fileChanges);

        string GenerateCompletionSummary(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration);

        Task<string> GenerateCompletionSummaryAsync(string projectPath, string? gitStartHash,
            AgentTaskStatus status, TimeSpan duration,
            CancellationToken cancellationToken = default);

        bool CheckOvernightComplete(string output);

        bool IsTokenLimitError(string output);
    }
}
