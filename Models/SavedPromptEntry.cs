namespace AgenticEngine.Models
{
    public class SavedPromptEntry : TaskConfigBase
    {
        public string PromptText { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }
}
