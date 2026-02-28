using System;

namespace AgenticEngine.Models
{
    internal class StoredTaskEntry : TaskRecordBase
    {
        public string FullOutput { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
