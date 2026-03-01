namespace HappyEngine.Models
{
    public class TaskTemplate : TaskConfigBase
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool SkipPermissions { get; set; } = true;
        public int MaxIterations { get; set; }
    }
}
