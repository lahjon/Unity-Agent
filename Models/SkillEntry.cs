using System;

namespace Spritely.Models
{
    /// <summary>
    /// Represents a reusable skill — a named prompt snippet that can be toggled on
    /// for any task. Skills are stored as markdown files with metadata in a JSON index.
    /// </summary>
    public class SkillEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";

        /// <summary>
        /// The markdown content of the skill (loaded from the .md file).
        /// Not persisted in the index — loaded on demand.
        /// </summary>
        public string Content { get; set; } = "";

        /// <summary>Whether the skill lives in the global app folder vs. a project folder.</summary>
        public bool IsGlobal { get; set; }

        /// <summary>Whether this skill is currently toggled on for the next task.</summary>
        public bool IsEnabled { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}

