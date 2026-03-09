using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages per-project rules, rule instructions, and crash/app/hang log paths.
    /// </summary>
    public class ProjectRulesManager
    {
        private readonly IProjectDataProvider _data;

        public ProjectRulesManager(IProjectDataProvider data)
        {
            _data = data;
        }

        public void SaveRuleInstruction()
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry != null)
            {
                entry.RuleInstruction = _data.View.RuleInstructionBox.Text;
                _data.SaveProjects();
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
        }

        public void RemoveProjectRule(string rule)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry == null) return;
            entry.ProjectRules.Remove(rule);
            _data.SaveProjects();
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
    }
}
