using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spritely.Dialogs;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Input Validation ─────────────────────────────────────────

        private void IterationsBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private void TimeoutBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        private void AutoVerifyToggle_Changed(object sender, RoutedEventArgs e)
        {
            _settingsManager.AutoVerify = AutoVerifyToggle.IsChecked == true;
        }

        private void ShowCodeChangesToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsManager == null || _projectManager == null) return;
            _settingsManager.ShowCodeChanges = ShowCodeChangesToggle.IsChecked == true;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        // ── Auto-Mode / Feature-Mode Toggles ────────────────────────

        private void AutoModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (AdvancedBorder == null) return;
            var autoMode = AutoModeToggle.IsChecked == true;
            AdvancedBorder.Visibility = autoMode ? Visibility.Collapsed : Visibility.Visible;

            // When disabling Auto-Mode on a game project, default MCP toggle to enabled
            if (!autoMode)
            {
                var entry = _projectManager?.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
                if (entry?.IsGame == true)
                    UseMcpToggle.IsChecked = true;
            }
        }

        private void AutoFeatureModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Purely a flag read at task launch time — no immediate UI side-effects needed.
        }

        // ── Advanced Panel ───────────────────────────────────────────

        private bool _advancedPanelOpen = true;
        private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
        {
            _advancedPanelOpen = !_advancedPanelOpen;
            AdvancedScrollViewer.Visibility = _advancedPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            AdvancedArrow.Text = _advancedPanelOpen ? "\u25BE" : "\u25B8";
        }

        private void AdditionalInstructionsToggle_Click(object sender, RoutedEventArgs e)
        {
            SetAdditionalInstructionsExpanded(AdditionalInstructionsToggle.IsChecked == true);
        }

        private void SetAdditionalInstructionsExpanded(bool expanded)
        {
            AdditionalInstructionsToggle.IsChecked = expanded;
            AdditionalInstructionsInput.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (AdditionalInstructionsToggle.Template.FindName("CollapseChevron", AdditionalInstructionsToggle) is TextBlock chevron)
            {
                var angle = expanded ? 0.0 : -90.0;
                if (chevron.RenderTransform is System.Windows.Media.RotateTransform rt && !rt.IsFrozen)
                    rt.Angle = angle;
                else
                    chevron.RenderTransform = new System.Windows.Media.RotateTransform(angle);
            }
        }

        private void UpdateExecuteButtonText()
        {
            if (PlanOnlyToggle == null || ExecuteButton == null) return;
            if (PlanOnlyToggle.IsChecked == true)
                ExecuteButton.ToolTip = "Plan Task";
            else if (FeatureModeToggle.IsChecked == true)
                ExecuteButton.ToolTip = "Start Feature Mode";
            else
                ExecuteButton.ToolTip = "Execute Task";
        }

        // ── Settings Controls ────────────────────────────────────────

        private void HistoryRetention_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (HistoryRetentionCombo?.SelectedItem is not ComboBoxItem item) return;
            if (int.TryParse(item.Tag?.ToString(), out var hours))
            {
                _settingsManager.HistoryRetentionHours = hours;
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
        }

        private void MaxConcurrentTasks_Changed(object sender, RoutedEventArgs e)
        {
            ApplyMaxConcurrentTasks();
        }

        private void MaxConcurrentTasks_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) ApplyMaxConcurrentTasks();
        }

        private void ApplyMaxConcurrentTasks()
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (int.TryParse(MaxConcurrentTasksBox.Text?.Trim(), out var val) && val >= 1)
            {
                _settingsManager.MaxConcurrentTasks = val;
                MaxConcurrentTasksBox.Text = _settingsManager.MaxConcurrentTasks.ToString();
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
                DrainInitQueue();
            }
            else
            {
                MaxConcurrentTasksBox.Text = _settingsManager.MaxConcurrentTasks.ToString();
            }
        }

        private void TokenLimitRetry_Changed(object sender, RoutedEventArgs e)
        {
            ApplyTokenLimitRetry();
        }

        private void TokenLimitRetry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) ApplyTokenLimitRetry();
        }

        private void ApplyTokenLimitRetry()
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (int.TryParse(TokenLimitRetryBox.Text?.Trim(), out var val) && val >= 1)
            {
                _settingsManager.TokenLimitRetryMinutes = val;
                TokenLimitRetryBox.Text = _settingsManager.TokenLimitRetryMinutes.ToString();
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
            else
            {
                TokenLimitRetryBox.Text = _settingsManager.TokenLimitRetryMinutes.ToString();
            }
        }

        private void TaskTimeout_Changed(object sender, RoutedEventArgs e)
        {
            ApplyTaskTimeout();
        }

        private void TaskTimeout_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)) ApplyTaskTimeout();
        }

        private void ApplyTaskTimeout()
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (int.TryParse(TaskTimeoutBox.Text?.Trim(), out var val) && val >= 1)
            {
                _settingsManager.TaskTimeoutMinutes = val;
                TaskTimeoutBox.Text = _settingsManager.TaskTimeoutMinutes.ToString();
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
            else
            {
                TaskTimeoutBox.Text = _settingsManager.TaskTimeoutMinutes.ToString();
            }
        }

        private void OpusEffort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_settingsManager == null || _projectManager == null) return;
            if (OpusEffortCombo?.SelectedItem is not ComboBoxItem item) return;
            var level = item.Tag?.ToString();
            if (!string.IsNullOrEmpty(level))
            {
                _settingsManager.OpusEffortLevel = level;
                _settingsManager.SaveSettings(_projectManager.ProjectPath);
            }
        }

        private void DefaultMcpSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsManager == null || _projectManager == null) return;

            _settingsManager.DefaultMcpServerName = DefaultMcpServerNameBox.Text?.Trim() ?? "UnityMCP";
            _settingsManager.DefaultMcpAddress = DefaultMcpAddressBox.Text?.Trim() ?? "http://127.0.0.1:8080/mcp";
            _settingsManager.DefaultMcpStartCommand = DefaultMcpStartCommandBox.Text?.Trim() ?? @"%USERPROFILE%\.local\bin\uvx.exe --from ""mcpforunityserver==9.4.7"" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools";

            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        private void ViewLogs_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.LogViewerDialog.Show();
        }

        private void ViewActivity_Click(object sender, RoutedEventArgs e)
        {
            Dialogs.ActivityDashboardDialog.Show(_activeTasks, _historyTasks, _projectManager.SavedProjects);
        }

        private void ClearIgnoredSuggestions_Click(object sender, RoutedEventArgs e)
        {
            var count = _helperManager.IgnoredCount;
            if (count == 0)
            {
                DarkDialog.ShowAlert("There are no ignored suggestions to clear.", "No Ignored Suggestions");
                return;
            }
            if (!DarkDialog.ShowConfirm(
                $"This will clear {count} ignored suggestion(s).\n\nPreviously ignored suggestions may reappear on the next generation. Continue?",
                "Clear Ignored Suggestions")) return;
            _helperManager.ClearIgnoredTitles();
        }
    }
}
