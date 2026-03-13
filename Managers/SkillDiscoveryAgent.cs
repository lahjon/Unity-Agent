using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// After successful task completion, analyzes the task output via Haiku to discover
    /// generalizable techniques or patterns that could be saved as reusable skills.
    /// Fire-and-forget — never blocks task teardown and never propagates exceptions.
    /// </summary>
    public class SkillDiscoveryAgent
    {
        private const string DiscoveryJsonSchema =
            """{"type":"object","properties":{"has_skill":{"type":"boolean"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"content":{"type":"string"}},"required":["has_skill"]}""";

        private static readonly TimeSpan HaikuTimeout = TimeSpan.FromMinutes(1);

        private readonly ClaudeService? _claudeService;
        private readonly SkillManager? _skillManager;
        private readonly Dispatcher _dispatcher;

        public SkillDiscoveryAgent(ClaudeService? claudeService, SkillManager? skillManager, Dispatcher dispatcher)
        {
            _claudeService = claudeService;
            _skillManager = skillManager;
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Analyzes a completed task for discoverable skills. Fire-and-forget — call with discard.
        /// </summary>
        public async Task DiscoverSkillAsync(string taskSummary, string? completionSummary, string? projectPath)
        {
            try
            {
                if (_claudeService == null || !_claudeService.IsConfigured || _skillManager == null)
                    return;

                if (string.IsNullOrWhiteSpace(taskSummary))
                    return;

                var outputExcerpt = string.IsNullOrWhiteSpace(completionSummary)
                    ? ""
                    : completionSummary.Length > 2000
                        ? completionSummary[..2000]
                        : completionSummary;

                var prompt = $"""
                    Analyze the following completed AI coding task and its output summary.
                    Determine if there is a generalizable technique, pattern, or reusable instruction
                    that could benefit future tasks as a "skill" (a reusable prompt snippet).

                    Only return has_skill=true if the task reveals a non-obvious, reusable pattern
                    that would save time or improve quality on future similar tasks.
                    Do NOT flag generic coding tasks (bug fixes, simple features) as skills.
                    Focus on: novel workflows, architecture patterns, integration techniques,
                    debugging strategies, or domain-specific knowledge.

                    TASK: {taskSummary}

                    OUTPUT SUMMARY:
                    {outputExcerpt}
                    """;

                using var cts = new CancellationTokenSource(HaikuTimeout);
                var result = await _claudeService.SendStructuredHaikuAsync(prompt, DiscoveryJsonSchema, cts.Token);

                if (result == null)
                    return;

                var hasSkill = result.Value.TryGetProperty("has_skill", out var hs) && hs.GetBoolean();
                if (!hasSkill)
                    return;

                var name = result.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var description = result.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var category = result.Value.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                var content = result.Value.TryGetProperty("content", out var ct) ? ct.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
                    return;

                // Check for duplicate skill names
                foreach (var existing in _skillManager.AllSkills)
                {
                    if (existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                var skill = new SkillEntry
                {
                    Name = name,
                    Description = description,
                    Category = category,
                    Content = content,
                    IsGlobal = false
                };

                // Surface toast on the UI thread (fire-and-forget)
                _ = _dispatcher.BeginInvoke(() =>
                {
                    var result = MessageBox.Show(
                        $"Discovered a potential skill from completed task:\n\n" +
                        $"Name: {skill.Name}\n" +
                        $"Category: {skill.Category}\n" +
                        $"Description: {skill.Description}\n\n" +
                        $"Would you like to save this skill?",
                        "Skill Discovery",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)
                    {
                        _skillManager.SaveSkill(skill, projectPath);
                        AppLogger.Info("SkillDiscovery", $"Saved discovered skill: {skill.Name}");
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SkillDiscovery", "Skill discovery failed (non-fatal)", ex);
            }
        }
    }
}
