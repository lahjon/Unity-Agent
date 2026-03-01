using System;

namespace HappyEngine.Models
{
    public class TaskConfigBase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Model { get; set; } = "ClaudeCode";
        public bool RemoteSession { get; set; }
        public bool Headless { get; set; }
        public bool SpawnTeam { get; set; }
        public bool IsFeatureMode { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool PlanOnly { get; set; }
        public bool IgnoreFileLocks { get; set; } = true;
        public bool UseMcp { get; set; }
        public bool NoGitWrite { get; set; } = true;
        public bool UseMessageBus { get; set; }
        public bool AutoDecompose { get; set; }
        public string AdditionalInstructions { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Copies all toggle flags from this instance to another <see cref="TaskConfigBase"/>.
        /// </summary>
        public void CopyFlagsTo(TaskConfigBase target)
        {
            target.RemoteSession = RemoteSession;
            target.Headless = Headless;
            target.SpawnTeam = SpawnTeam;
            target.IsFeatureMode = IsFeatureMode;
            target.ExtendedPlanning = ExtendedPlanning;
            target.PlanOnly = PlanOnly;
            target.IgnoreFileLocks = IgnoreFileLocks;
            target.UseMcp = UseMcp;
            target.NoGitWrite = NoGitWrite;
            target.UseMessageBus = UseMessageBus;
            target.AutoDecompose = AutoDecompose;
        }

        /// <summary>
        /// Copies toggle flags from an <see cref="AgentTaskData"/> into this instance.
        /// </summary>
        public void CopyFlagsFrom(AgentTaskData source)
        {
            RemoteSession = source.RemoteSession;
            Headless = source.Headless;
            SpawnTeam = source.SpawnTeam;
            IsFeatureMode = source.IsFeatureMode;
            ExtendedPlanning = source.ExtendedPlanning;
            PlanOnly = source.PlanOnly;
            IgnoreFileLocks = source.IgnoreFileLocks;
            UseMcp = source.UseMcp;
            NoGitWrite = source.NoGitWrite;
            UseMessageBus = source.UseMessageBus;
            AutoDecompose = source.AutoDecompose;
        }

        /// <summary>
        /// Applies toggle flags from this instance onto an <see cref="AgentTaskData"/>.
        /// </summary>
        public void ApplyFlagsTo(AgentTaskData target)
        {
            target.RemoteSession = RemoteSession;
            target.Headless = Headless;
            target.SpawnTeam = SpawnTeam;
            target.IsFeatureMode = IsFeatureMode;
            target.ExtendedPlanning = ExtendedPlanning;
            target.PlanOnly = PlanOnly;
            target.IgnoreFileLocks = IgnoreFileLocks;
            target.UseMcp = UseMcp;
            target.NoGitWrite = NoGitWrite;
            target.UseMessageBus = UseMessageBus;
            target.AutoDecompose = AutoDecompose;
        }
    }
}
