using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages per-project rules, rule instructions, and crash/app/hang log paths.
    /// Optionally triggers CLAUDE.md regeneration when rules change substantively.
    /// </summary>
    public class ProjectRulesManager
    {
        private readonly IProjectDataProvider _data;
        private ITaskFactory? _taskFactory;

        // Tracks the last-known rules hash per project path to detect substantive changes
        private readonly ConcurrentDictionary<string, string> _rulesHashCache = new(StringComparer.OrdinalIgnoreCase);

        // Debounce: only one regeneration per project at a time
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingRegens = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when a CLAUDE.md regeneration starts or finishes for UI feedback.</summary>
        public event Action<string, bool>? ClaudeMdRegenerationStateChanged;

        public ProjectRulesManager(IProjectDataProvider data)
        {
            _data = data;
        }

        public void SetTaskFactory(ITaskFactory taskFactory)
        {
            _taskFactory = taskFactory;
        }

        public void SaveRuleInstruction()
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry != null)
            {
                entry.RuleInstruction = _data.View.RuleInstructionBox.Text;
                _data.SaveProjects();
                TriggerClaudeMdRegenIfNeeded(entry);
            }
            _data.View.EditRuleInstructionToggle.IsChecked = false;
        }

        public void AddProjectRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return;
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry == null) return;
            entry.ProjectRules.Add(rule.Trim());
            _data.SaveProjects();
            TriggerClaudeMdRegenIfNeeded(entry);
        }

        public void RemoveProjectRule(string rule)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry == null) return;
            entry.ProjectRules.Remove(rule);
            _data.SaveProjects();
            TriggerClaudeMdRegenIfNeeded(entry);
        }

        /// <summary>
        /// Call after rules are saved externally (e.g. from ProjectSettingsDialog)
        /// to check if CLAUDE.md should be regenerated.
        /// </summary>
        public void NotifyRulesChanged(ProjectEntry entry)
        {
            TriggerClaudeMdRegenIfNeeded(entry);
        }

        public string GetProjectRulesBlock(string projectPath)
        {
            var sb = new StringBuilder();
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            if (entry != null)
            {
                var hasInstruction = !string.IsNullOrWhiteSpace(entry.RuleInstruction);
                var hasRules = entry.ProjectRules.Count > 0;

                if (hasInstruction || hasRules)
                {
                    sb.Append("# PROJECT RULES\n");
                    if (hasInstruction)
                        sb.Append(entry.RuleInstruction.Trim()).Append("\n");
                    if (hasRules)
                    {
                        if (hasInstruction) sb.Append("\n");
                        foreach (var r in entry.ProjectRules)
                            sb.Append("- ").Append(r).Append("\n");
                    }
                    sb.Append("\n");
                }
            }

            return sb.ToString();
        }

        public static string GetDefaultCrashLogPath() => Path.Combine(AppLogger.GetLogDir(), "crash.log");
        public static string GetDefaultAppLogPath() => Path.Combine(AppLogger.GetLogDir(), "app.log");
        public static string GetDefaultHangLogPath() => Path.Combine(AppLogger.GetLogDir(), "hang.log");

        public List<string> GetCrashLogPaths(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            return new List<string>
            {
                !string.IsNullOrEmpty(entry?.CrashLogPath) ? entry.CrashLogPath : GetDefaultCrashLogPath(),
                !string.IsNullOrEmpty(entry?.AppLogPath) ? entry.AppLogPath : GetDefaultAppLogPath(),
                !string.IsNullOrEmpty(entry?.HangLogPath) ? entry.HangLogPath : GetDefaultHangLogPath()
            };
        }

        public void SaveCrashLogPaths(string crashLogPath, string appLogPath, string hangLogPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry != null)
            {
                entry.CrashLogPath = crashLogPath;
                entry.AppLogPath = appLogPath;
                entry.HangLogPath = hangLogPath;
                _data.SaveProjects();
            }
            _data.View.EditCrashLogPathsToggle.IsChecked = false;
        }

        public bool IsGameProject(string projectPath)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == projectPath);
            return entry?.IsGame == true;
        }

        // ── CLAUDE.md auto-regeneration ──────────────────────────────

        /// <summary>
        /// Computes a SHA-256 hash of the combined rule instruction + project rules
        /// to detect substantive changes.
        /// </summary>
        internal static string ComputeRulesHash(ProjectEntry entry)
        {
            var sb = new StringBuilder();
            sb.Append(entry.RuleInstruction?.Trim() ?? "");
            sb.Append('\0');
            foreach (var rule in entry.ProjectRules)
            {
                sb.Append(rule.Trim());
                sb.Append('\0');
            }
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexStringLower(bytes);
        }

        /// <summary>
        /// Seeds the hash cache for a project so the first save after app launch
        /// doesn't trigger a spurious regeneration.
        /// </summary>
        public void SeedRulesHash(ProjectEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Path))
                _rulesHashCache[entry.Path] = ComputeRulesHash(entry);
        }

        private void TriggerClaudeMdRegenIfNeeded(ProjectEntry entry)
        {
            if (!entry.AutoRegenerateClaudeMd) return;
            if (_taskFactory == null) return;
            if (string.IsNullOrEmpty(entry.Path)) return;

            var newHash = ComputeRulesHash(entry);
            var changed = !_rulesHashCache.TryGetValue(entry.Path, out var oldHash) || oldHash != newHash;
            _rulesHashCache[entry.Path] = newHash;

            if (!changed) return;

            // Cancel any pending regeneration for this project
            if (_pendingRegens.TryRemove(entry.Path, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _pendingRegens[entry.Path] = cts;

            _ = RegenerateClaudeMdAsync(entry, cts.Token);
        }

        private async Task RegenerateClaudeMdAsync(ProjectEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                // Small delay to debounce rapid rule edits
                await Task.Delay(2000, cancellationToken);

                AppLogger.Info("ProjectRulesManager", $"Auto-regenerating CLAUDE.md for {entry.DisplayName} after rule changes...");
                ClaudeMdRegenerationStateChanged?.Invoke(entry.Path, true);

                var content = await _taskFactory!.GenerateClaudeMdAsync(entry.Path, entry.IsGame, cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    AppLogger.Warn("ProjectRulesManager", $"CLAUDE.md regeneration returned empty for {entry.DisplayName}");
                    return;
                }

                var claudeMdPath = Path.Combine(entry.Path, "CLAUDE.md");
                SafeFileWriter.WriteInBackground(claudeMdPath, content, "ProjectRulesManager");
                AppLogger.Info("ProjectRulesManager", $"CLAUDE.md regenerated for {entry.DisplayName}");
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled — a newer regeneration superseded this one
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectRulesManager", $"Failed to auto-regenerate CLAUDE.md for {entry.DisplayName}", ex);
            }
            finally
            {
                _pendingRegens.TryRemove(entry.Path, out _);
                ClaudeMdRegenerationStateChanged?.Invoke(entry.Path, false);
            }
        }
    }
}
