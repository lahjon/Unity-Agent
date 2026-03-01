using System.Collections.Generic;
using System.Diagnostics;

namespace HappyEngine.Managers
{
    public interface IPromptBuilder
    {
        string BuildBasePrompt(string systemPrompt, string description, bool useMcp,
            bool isOvernight, bool extendedPlanning = false, bool noGitWrite = false,
            bool planOnly = false, string projectDescription = "",
            string projectRulesBlock = "",
            bool autoDecompose = false, bool spawnTeam = false,
            bool isGameProject = false, string taskId = "");

        string BuildFullPrompt(string systemPrompt, AgentTask task,
            string projectDescription = "", string projectRulesBlock = "",
            bool isGameProject = false);

        string BuildPromptWithImages(string basePrompt, List<string> imagePaths);

        string BuildMessageBusBlock(string taskId,
            List<(string id, string summary)> siblings);

        string BuildClaudeCommand(bool skipPermissions, bool remoteSession);

        string BuildPowerShellScript(string projectPath, string promptFilePath,
            string claudeCmd);

        string BuildHeadlessPowerShellScript(string projectPath, string promptFilePath,
            bool skipPermissions, bool remoteSession);

        ProcessStartInfo BuildProcessStartInfo(string ps1FilePath, bool headless);

        string BuildOvernightContinuationPrompt(int iteration, int maxIterations, string taskId = "");

        string BuildDependencyContext(List<string> depIds,
            IEnumerable<AgentTask> activeTasks, IEnumerable<AgentTask> historyTasks);

    }
}
