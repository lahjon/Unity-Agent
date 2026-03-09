using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── System Prompt Editing ─────────────────────────────────────

        private void EditSystemPromptToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditSystemPromptToggle.IsChecked == true;
            SystemPromptBox.IsReadOnly = !editing;
            SystemPromptBox.Opacity = editing ? 1.0 : 0.6;
            PromptEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SavePrompt_Click(object sender, RoutedEventArgs e)
        {
            SystemPrompt = SystemPromptBox.Text;
            var content = SystemPrompt;
            var path = SystemPromptFile;
            Managers.SafeFileWriter.WriteInBackground(path, content, "MainWindow");
            EditSystemPromptToggle.IsChecked = false;
        }

        private void ResetPrompt_Click(object sender, RoutedEventArgs e)
        {
            var path = SystemPromptFile;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                }
                catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to delete system prompt file", ex); }
            }, System.Threading.CancellationToken.None);
            SystemPrompt = DefaultSystemPrompt;
            SystemPromptBox.Text = SystemPrompt;
            EditSystemPromptToggle.IsChecked = false;
        }

        // ── Project Description Editing ────────────────────────────

        private void EditShortDescToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditShortDescToggle.IsChecked == true;
            ShortDescBox.IsReadOnly = !editing;
            ShortDescBox.Opacity = editing ? 1.0 : 0.6;
            ShortDescEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EditLongDescToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditLongDescToggle.IsChecked == true;
            LongDescBox.IsReadOnly = !editing;
            LongDescBox.Opacity = editing ? 1.0 : 0.6;
            LongDescEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveShortDesc_Click(object sender, RoutedEventArgs e) => _projectManager.SaveShortDesc();

        private void SaveLongDesc_Click(object sender, RoutedEventArgs e) => _projectManager.SaveLongDesc();

        private void EditRuleInstructionToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditRuleInstructionToggle.IsChecked == true;
            RuleInstructionBox.IsReadOnly = !editing;
            RuleInstructionBox.Opacity = editing ? 1.0 : 0.6;
            RuleInstructionEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveRuleInstruction_Click(object sender, RoutedEventArgs e) => _projectManager.SaveRuleInstruction();

        private void EditCrashLogPathsToggle_Changed(object sender, RoutedEventArgs e)
        {
            var editing = EditCrashLogPathsToggle.IsChecked == true;
            CrashLogPathBox.IsReadOnly = !editing;
            CrashLogPathBox.Opacity = editing ? 1.0 : 0.6;
            AppLogPathBox.IsReadOnly = !editing;
            AppLogPathBox.Opacity = editing ? 1.0 : 0.6;
            HangLogPathBox.IsReadOnly = !editing;
            HangLogPathBox.Opacity = editing ? 1.0 : 0.6;
            CrashLogPathsEditButtons.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveCrashLogPaths_Click(object sender, RoutedEventArgs e)
        {
            _projectManager.SaveCrashLogPaths(
                CrashLogPathBox.Text.Trim(),
                AppLogPathBox.Text.Trim(),
                HangLogPathBox.Text.Trim());
        }

        private void ResetCrashLogPaths_Click(object sender, RoutedEventArgs e)
        {
            CrashLogPathBox.Text = ProjectManager.GetDefaultCrashLogPath();
            AppLogPathBox.Text = ProjectManager.GetDefaultAppLogPath();
            HangLogPathBox.Text = ProjectManager.GetDefaultHangLogPath();
        }

        private void ProjectTypeGameToggle_Changed(object sender, RoutedEventArgs e)
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry != null)
            {
                entry.IsGame = ProjectTypeGameToggle.IsChecked == true;
                _projectManager.SaveProjects();
                UpdateMcpVisibility(entry.IsGame);
            }
        }

        private void UpdateMcpVisibility(bool isGame)
        {
            UseMcpToggle.Visibility = isGame ? Visibility.Visible : Visibility.Collapsed;
            McpProjectTabItem.Visibility = isGame ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── MCP Settings ────────────────────────────────────────────

        private void SyncMcpSettingsFields()
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry != null)
            {
                McpServerNameBox.Text = entry.McpServerName;
                McpAddressBox.Text = entry.McpAddress;
                McpStartCommandBox.Text = entry.McpStartCommand;
                McpConnectionStatus.Text = entry.McpStatus switch
                {
                    Models.McpStatus.Enabled => "Connected",
                    Models.McpStatus.Initialized => "Initialized",
                    Models.McpStatus.Investigating => "Investigating...",
                    _ => "Disconnected"
                };
                McpConnectionStatus.Foreground = entry.McpStatus switch
                {
                    Models.McpStatus.Enabled => FindResource("Success") as System.Windows.Media.Brush,
                    Models.McpStatus.Investigating => FindResource("WarningOrange") as System.Windows.Media.Brush,
                    _ => FindResource("TextMuted") as System.Windows.Media.Brush
                } ?? FindResource("TextMuted") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

                McpOutputTextBox.Text = entry.McpOutput?.ToString() ?? "";
            }
        }

        private void McpSettings_Changed(object sender, RoutedEventArgs e)
        {
            var entry = _projectManager.SavedProjects.FirstOrDefault(p => p.Path == _projectManager.ProjectPath);
            if (entry == null) return;
            entry.McpServerName = McpServerNameBox.Text?.Trim() ?? "UnityMCP";
            entry.McpAddress = McpAddressBox.Text?.Trim() ?? "http://127.0.0.1:8080/mcp";
            entry.McpStartCommand = McpStartCommandBox.Text?.Trim() ?? "";
            _projectManager.SaveProjects();
        }

        private async void McpTestConnection_Click(object sender, RoutedEventArgs e)
        {
            McpConnectionStatus.Text = "Testing...";
            McpConnectionStatus.Foreground = FindResource("TextMuted") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Gray;
            McpTestConnectionBtn.IsEnabled = false;
            try
            {
                var address = McpAddressBox.Text?.Trim();
                if (string.IsNullOrEmpty(address)) { McpConnectionStatus.Text = "No address"; return; }

                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    McpConnectionStatus.Text = "Invalid URL";
                    McpConnectionStatus.Foreground = FindResource("Danger") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Red;
                    return;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                var jsonRequest = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                            experimental = new { }
                        },
                        clientInfo = new
                        {
                            name = "Spritely",
                            version = "1.0.0"
                        }
                    },
                    id = 1
                };

                var json = System.Text.Json.JsonSerializer.Serialize(jsonRequest);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Content = content;
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await SharedHttpClient.SendAsync(request, timeoutCts.Token);

                if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable &&
                    response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    McpConnectionStatus.Text = "Connected";
                    McpConnectionStatus.Foreground = FindResource("Success") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Green;
                }
                else
                {
                    McpConnectionStatus.Text = $"Error: {(int)response.StatusCode}";
                    McpConnectionStatus.Foreground = FindResource("Danger") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MCP", "Connection test failed", ex);
                McpConnectionStatus.Text = "Unreachable";
                McpConnectionStatus.Foreground = FindResource("Danger") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Red;
            }
            finally
            {
                McpTestConnectionBtn.IsEnabled = true;
            }
        }

        private void AddProjectRule_Click(object sender, RoutedEventArgs e)
        {
            var rule = NewRuleInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(rule)) return;
            _projectManager.AddProjectRule(rule);
            NewRuleInput.Clear();
        }

        private void NewRuleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
            {
                AddProjectRule_Click(sender, e);
                e.Handled = true;
            }
        }

        private void RemoveProjectRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string rule)
                _projectManager.RemoveProjectRule(rule);
        }

        private void RegenerateDescriptions_Click(object sender, RoutedEventArgs e) => _projectManager.RegenerateDescriptions();

        // ── Toggle Handlers ────────────────────────────────────────

        private void DefaultToggle_Changed(object sender, RoutedEventArgs e)
        {
        }

        private void FeatureModeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ExecuteButton == null) return;
            UpdateExecuteButtonText();
            if (FeatureModeIterationsPanel != null)
                FeatureModeIterationsPanel.Visibility = FeatureModeToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Project Events ─────────────────────────────────────────

        private void AddProjectBtn_Click(object sender, RoutedEventArgs e) =>
            _projectManager.HandleAddProjectPathClick(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);

        private void CreateProject_Click(object sender, RoutedEventArgs e) =>
            _projectManager.CreateProject(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);

        private void ProjectCombo_Changed(object sender, SelectionChangedEventArgs e) { }

        private void ModelCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ModelCombo?.SelectedItem is not ComboBoxItem item) return;
            var tag = item.Tag?.ToString();
            var isGemini = tag == "Gemini" || tag == "GeminiGameArt";
            var isGameArt = tag == "GeminiGameArt";
            if (FeatureModeToggle != null) FeatureModeToggle.IsEnabled = !isGemini;
            if (SpawnTeamToggle != null) SpawnTeamToggle.IsEnabled = !isGemini;
            if (ExtendedPlanningToggle != null) ExtendedPlanningToggle.IsEnabled = !isGemini;
            if (AutoDecomposeToggle != null) AutoDecomposeToggle.IsEnabled = !isGemini;
            if (AssetTypeLabel != null) AssetTypeLabel.Visibility = isGameArt ? Visibility.Visible : Visibility.Collapsed;
            if (AssetTypeCombo != null) AssetTypeCombo.Visibility = isGameArt ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
