using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HappyEngine.Models;

namespace HappyEngine
{
    public partial class MainWindow
    {
        // ── Saved Prompts ─────────────────────────────────────────

        private readonly ObservableCollection<SavedPromptEntry> _savedPrompts = new();
        private bool _savedPromptsPanelOpen = true;

        private string SavedPromptsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HappyEngine", "saved_prompts.json");

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
                AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "",
            };
            ReadUiFlagsInto(entry);

            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            TaskInput.Text = string.Empty;
            AdditionalInstructionsInput.Clear();

            ResetPerTaskToggles();
        }

        private void StoreTask_Click(object sender, RoutedEventArgs e)
        {
            var text = TaskInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var storedTask = new AgentTask
            {
                Description = text,
                ProjectPath = _projectManager.ProjectPath,
                ProjectColor = _projectManager.GetProjectColor(_projectManager.ProjectPath),
                ProjectDisplayName = _projectManager.GetProjectDisplayName(_projectManager.ProjectPath),
                StoredPrompt = text,
                SkipPermissions = true,
                StartTime = DateTime.Now,
                AdditionalInstructions = AdditionalInstructionsInput.Text?.Trim() ?? "",
            };
            storedTask.Summary = text.Length > 80 ? text[..80] + "..." : text;
            storedTask.Status = AgentTaskStatus.Completed;

            _storedTasks.Insert(0, storedTask);
            _historyManager.SaveStoredTasks(_storedTasks);
            RefreshFilterCombos();

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
            ApplyFlagsToUi(entry);
            AdditionalInstructionsInput.Text = entry.AdditionalInstructions ?? "";
        }

        private void DeleteSavedPrompt_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();
        }

        private void SavedPromptsToggle_Click(object sender, RoutedEventArgs e)
        {
            _savedPromptsPanelOpen = !_savedPromptsPanelOpen;
            SavedPromptsScrollViewer.Visibility = _savedPromptsPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            SavedPromptsArrow.Text = _savedPromptsPanelOpen ? "\u25BE" : "\u25B8";
            SavedPanelCol.Width = _savedPromptsPanelOpen ? new GridLength(200) : GridLength.Auto;
            SavedPanelCol.MinWidth = _savedPromptsPanelOpen ? 120 : 0;
        }
    }
}
