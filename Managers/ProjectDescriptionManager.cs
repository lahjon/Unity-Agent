using System;
using System.IO;
using System.Linq;
using System.Windows;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages project description generation, editing, and persistence.
    /// </summary>
    public class ProjectDescriptionManager
    {
        private readonly IProjectDataProvider _data;
        private ITaskFactory? _taskFactory;

        public ProjectDescriptionManager(IProjectDataProvider data)
        {
            _data = data;
        }

        public void SetTaskFactory(ITaskFactory taskFactory)
        {
            _taskFactory = taskFactory;
        }

        public void RefreshDescriptionBoxes()
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            var initializing = entry?.IsInitializing == true;

            if (initializing)
            {
                _data.View.ShortDescBox.Text = "Initializing...";
                _data.View.LongDescBox.Text = "Initializing...";
                _data.View.ShortDescBox.FontStyle = FontStyles.Italic;
                _data.View.LongDescBox.FontStyle = FontStyles.Italic;
            }
            else
            {
                _data.View.ShortDescBox.Text = entry?.ShortDescription ?? "";
                _data.View.LongDescBox.Text = entry?.LongDescription ?? "";
                _data.View.ShortDescBox.FontStyle = FontStyles.Normal;
                _data.View.LongDescBox.FontStyle = FontStyles.Normal;
            }

            _data.View.RuleInstructionBox.Text = entry?.RuleInstruction ?? "";
            _data.View.ProjectRulesList.ItemsSource = null;
            _data.View.ProjectRulesList.ItemsSource = entry?.ProjectRules ?? new System.Collections.Generic.List<string>();

            _data.View.CrashLogPathBox.Text = !string.IsNullOrEmpty(entry?.CrashLogPath) ? entry.CrashLogPath : ProjectRulesManager.GetDefaultCrashLogPath();
            _data.View.AppLogPathBox.Text = !string.IsNullOrEmpty(entry?.AppLogPath) ? entry.AppLogPath : ProjectRulesManager.GetDefaultAppLogPath();
            _data.View.HangLogPathBox.Text = !string.IsNullOrEmpty(entry?.HangLogPath) ? entry.HangLogPath : ProjectRulesManager.GetDefaultHangLogPath();
            _data.View.EditCrashLogPathsToggle.IsChecked = false;

            _data.View.EditShortDescToggle.IsChecked = false;
            _data.View.EditLongDescToggle.IsChecked = false;
            _data.View.EditRuleInstructionToggle.IsChecked = false;
            _data.View.EditShortDescToggle.IsEnabled = !initializing;
            _data.View.EditLongDescToggle.IsEnabled = !initializing;
        }

        public string GetProjectDescription(AgentTask task)
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == task.ProjectPath);
            if (entry == null) return "";
            return task.ExtendedPlanning
                ? (string.IsNullOrWhiteSpace(entry.LongDescription) ? entry.ShortDescription : entry.LongDescription)
                : entry.ShortDescription;
        }

        /// <summary>
        /// Generates descriptions and updates the entry. Throws on failure.
        /// </summary>
        public async System.Threading.Tasks.Task RegenerateProjectDescriptionAsync(ProjectEntry entry)
        {
            var (shortDesc, longDesc) = await _taskFactory!.GenerateProjectDescriptionAsync(entry.Path, default, entry.IsGame);
            entry.ShortDescription = shortDesc;
            entry.LongDescription = longDesc;
            _data.SaveProjects();
        }

        public async System.Threading.Tasks.Task GenerateProjectDescriptionInBackground(ProjectEntry entry)
        {
            try
            {
                var (shortDesc, longDesc) = await _taskFactory!.GenerateProjectDescriptionAsync(entry.Path, default, entry.IsGame);
                _data.View.ViewDispatcher.Invoke(() =>
                {
                    entry.ShortDescription = shortDesc;
                    entry.LongDescription = longDesc;
                    entry.IsInitializing = false;
                    _data.SaveProjects();
                    _data.RefreshProjectCombo();
                    _data.RefreshProjectList(null, null, null);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectDescriptionManager", $"Failed to generate description for {entry.Path}", ex);
                _data.View.ViewDispatcher.Invoke(() =>
                {
                    entry.IsInitializing = false;
                    _data.RefreshProjectList(null, null, null);
                });
            }
        }

        public void RegenerateDescriptions()
        {
            AsyncHelper.FireAndForget(async () =>
            {
                var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
                if (entry == null) return;

                entry.IsInitializing = true;
                entry.ShortDescription = "";
                entry.LongDescription = "";
                _data.SaveProjects();
                _data.RefreshProjectCombo();
                _data.RefreshProjectList(null, null, null);
                RefreshDescriptionBoxes();

                _data.View.RegenerateDescBtn.IsEnabled = false;
                _data.View.RegenerateDescBtn.Content = "Regenerating...";

                await GenerateProjectDescriptionInBackground(entry);

                _data.View.RegenerateDescBtn.Content = "Regenerate Descriptions";
                _data.View.RegenerateDescBtn.IsEnabled = true;
                RefreshDescriptionBoxes();
            }, "ProjectDescriptionManager.RegenerateDescriptions");
        }

        public void SaveShortDesc()
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry != null)
            {
                entry.ShortDescription = _data.View.ShortDescBox.Text;
                _data.SaveProjects();
            }
            _data.View.EditShortDescToggle.IsChecked = false;
        }

        public void SaveLongDesc()
        {
            var entry = _data.SavedProjects.FirstOrDefault(p => p.Path == _data.ProjectPath);
            if (entry != null)
            {
                entry.LongDescription = _data.View.LongDescBox.Text;
                _data.SaveProjects();
            }
            _data.View.EditLongDescToggle.IsChecked = false;
        }

        public async System.Threading.Tasks.Task EnsureClaudeMdAsync(ProjectEntry entry)
        {
            try
            {
                var claudeMdPath = Path.Combine(entry.Path, "CLAUDE.md");
                var claudeDir = Path.Combine(entry.Path, ".claude");

                if (File.Exists(claudeMdPath) || Directory.Exists(claudeDir))
                    return;

                AppLogger.Info("ProjectDescriptionManager", $"Generating CLAUDE.md for {entry.DisplayName}...");

                var content = await _taskFactory!.GenerateClaudeMdAsync(entry.Path, entry.IsGame);
                if (string.IsNullOrWhiteSpace(content))
                {
                    AppLogger.Warn("ProjectDescriptionManager", $"CLAUDE.md generation returned empty for {entry.DisplayName}");
                    return;
                }

                await System.IO.File.WriteAllTextAsync(claudeMdPath, content);
                AppLogger.Info("ProjectDescriptionManager", $"Created CLAUDE.md for {entry.DisplayName}");

                _data.View.ViewDispatcher.Invoke(() => _data.RefreshProjectList(null, null, null));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ProjectDescriptionManager", $"Failed to generate CLAUDE.md for {entry.DisplayName}", ex);
            }
        }
    }
}
