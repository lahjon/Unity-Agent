namespace HappyEngine.Models
{
    /// <summary>
    /// Common toggle flags shared between <see cref="TaskConfigBase"/> and
    /// <see cref="AgentTaskData"/>. Centralises the flag surface so that adding
    /// a new toggle only requires updating this interface and its implementers,
    /// rather than three separate copy methods.
    /// </summary>
    public interface ITaskFlags
    {
        bool RemoteSession { get; set; }
        bool Headless { get; set; }
        bool SpawnTeam { get; set; }
        bool IsFeatureMode { get; set; }
        bool ExtendedPlanning { get; set; }
        bool PlanOnly { get; set; }
        bool IgnoreFileLocks { get; set; }
        bool UseMcp { get; set; }
        bool NoGitWrite { get; set; }
        bool UseMessageBus { get; set; }
        bool AutoDecompose { get; set; }
        bool ApplyFix { get; set; }
        int Iterations { get; set; }

        /// <summary>
        /// Copies every flag from <paramref name="source"/> into <paramref name="target"/>.
        /// </summary>
        static void CopyFlags(ITaskFlags source, ITaskFlags target)
        {
            target.RemoteSession = source.RemoteSession;
            target.Headless = source.Headless;
            target.SpawnTeam = source.SpawnTeam;
            target.IsFeatureMode = source.IsFeatureMode;
            target.ExtendedPlanning = source.ExtendedPlanning;
            target.PlanOnly = source.PlanOnly;
            target.IgnoreFileLocks = source.IgnoreFileLocks;
            target.UseMcp = source.UseMcp;
            target.NoGitWrite = source.NoGitWrite;
            target.UseMessageBus = source.UseMessageBus;
            target.AutoDecompose = source.AutoDecompose;
            target.ApplyFix = source.ApplyFix;
            target.Iterations = source.Iterations;
        }
    }
}
