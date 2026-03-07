using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Spritely.Helpers;
using Spritely.Managers;

namespace Spritely.Dialogs
{
    public class ExportDialog : DarkDialogWindow
    {
        // UI controls
        private readonly RadioButton _markdownRadio;
        private readonly RadioButton _jsonRadio;
        private readonly RadioButton _plainTextRadio;
        private readonly TextBox _filePathBox;
        private readonly Button _browseButton;
        private readonly Button _exportButton;
        private readonly Button _cancelButton;
        private readonly TextBlock _taskCountLabel;

        // Data
        private readonly List<AgentTask> _tasksToExport;

        public ExportDialog(List<AgentTask> tasksToExport)
        {
            _tasksToExport = tasksToExport;

            Title = "Export Tasks";
            Width = 500;
            Height = 280;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Create main grid
            var mainGrid = new Grid { Margin = new Thickness(20) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Task count label
            _taskCountLabel = new TextBlock
            {
                Text = _tasksToExport.Count == 1
                    ? "Exporting 1 task"
                    : $"Exporting {_tasksToExport.Count} tasks",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 15)
            };
            Grid.SetRow(_taskCountLabel, 0);
            mainGrid.Children.Add(_taskCountLabel);

            // Format selection
            var formatLabel = new TextBlock
            {
                Text = "Export Format:",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(formatLabel, 1);
            mainGrid.Children.Add(formatLabel);

            var formatPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 15)
            };

            _markdownRadio = CreateRadioButton("Markdown", true);
            _jsonRadio = CreateRadioButton("JSON", false);
            _plainTextRadio = CreateRadioButton("Plain Text", false);

            _markdownRadio.Checked += (s, e) => UpdateFileExtension();
            _jsonRadio.Checked += (s, e) => UpdateFileExtension();
            _plainTextRadio.Checked += (s, e) => UpdateFileExtension();

            formatPanel.Children.Add(_markdownRadio);
            formatPanel.Children.Add(_jsonRadio);
            formatPanel.Children.Add(_plainTextRadio);

            Grid.SetRow(formatPanel, 2);
            mainGrid.Children.Add(formatPanel);

            // File path selection
            var fileLabel = new TextBlock
            {
                Text = "Save to:",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(fileLabel, 3);
            mainGrid.Children.Add(fileLabel);

            var filePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };

            _browseButton = new Button
            {
                Content = "Browse...",
                Width = 80,
                Height = 24,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("StandardBtn")
            };
            _browseButton.Click += BrowseButton_Click;
            DockPanel.SetDock(_browseButton, Dock.Right);

            _filePathBox = new TextBox
            {
                Style = (Style)Application.Current.FindResource("DarkTextBox"),
                Height = 24
            };

            filePanel.Children.Add(_browseButton);
            filePanel.Children.Add(_filePathBox);

            Grid.SetRow(filePanel, 4);
            mainGrid.Children.Add(filePanel);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            _exportButton = new Button
            {
                Content = "Export",
                Width = 80,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.FindResource("PrimaryBtn"),
                IsDefault = true
            };
            _exportButton.Click += ExportButton_Click;

            _cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 28,
                Style = (Style)Application.Current.FindResource("StandardBtn"),
                IsCancel = true
            };
            _cancelButton.Click += (s, e) => Close();

            buttonPanel.Children.Add(_exportButton);
            buttonPanel.Children.Add(_cancelButton);

            Grid.SetRow(buttonPanel, 5);
            mainGrid.Children.Add(buttonPanel);

            // Set dialog content
            SetBodyContent(mainGrid);

