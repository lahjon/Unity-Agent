namespace HappyEngine.Models
{
    internal class TaskRecordBase
    {
        public string Description { get; set; } = "";
        public string Summary { get; set; } = "";
        public string StoredPrompt { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string? ProjectPath { get; set; }
        public string ProjectColor { get; set; } = "#666666";
        public string ProjectDisplayName { get; set; } = "";
        public bool SkipPermissions { get; set; }
    }
}
