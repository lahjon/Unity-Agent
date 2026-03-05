using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages skills — reusable prompt snippets stored as .md files.
    /// Supports both global skills (shared across all projects) and
    /// per-project skills (stored alongside project files).
    /// </summary>
    public class SkillManager
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly string _globalSkillsDir;
        private readonly List<SkillEntry> _globalSkills = new();
        private readonly List<SkillEntry> _projectSkills = new();

        /// <summary>All loaded skills (global + project), read-only view.</summary>
        public IReadOnlyList<SkillEntry> AllSkills
        {
            get
            {
                var list = new List<SkillEntry>(_globalSkills.Count + _projectSkills.Count);
                list.AddRange(_globalSkills);
                list.AddRange(_projectSkills);
                return list;
            }
        }

        public IReadOnlyList<SkillEntry> GlobalSkills => _globalSkills;
        public IReadOnlyList<SkillEntry> ProjectSkills => _projectSkills;

        public SkillManager(string appDataDir)
        {
            _globalSkillsDir = Path.Combine(appDataDir, "skills");
        }

        // ── Loading ────────────────────────────────────────────────

        /// <summary>
        /// Loads global skills and, if a project path is provided, project skills.
        /// </summary>
        public async Task LoadAsync(string? projectPath = null)
        {
            _globalSkills.Clear();
            _projectSkills.Clear();

            var globals = await LoadFromDirectoryAsync(_globalSkillsDir, isGlobal: true);
            _globalSkills.AddRange(globals);

            if (!string.IsNullOrEmpty(projectPath))
            {
                var projectSkillsDir = GetProjectSkillsDir(projectPath);
                var projects = await LoadFromDirectoryAsync(projectSkillsDir, isGlobal: false);
                _projectSkills.AddRange(projects);
            }
        }

        private static async Task<List<SkillEntry>> LoadFromDirectoryAsync(string skillsDir, bool isGlobal)
        {
            var result = new List<SkillEntry>();
            var indexFile = Path.Combine(skillsDir, "skills_index.json");

            if (!File.Exists(indexFile)) return result;

            try
            {
                var json = await Task.Run(() => File.ReadAllText(indexFile));
                var entries = JsonSerializer.Deserialize<List<SkillEntry>>(json);
                if (entries == null) return result;

                foreach (var entry in entries)
                {
                    entry.IsGlobal = isGlobal;
                    var mdFile = Path.Combine(skillsDir, $"{entry.Id}.md");
                    if (File.Exists(mdFile))
                        entry.Content = await Task.Run(() => File.ReadAllText(mdFile));
                    result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SkillManager", $"Failed to load skills from {skillsDir}", ex);
            }

            return result;
        }

        // ── Saving ─────────────────────────────────────────────────

        /// <summary>
        /// Saves a skill (creates or updates). If the skill is global, saves to the global folder;
        /// otherwise saves to the project skills folder.
        /// </summary>
        public void SaveSkill(SkillEntry skill, string? projectPath = null)
        {
            skill.UpdatedAt = DateTime.Now;
            var dir = skill.IsGlobal ? _globalSkillsDir : GetProjectSkillsDir(projectPath ?? "");
            var list = skill.IsGlobal ? _globalSkills : _projectSkills;

            // Add or update in the in-memory list
            var existing = list.FindIndex(s => s.Id == skill.Id);
            if (existing >= 0)
                list[existing] = skill;
            else
                list.Add(skill);

            // Write the .md content file
            var mdFile = Path.Combine(dir, $"{skill.Id}.md");
            SafeFileWriter.WriteInBackground(mdFile, skill.Content, "SkillManager");

            // Write the index (without Content field to keep it small)
            PersistIndex(dir, list);
        }

        /// <summary>Deletes a skill and its .md file.</summary>
        public void DeleteSkill(SkillEntry skill, string? projectPath = null)
        {
            var dir = skill.IsGlobal ? _globalSkillsDir : GetProjectSkillsDir(projectPath ?? "");
            var list = skill.IsGlobal ? _globalSkills : _projectSkills;

            list.RemoveAll(s => s.Id == skill.Id);

            var mdFile = Path.Combine(dir, $"{skill.Id}.md");
            try { if (File.Exists(mdFile)) File.Delete(mdFile); }
            catch (Exception ex) { AppLogger.Warn("SkillManager", "Failed to delete skill file", ex); }

            PersistIndex(dir, list);
        }

        // ── Prompt Building ────────────────────────────────────────

        /// <summary>
        /// Builds a prompt block from the currently enabled skills.
        /// Returns empty string if no skills are enabled.
        /// </summary>
        public string BuildSkillsBlock()
        {
            var enabled = AllSkills.Where(s => s.IsEnabled && !string.IsNullOrWhiteSpace(s.Content)).ToList();
            if (enabled.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("# ACTIVE SKILLS");
            sb.AppendLine();

            foreach (var skill in enabled)
            {
                sb.AppendLine($"## Skill: {skill.Name}");
                if (!string.IsNullOrWhiteSpace(skill.Description))
                    sb.AppendLine($"_{skill.Description}_");
                sb.AppendLine();
                sb.AppendLine(skill.Content.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds a prompt block from specific skill IDs (for restoring from saved prompts/templates).
        /// </summary>
        public string BuildSkillsBlock(IEnumerable<string> skillIds)
        {
            var idSet = new HashSet<string>(skillIds);
            var matching = AllSkills.Where(s => idSet.Contains(s.Id) && !string.IsNullOrWhiteSpace(s.Content)).ToList();
            if (matching.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("# ACTIVE SKILLS");
            sb.AppendLine();

            foreach (var skill in matching)
            {
                sb.AppendLine($"## Skill: {skill.Name}");
                if (!string.IsNullOrWhiteSpace(skill.Description))
                    sb.AppendLine($"_{skill.Description}_");
                sb.AppendLine();
                sb.AppendLine(skill.Content.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>Returns the IDs of all currently enabled skills.</summary>
        public List<string> GetEnabledSkillIds() =>
            AllSkills.Where(s => s.IsEnabled).Select(s => s.Id).ToList();

        /// <summary>Enables skills by their IDs and disables all others.</summary>
        public void SetEnabledSkills(IEnumerable<string> skillIds)
        {
            var idSet = new HashSet<string>(skillIds ?? Enumerable.Empty<string>());
            foreach (var skill in AllSkills)
                skill.IsEnabled = idSet.Contains(skill.Id);
        }

        /// <summary>Disables all skills.</summary>
        public void ClearEnabledSkills()
        {
            foreach (var skill in AllSkills)
                skill.IsEnabled = false;
        }

        /// <summary>Returns distinct categories across all skills.</summary>
        public List<string> GetCategories() =>
            AllSkills
                .Where(s => !string.IsNullOrWhiteSpace(s.Category))
                .Select(s => s.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();

        // ── Helpers ────────────────────────────────────────────────

        private static string GetProjectSkillsDir(string projectPath) =>
            Path.Combine(projectPath, ".spritely", "skills");

        private static void PersistIndex(string dir, List<SkillEntry> skills)
        {
            // Write index without the Content field to keep it small
            var indexEntries = skills.Select(s => new SkillIndexEntry
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                Category = s.Category,
                IsGlobal = s.IsGlobal,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            }).ToList();

            var json = JsonSerializer.Serialize(indexEntries, JsonOpts);
            var indexFile = Path.Combine(dir, "skills_index.json");
            SafeFileWriter.WriteInBackground(indexFile, json, "SkillManager");
        }

        /// <summary>Index-only model (excludes Content and IsEnabled).</summary>
        private class SkillIndexEntry
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Category { get; set; } = "";
            public bool IsGlobal { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}

