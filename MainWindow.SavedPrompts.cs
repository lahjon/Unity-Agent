using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgenticEngine.Models;

namespace AgenticEngine
{
    public partial class MainWindow
    {
        // ── Saved Prompts ─────────────────────────────────────────

        private readonly ObservableCollection<SavedPromptEntry> _savedPrompts = new();

        private string SavedPromptsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticEngine", "saved_prompts.json");

        private async System.Threading.Tasks.Task LoadSavedPromptsAsync()
        {
            SavedPromptsPanel.ItemsSource = _savedPrompts;

            try
            {
                if (File.Exists(SavedPromptsFile))
                {
                    var json = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(SavedPromptsFile));
                    var entries = System.Text.Json.JsonSerializer.Deserialize<List<SavedPromptEntry>>(json);
                    if (entries != null)
                    {
                        _savedPrompts.Clear();
                        foreach (var entry in entries)
                            _savedPrompts.Add(entry);
                    }
                }
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load saved prompts", ex); }
        }

        private void PersistSavedPrompts()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_savedPrompts.ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Managers.SafeFileWriter.WriteInBackground(SavedPromptsFile, json, "MainWindow");
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to persist saved prompts", ex); }
        }

        private void SavePromptEntry_Click(object sender, RoutedEventArgs e)
        {
            var text = TaskInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var modelTag = "ClaudeCode";
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem)
                modelTag = modelItem.Tag?.ToString() ?? "ClaudeCode";

            var entry = new SavedPromptEntry
            {
                PromptText = text,
                DisplayName = text.Length > 40 ? text.Substring(0, 40) + "..." : text,
                Model = modelTag,
                RemoteSession = RemoteSessionToggle.IsChecked == true,
                Headless = HeadlessToggle.IsChecked == true,
                SpawnTeam = SpawnTeamToggle.IsChecked == true,
                IsOvernight = OvernightToggle.IsChecked == true,
                ExtendedPlanning = ExtendedPlanningToggle.IsChecked == true,
                PlanOnly = PlanOnlyToggle.IsChecked == true,
                IgnoreFileLocks = IgnoreFileLocksToggle.IsChecked == true,
                UseMcp = UseMcpToggle.IsChecked == true,
                NoGitWrite = DefaultNoGitWriteToggle.IsChecked == true,
                AutoDecompose = AutoDecomposeToggle.IsChecked == true,
                AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "",
            };

            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            TaskInput.Text = string.Empty;
            AdditionalInstructionsInput.Clear();

            ResetPerTaskToggles();
        }

        private void SavedPromptCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            LoadSavedPromptIntoUi(entry);
        }

        private void SavedPromptCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();
            e.Handled = true;
        }

        private void SavedPromptRun_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            LoadSavedPromptIntoUi(entry);
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();
            Execute_Click(this, new RoutedEventArgs());
        }

        private void SavedPromptCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            Clipboard.SetText(entry.PromptText);
        }

        private void LoadSavedPromptIntoUi(SavedPromptEntry entry)
        {
            TaskInput.Text = entry.PromptText;

            // Restore model selection
            for (int i = 0; i < ModelCombo.Items.Count; i++)
            {
                if (ModelCombo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == entry.Model)
                {
                    ModelCombo.SelectedIndex = i;
                    break;
                }
            }

            // Restore toggles
            RemoteSessionToggle.IsChecked = entry.RemoteSession;
            HeadlessToggle.IsChecked = entry.Headless;
            SpawnTeamToggle.IsChecked = entry.SpawnTeam;
            OvernightToggle.IsChecked = entry.IsOvernight;
            ExtendedPlanningToggle.IsChecked = entry.ExtendedPlanning;
            PlanOnlyToggle.IsChecked = entry.PlanOnly;
            IgnoreFileLocksToggle.IsChecked = entry.IgnoreFileLocks;
            UseMcpToggle.IsChecked = entry.UseMcp;
            DefaultNoGitWriteToggle.IsChecked = entry.NoGitWrite;
            AutoDecomposeToggle.IsChecked = entry.AutoDecompose;
            AdditionalInstructionsInput.Text = entry.AdditionalInstructions ?? "";
        }

        private void DeleteSavedPrompt_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();
        }
    }
}