            // Set default file path
            SetDefaultFilePath();
        }

        private RadioButton CreateRadioButton(string content, bool isChecked)
        {
            return new RadioButton
            {
                Content = content,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 20, 0),
                Style = (Style)Application.Current.FindResource("DarkRadio")
            };
        }

        private void SetDefaultFilePath()
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var fileName = _tasksToExport.Count == 1
                ? $"task_{_tasksToExport[0].TaskNumber}_{timestamp}.md"
                : $"tasks_export_{timestamp}.md";

            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _filePathBox.Text = Path.Combine(documentsPath, fileName);
        }

        private void UpdateFileExtension()
        {
            if (string.IsNullOrWhiteSpace(_filePathBox.Text))
                return;

            var directory = Path.GetDirectoryName(_filePathBox.Text) ?? "";
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(_filePathBox.Text);

            string extension;
            if (_markdownRadio.IsChecked == true)
                extension = ".md";
            else if (_jsonRadio.IsChecked == true)
                extension = ".json";
            else
                extension = ".txt";

            _filePathBox.Text = Path.Combine(directory, fileNameWithoutExt + extension);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveFileDialog
            {
                Title = "Export Tasks",
                InitialDirectory = Path.GetDirectoryName(_filePathBox.Text) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                FileName = Path.GetFileName(_filePathBox.Text)
            };

            if (_markdownRadio.IsChecked == true)
            {
                saveDialog.Filter = "Markdown files (*.md)|*.md|All files (*.*)|*.*";
                saveDialog.DefaultExt = ".md";
            }
            else if (_jsonRadio.IsChecked == true)
            {
                saveDialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";
                saveDialog.DefaultExt = ".json";
            }
            else
            {
                saveDialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
                saveDialog.DefaultExt = ".txt";
            }

            if (saveDialog.ShowDialog() == true)
            {
                _filePathBox.Text = saveDialog.FileName;
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_filePathBox.Text))
            {
                MessageBox.Show("Please specify a file path.", "Export Tasks",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _exportButton.IsEnabled = false;
                _exportButton.Content = "Exporting...";

                string content;
                if (_markdownRadio.IsChecked == true)
                    content = ExportToMarkdown();
                else if (_jsonRadio.IsChecked == true)
                    content = ExportToJson();
                else
                    content = ExportToPlainText();

                await File.WriteAllTextAsync(_filePathBox.Text, content);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                _exportButton.IsEnabled = true;
                _exportButton.Content = "Export";
            }
        }

        private static string? TryReadPromptFile(AgentTask task)
        {
            var scriptDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spritely", "scripts");
            var promptFile = Path.Combine(scriptDir, $"prompt_{task.Id}.txt");
            try
            {
                if (File.Exists(promptFile))
                    return File.ReadAllText(promptFile, Encoding.UTF8);
            }
            catch { /* best-effort */ }
            return null;
        }

        private static string FormatDuration(TimeSpan duration) =>
            duration.TotalHours >= 1
                ? $"{duration.TotalHours:F1} hours ({duration.TotalMinutes:F0} minutes)"
                : $"{duration.TotalMinutes:F1} minutes";

        private string ExportToMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# Task Export - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"**Total Tasks:** {_tasksToExport.Count}");
            sb.AppendLine();

            // Summary table if multiple tasks
            if (_tasksToExport.Count > 1)
            {
                sb.AppendLine("## Summary");
                sb.AppendLine();
                sb.AppendLine("| Task # | Status | Description | Duration | Tokens | Cost |");
                sb.AppendLine("|--------|--------|-------------|----------|--------|------|");

                foreach (var task in _tasksToExport.OrderBy(t => t.TaskNumber))
                {
                    var duration = task.EndTime.HasValue
                        ? $"{(task.EndTime.Value - task.StartTime).TotalMinutes:F1}m"
                        : "N/A";

                    var cost = task.HasTokenData
                        ? FormatHelpers.FormatCost(FormatHelpers.EstimateCost(
                            task.InputTokens, task.OutputTokens,
                            task.CacheReadTokens, task.CacheCreationTokens))
                        : "N/A";

                    var description = task.ShortDescription.Replace("|", "\\|");
                    if (description.Length > 50)
                        description = description[..50] + "...";

                    sb.AppendLine($"| #{task.TaskNumber:D4} | {task.Status} | {description} | {duration} | {task.TotalAllTokens:N0} | {cost} |");
                }

                sb.AppendLine();
            }

            // Individual task details
            sb.AppendLine("## Task Details");
            sb.AppendLine();

            foreach (var task in _tasksToExport.OrderBy(t => t.TaskNumber))
            {
                sb.AppendLine($"### Task #{task.TaskNumber:D4}");
                sb.AppendLine();

                // ── Metadata ──
                sb.AppendLine("#### Metadata");
                sb.AppendLine();
                sb.AppendLine($"| Field | Value |");
                sb.AppendLine($"|-------|-------|");
                sb.AppendLine($"| **ID** | `{task.Id}` |");
                sb.AppendLine($"| **Status** | {task.Status} |");
                sb.AppendLine($"| **Model** | {task.Model} |");
                sb.AppendLine($"| **Project** | {task.ProjectName} |");
                sb.AppendLine($"| **Project Path** | `{task.ProjectPath}` |");
                sb.AppendLine($"| **Started** | {task.StartTime:yyyy-MM-dd HH:mm:ss} |");

                if (task.EndTime.HasValue)
                {
                    sb.AppendLine($"| **Ended** | {task.EndTime.Value:yyyy-MM-dd HH:mm:ss} |");
                    sb.AppendLine($"| **Duration** | {FormatDuration(task.EndTime.Value - task.StartTime)} |");
                }

                sb.AppendLine($"| **Priority** | {task.PriorityLevel} |");
                sb.AppendLine($"| **Iterations** | {task.CurrentIteration} / {task.MaxIterations} |");

                if (!string.IsNullOrWhiteSpace(task.ActiveTogglesText))
                    sb.AppendLine($"| **Flags** | {task.ActiveTogglesText} |");

                if (!string.IsNullOrEmpty(task.ConversationId))
                    sb.AppendLine($"| **Conversation ID** | `{task.ConversationId}` |");

                if (!string.IsNullOrEmpty(task.GitStartHash))
                    sb.AppendLine($"| **Git Start Hash** | `{task.GitStartHash}` |");
                if (!string.IsNullOrEmpty(task.CommitHash))
                    sb.AppendLine($"| **Commit Hash** | `{task.CommitHash}` |");
                if (task.IsCommitted)
                    sb.AppendLine($"| **Committed** | Yes |");

                if (!string.IsNullOrEmpty(task.GroupId))
                    sb.AppendLine($"| **Group** | {task.GroupName ?? task.GroupId} |");
                if (!string.IsNullOrEmpty(task.ParentTaskId))
                    sb.AppendLine($"| **Parent Task** | `{task.ParentTaskId}` |");
                if (task.TimeoutMinutes.HasValue)
                    sb.AppendLine($"| **Timeout** | {task.TimeoutMinutes} min |");

                sb.AppendLine();

                // ── Token Usage ──
                if (task.HasTokenData)
                {
                    sb.AppendLine("#### Token Usage");
                    sb.AppendLine();
                    var cost = FormatHelpers.EstimateCost(task.InputTokens, task.OutputTokens,
                        task.CacheReadTokens, task.CacheCreationTokens);
                    sb.AppendLine($"| Metric | Count |");
                    sb.AppendLine($"|--------|-------|");
                    sb.AppendLine($"| Input Tokens | {task.InputTokens:N0} |");
                    sb.AppendLine($"| Output Tokens | {task.OutputTokens:N0} |");
                    if (task.CacheReadTokens > 0)
                        sb.AppendLine($"| Cache Read Tokens | {task.CacheReadTokens:N0} |");
                    if (task.CacheCreationTokens > 0)
                        sb.AppendLine($"| Cache Creation Tokens | {task.CacheCreationTokens:N0} |");
                    sb.AppendLine($"| **Total Tokens** | **{task.TotalAllTokens:N0}** |");
                    sb.AppendLine($"| **Estimated Cost** | **{FormatHelpers.FormatCost(cost)}** |");
                    sb.AppendLine();
                }

                // ── Description / User Prompt ──
                sb.AppendLine("#### User Prompt (Description)");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(task.Description);
                sb.AppendLine("```");
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                {
                    sb.AppendLine("#### Additional Instructions");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(task.AdditionalInstructions);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.DependencyContext))
                {
                    sb.AppendLine("#### Dependency Context");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(task.DependencyContext);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                // ── Full Prompt Sent to Model ──
                var promptText = TryReadPromptFile(task);
                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    sb.AppendLine("#### Full Prompt Sent to Model");
                    sb.AppendLine();
                    sb.AppendLine("<details><summary>Click to expand full prompt</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(promptText);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                // ── Completion Summary ──
                if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                {
                    sb.AppendLine("#### Completion Summary");
                    sb.AppendLine();
                    sb.AppendLine(task.CompletionSummary);
                    sb.AppendLine();
                }

                // ── Verification ──
                if (!string.IsNullOrWhiteSpace(task.VerificationResult))
                {
                    sb.AppendLine("#### Verification Result");
                    sb.AppendLine();
                    sb.AppendLine($"**Verified:** {(task.IsVerified ? "Yes" : "No")}");
                    sb.AppendLine();
                    sb.AppendLine(task.VerificationResult);
                    sb.AppendLine();
                }

                // ── Recommendations ──
                if (!string.IsNullOrWhiteSpace(task.Recommendations))
                {
                    sb.AppendLine("#### Recommendations");
                    sb.AppendLine();
                    sb.AppendLine(task.Recommendations);
                    sb.AppendLine();
                }

                // ── Changed Files ──
                if (task.ChangedFiles.Count > 0)
                {
                    sb.AppendLine("#### Changed Files");
                    sb.AppendLine();
                    foreach (var file in task.ChangedFiles)
                        sb.AppendLine($"- `{file}`");
                    sb.AppendLine();
                }

                // ── Commit Info ──
                if (!string.IsNullOrWhiteSpace(task.CommitDiff))
                {
                    sb.AppendLine("#### Commit Diff (stat)");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(task.CommitDiff);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.CommitError))
                {
                    sb.AppendLine("#### Commit Error");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(task.CommitError);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }

                // ── Full Model Output ──
                if (!string.IsNullOrWhiteSpace(task.FullOutput))
                {
                    sb.AppendLine("#### Full Model Output");
                    sb.AppendLine();
                    sb.AppendLine("<details><summary>Click to expand full output</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(task.FullOutput);
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string ExportToJson()
        {
            var exportData = _tasksToExport.OrderBy(t => t.TaskNumber).Select(task =>
            {
                var cost = task.HasTokenData
                    ? FormatHelpers.EstimateCost(task.InputTokens, task.OutputTokens,
                        task.CacheReadTokens, task.CacheCreationTokens)
                    : 0m;

                var entry = new Dictionary<string, object?>
                {
                    ["id"] = task.Id,
                    ["taskNumber"] = task.TaskNumber,
                    ["status"] = task.Status.ToString(),
                    ["model"] = task.Model.ToString(),
                    ["projectName"] = task.ProjectName,
                    ["projectPath"] = task.ProjectPath,
                    ["startTime"] = task.StartTime.ToString("o"),
                    ["endTime"] = task.EndTime?.ToString("o"),
                    ["durationMinutes"] = task.EndTime.HasValue
                        ? Math.Round((task.EndTime.Value - task.StartTime).TotalMinutes, 2)
                        : null,
                    ["priority"] = task.PriorityLevel.ToString(),
                    ["iterations"] = new { current = task.CurrentIteration, max = task.MaxIterations },
                    ["flags"] = task.ActiveTogglesText,
                    ["conversationId"] = task.ConversationId,
                    ["gitStartHash"] = task.GitStartHash,
                    ["commitHash"] = task.CommitHash,
                    ["isCommitted"] = task.IsCommitted,
                    ["groupId"] = task.GroupId,
                    ["groupName"] = task.GroupName,
                    ["parentTaskId"] = task.ParentTaskId,
                    ["timeoutMinutes"] = task.TimeoutMinutes,
                    ["tokens"] = new
                    {
                        input = task.InputTokens,
                        output = task.OutputTokens,
                        cacheRead = task.CacheReadTokens,
                        cacheCreation = task.CacheCreationTokens,
                        total = task.TotalAllTokens,
                        estimatedCost = cost
                    },
                    ["description"] = task.Description,
                    ["additionalInstructions"] = NullIfEmpty(task.AdditionalInstructions),
                    ["dependencyContext"] = task.DependencyContext,
                    ["fullPromptSentToModel"] = TryReadPromptFile(task),
                    ["completionSummary"] = NullIfEmpty(task.CompletionSummary),
                    ["verificationResult"] = NullIfEmpty(task.VerificationResult),
                    ["isVerified"] = task.IsVerified,
                    ["recommendations"] = NullIfEmpty(task.Recommendations),
                    ["changedFiles"] = task.ChangedFiles.Count > 0 ? task.ChangedFiles : null,
                    ["commitDiff"] = task.CommitDiff,
                    ["commitError"] = task.CommitError,
                    ["fullOutput"] = task.FullOutput
                };

                return entry;
            }).ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(exportData, options);
        }

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        private string ExportToPlainText()
        {
            var sb = new StringBuilder();

            foreach (var task in _tasksToExport.OrderBy(t => t.TaskNumber))
            {
                sb.AppendLine(new string('=', 60));
                sb.AppendLine($"  TASK #{task.TaskNumber:D4}  (ID: {task.Id})");
                sb.AppendLine(new string('=', 60));
                sb.AppendLine();

                // Metadata
                sb.AppendLine($"Status:       {task.Status}");
                sb.AppendLine($"Model:        {task.Model}");
                sb.AppendLine($"Project:      {task.ProjectName}");
                sb.AppendLine($"Project Path: {task.ProjectPath}");
                sb.AppendLine($"Priority:     {task.PriorityLevel}");
                sb.AppendLine($"Iterations:   {task.CurrentIteration} / {task.MaxIterations}");
                sb.AppendLine($"Started:      {task.StartTime:yyyy-MM-dd HH:mm:ss}");

                if (task.EndTime.HasValue)
                {
                    sb.AppendLine($"Ended:        {task.EndTime.Value:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Duration:     {FormatDuration(task.EndTime.Value - task.StartTime)}");
                }

                if (!string.IsNullOrWhiteSpace(task.ActiveTogglesText))
                    sb.AppendLine($"Flags:        {task.ActiveTogglesText}");
                if (!string.IsNullOrEmpty(task.ConversationId))
                    sb.AppendLine($"Conversation: {task.ConversationId}");
                if (!string.IsNullOrEmpty(task.GitStartHash))
                    sb.AppendLine($"Git Start:    {task.GitStartHash}");
                if (!string.IsNullOrEmpty(task.CommitHash))
                    sb.AppendLine($"Commit Hash:  {task.CommitHash}");
                if (task.IsCommitted)
                    sb.AppendLine($"Committed:    Yes");

                if (task.HasTokenData)
                {
                    sb.AppendLine();
                    sb.AppendLine("── Token Usage ──");
                    sb.AppendLine($"  Input:          {task.InputTokens:N0}");
                    sb.AppendLine($"  Output:         {task.OutputTokens:N0}");
                    if (task.CacheReadTokens > 0)
                        sb.AppendLine($"  Cache Read:     {task.CacheReadTokens:N0}");
                    if (task.CacheCreationTokens > 0)
                        sb.AppendLine($"  Cache Creation: {task.CacheCreationTokens:N0}");
                    sb.AppendLine($"  Total:          {task.TotalAllTokens:N0}");
                    var cost = FormatHelpers.EstimateCost(task.InputTokens, task.OutputTokens,
                        task.CacheReadTokens, task.CacheCreationTokens);
                    sb.AppendLine($"  Est. Cost:      {FormatHelpers.FormatCost(cost)}");
                }

                sb.AppendLine();
                sb.AppendLine("── Description ──");
                sb.AppendLine(task.Description);
                sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(task.AdditionalInstructions))
                {
                    sb.AppendLine("── Additional Instructions ──");
                    sb.AppendLine(task.AdditionalInstructions);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.DependencyContext))
                {
                    sb.AppendLine("── Dependency Context ──");
                    sb.AppendLine(task.DependencyContext);
                    sb.AppendLine();
                }

                var promptText = TryReadPromptFile(task);
                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    sb.AppendLine("── Full Prompt Sent to Model ──");
                    sb.AppendLine(promptText);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
                {
                    sb.AppendLine("── Completion Summary ──");
                    sb.AppendLine(task.CompletionSummary);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.VerificationResult))
                {
                    sb.AppendLine($"── Verification (Verified: {(task.IsVerified ? "Yes" : "No")}) ──");
                    sb.AppendLine(task.VerificationResult);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.Recommendations))
                {
                    sb.AppendLine("── Recommendations ──");
                    sb.AppendLine(task.Recommendations);
                    sb.AppendLine();
                }

                if (task.ChangedFiles.Count > 0)
                {
                    sb.AppendLine("── Changed Files ──");
                    foreach (var file in task.ChangedFiles)
                        sb.AppendLine($"  {file}");
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.CommitDiff))
                {
                    sb.AppendLine("── Commit Diff ──");
                    sb.AppendLine(task.CommitDiff);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.CommitError))
                {
                    sb.AppendLine("── Commit Error ──");
                    sb.AppendLine(task.CommitError);
                    sb.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(task.FullOutput))
                {
                    sb.AppendLine("── Full Model Output ──");
                    sb.AppendLine(task.FullOutput);
                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}