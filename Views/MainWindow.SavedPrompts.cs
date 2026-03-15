using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Saved Prompts ─────────────────────────────────────────

        private readonly ObservableCollection<SavedPromptEntry> _savedPrompts = new();
        private bool _savedPromptsPanelOpen = true;
        private SavedPromptEntry? _currentLoadedPrompt = null;
        private bool _loadedPromptModified = false;

        private static readonly string _globalSavedPromptsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spritely", "saved_prompts.json");

        private string? SavedPromptsFile
        {
            get
            {
                var projectPath = _projectManager?.ProjectPath;
                if (string.IsNullOrEmpty(projectPath)) return null;
                return Path.Combine(projectPath, ".spritely", "saved_prompts.json");
            }
        }

        private async System.Threading.Tasks.Task LoadSavedPromptsAsync(System.Threading.CancellationToken ct = default)
        {
            SavedPromptsPanel.ItemsSource = _savedPrompts;
            _savedPrompts.Clear();

            var file = SavedPromptsFile;
            if (file == null) return;

            try
            {
                // One-time migration: copy global saved prompts into current project
                if (!File.Exists(file) && File.Exists(_globalSavedPromptsFile))
                {
                    var dir = Path.GetDirectoryName(file)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var globalJson = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(_globalSavedPromptsFile), ct);
                    ct.ThrowIfCancellationRequested();
                    await System.Threading.Tasks.Task.Run(() => File.WriteAllText(file, globalJson), ct);
                    Managers.AppLogger.Info("MainWindow", $"Migrated global saved prompts to {file}");
                }

                if (File.Exists(file))
                {
                    var json = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(file), ct);
                    ct.ThrowIfCancellationRequested();
                    var entries = System.Text.Json.JsonSerializer.Deserialize<List<SavedPromptEntry>>(json);
                    if (entries != null)
                    {
                        foreach (var entry in entries)
                            _savedPrompts.Add(entry);
                    }
                }
            }
            catch (OperationCanceledException) { /* window closing */ }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to load saved prompts", ex); }
        }

        private void PersistSavedPrompts()
        {
            var file = SavedPromptsFile;
            if (file == null) return;

            try
            {
                var dir = Path.GetDirectoryName(file)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = System.Text.Json.JsonSerializer.Serialize(_savedPrompts.ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Managers.SafeFileWriter.WriteInBackground(file, json, "MainWindow");
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
                ActiveSkillIds = _skillManager.GetEnabledSkillIds(),
            };
            ReadUiFlagsInto(entry);

            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            TaskInput.Text = string.Empty;
            AdditionalInstructionsInput.Clear();

            // Clear tracking fields
            _currentLoadedPrompt = null;
            _loadedPromptModified = false;

            ResetPerTaskToggles();
        }

        private void StoreTask_Click(object sender, RoutedEventArgs e)
        {
            if (!_projectManager.HasProjects) return;
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

            // Clear tracking fields
            _currentLoadedPrompt = null;
            _loadedPromptModified = false;

            ResetPerTaskToggles();
        }

        internal void SavedPromptCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            LoadSavedPromptIntoUi(entry);
        }

        internal void SavedPromptCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();
            e.Handled = true;
        }

        internal void SavedPromptRun_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            LoadSavedPromptIntoUi(entry);
            _savedPrompts.Remove(entry);
            PersistSavedPrompts();

            // Clear the tracking since we already removed it
            _currentLoadedPrompt = null;
            _loadedPromptModified = false;

            Execute_Click(this, new RoutedEventArgs());
        }

        internal void SavedPromptCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.DataContext is not SavedPromptEntry entry) return;
            Clipboard.SetText(entry.PromptText);
        }

        private void LoadSavedPromptIntoUi(SavedPromptEntry entry)
        {
            TaskInput.Text = entry.PromptText;

            // Track the loaded prompt
            _currentLoadedPrompt = entry;
            _loadedPromptModified = false;

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
            SetAdditionalInstructionsExpanded(!string.IsNullOrWhiteSpace(entry.AdditionalInstructions));

            // Restore skill selections
            _skillManager.SetEnabledSkills(entry.ActiveSkillIds ?? new());
            RefreshSkillsPanel();
        }

        internal void DeleteSavedPrompt_Click(object sender, RoutedEventArgs e)
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
        }
    }
}
