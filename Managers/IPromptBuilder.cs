using System.Collections.Generic;
using System.Diagnostics;

namespace HappyEngine.Managers
{
    public interface IPromptBuilder
    {
        string BuildBasePrompt(string systemPrompt, string description, bool useMcp,
            bool isFeatureMode, bool extendedPlanning = false, bool noGitWrite = false,
            bool planOnly = false, string projectDescription = "",
            string projectRulesBlock = "",
            bool autoDecompose = false, bool spawnTeam = false,
            bool isGameProject = false, string taskId = "",
            bool applyFix = true);

        string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false);

        string BuildPromptWithImages(string basePrompt, List<string> imagePaths);

        string BuildMessageBusBlock(string taskId,
            List<(string id, string summary)> siblings);

        string BuildClaudeCommand(bool skipPermissions, bool remoteSession, string? modelId = null);

        string BuildPowerShellScript(string projectPath, string promptFilePath,
            string claudeCmd);

        string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath,
            bool skipPermissions, bool remoteSession, string? modelId = null);

        ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless);

        string BuildFeatureModeContinuationPrompt(int iteration, int maxIterations, string taskId = "");

        string BuildFeatureModePlanConsolidationPrompt(int iteration, int maxIterations,
            string teamResults, string featureDescription);

        string BuildFeatureModeEvaluationPrompt(int iteration, int maxIterations,
            string featureDescription, string implementationResults);

        string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks);

    }
}
