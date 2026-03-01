using System;

namespace HappyEngine.Models
{
    public class TaskConfigBase : ITaskFlags
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Model { get; set; } = "ClaudeCode";
        public bool RemoteSession { get; set; }
        public bool Headless { get; set; }
        public bool SpawnTeam { get; set; }
        public bool IsFeatureMode { get; set; }
        public bool ExtendedPlanning { get; set; }
        public bool PlanOnly { get; set; }
        public bool IgnoreFileLocks { get; set; }
        public bool UseMcp { get; set; }
        public bool NoGitWrite { get; set; }
        public bool UseMessageBus { get; set; }
        public bool AutoDecompose { get; set; }
        public bool ApplyFix { get; set; } = true;
        public int FeatureModeIterations { get; set; } = 2;
        public string AdditionalInstructions { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // ITaskFlags.Iterations maps to FeatureModeIterations
        int ITaskFlags.Iterations
        {
            get => FeatureModeIterations;
            set => FeatureModeIterations = value;
        }

        /// <summary>
        /// Copies all toggle flags from this instance to another <see cref="TaskConfigBase"/>.
        /// </summary>
        public void CopyFlagsTo(TaskConfigBase target) => ITaskFlags.CopyFlags(this, target);

        /// <summary>
        /// Copies toggle flags from an <see cref="AgentTaskData"/> into this instance.
        /// </summary>
        public void CopyFlagsFrom(AgentTaskData source) => ITaskFlags.CopyFlags(source, this);

        /// <summary>
        /// Applies toggle flags from this instance onto an <see cref="AgentTaskData"/>.
        /// </summary>
        public void ApplyFlagsTo(AgentTaskData target) => ITaskFlags.CopyFlags(this, target);
    }
}
