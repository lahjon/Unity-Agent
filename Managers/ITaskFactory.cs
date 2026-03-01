using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HappyEngine.Managers
{
    public interface ITaskFactory
    {
        bool ValidateTaskInput(string? description);

        AgentTask CreateTask(
            string description,
            string projectPath,
            bool skipPermissions,
            bool remoteSession,
            bool headless,
            bool isFeatureMode,
            bool ignoreFileLocks,
            bool useMcp,
            bool spawnTeam = false,
            bool extendedPlanning = false,
            bool noGitWrite = false,
            bool planOnly = false,
            bool useMessageBus = false,
            List<string>? imagePaths = null,
            ModelType model = ModelType.ClaudeCode,
            string? parentTaskId = null,
            bool autoDecompose = false);

        void PrepareTaskForFeatureModeStart(AgentTask task);

        string GenerateLocalSummary(string description);

        Task<(string Short, string Long)> GenerateProjectDescriptionAsync(
            string projectPath, CancellationToken cancellationToken = default);
    }
}
