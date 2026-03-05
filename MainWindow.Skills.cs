using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spritely.Dialogs;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Skills ────────────────────────────────────────────────

        private readonly ObservableCollection<SkillEntry> _skills = new();
        private bool _skillsPanelOpen = true;

        /// <summary>
        /// Loads skills from both global and project directories and binds them to the UI.
        /// Called during Window_Loaded and when the active project changes.
        /// </summary>
        private async System.Threading.Tasks.Task LoadSkillsAsync()
        {
            try
            {
                var projectPath = _projectManager.HasProjects ? _projectManager.ProjectPath : null;
                await _skillManager.LoadAsync(projectPath);
                RefreshSkillsPanel();
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Warn("MainWindow", "Failed to load skills", ex);
            }
        }

        /// <summary>Rebuilds the skills chips panel from the current SkillManager state.</summary>
        private void RefreshSkillsPanel()
        {
            _skills.Clear();
            foreach (var skill in _skillManager.AllSkills)
                _skills.Add(skill);

            RenderSkillChips();
        }

        private void RenderSkillChips()
        {
            SkillsPanel.Children.Clear();

            if (_skills.Count == 0)
            {
                var placeholder = new TextBlock
                {
                    Text = "No skills — click Manage to create one",
                    Foreground = (Brush)FindResource("TextMuted"),
                    FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                SkillsPanel.Children.Add(placeholder);
                return;
            }

            foreach (var skill in _skills)
            {
                var chip = BuildSkillChip(skill);
                SkillsPanel.Children.Add(chip);
            }
        }

        private Border BuildSkillChip(SkillEntry skill)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                Background = skill.IsEnabled
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource("BgPopup"),
                ToolTip = BuildSkillTooltip(skill)
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };

            var scopeIcon = new TextBlock
            {
                Text = skill.IsGlobal ? "🌐" : "📁",
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            stack.Children.Add(scopeIcon);

            var nameBlock = new TextBlock
            {
                Text = skill.Name,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = skill.IsEnabled
                    ? Brushes.White
                    : (Brush)FindResource("TextLight"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150
            };
            stack.Children.Add(nameBlock);

            border.Child = stack;

            border.MouseLeftButtonDown += (_, _) =>
            {
                skill.IsEnabled = !skill.IsEnabled;
                // Update chip appearance
                border.Background = skill.IsEnabled
                    ? (Brush)FindResource("Accent")
                    : (Brush)FindResource("BgPopup");
                nameBlock.Foreground = skill.IsEnabled
                    ? Brushes.White
                    : (Brush)FindResource("TextLight");
            };

            // Middle-click to quick-edit
            border.MouseDown += (_, me) =>
            {
                if (me.ChangedButton != MouseButton.Middle) return;
                me.Handled = true;
                var projectPath = _projectManager.HasProjects ? _projectManager.ProjectPath : null;
                if (SkillEditorDialog.ShowSkillEditDialog(skill, skill.IsGlobal, _skillManager, projectPath))
                    RefreshSkillsPanel();
            };

            return border;
        }

        private static string BuildSkillTooltip(SkillEntry skill)
        {
            var tip = skill.Name;
            if (!string.IsNullOrWhiteSpace(skill.Category))
                tip += $" [{skill.Category}]";
            if (!string.IsNullOrWhiteSpace(skill.Description))
                tip += "\n" + skill.Description;
            tip += skill.IsGlobal ? "\n(Global)" : "\n(Project)";
            tip += "\n\nClick to toggle • Middle-click to edit";
            return tip;
        }

        private void SkillsToggle_Click(object sender, RoutedEventArgs e)
        {
            _skillsPanelOpen = !_skillsPanelOpen;
            SkillsScrollViewer.Visibility = _skillsPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            SkillsArrow.Text = _skillsPanelOpen ? "\u25BE" : "\u25B8";
        }

        private async void ManageSkills_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = _projectManager.HasProjects ? _projectManager.ProjectPath : null;
            if (SkillEditorDialog.Show(_skillManager, projectPath))
            {
                // Reload skills after changes in the manager dialog
                await LoadSkillsAsync();
            }
        }

        /// <summary>
        /// Returns the skills prompt block for the current enabled skills.
        /// Called by the prompt pipeline.
        /// </summary>
        private string GetActiveSkillsBlock() => _skillManager.BuildSkillsBlock();
    }
}

