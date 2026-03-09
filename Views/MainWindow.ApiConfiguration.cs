using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Gemini Settings ─────────────────────────────────────────

        private void SaveGeminiKey_Click(object sender, RoutedEventArgs e)
        {
            var key = GeminiApiKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key) || key.Contains('*'))
            {
                GeminiKeyStatus.Text = "Enter a valid API key";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }
            if (!Managers.GeminiService.IsValidApiKeyFormat(key))
            {
                GeminiKeyStatus.Text = "Invalid key format — expected 39 chars starting with 'AIza'";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }

            try
            {
                _geminiService.SaveApiKey(key);
                GeminiApiKeyBox.Text = _geminiService.GetMaskedApiKey();
                GeminiKeyStatus.Text = "API key saved successfully";
                GeminiKeyStatus.Foreground = (Brush)FindResource("Success");
                _chatManager.PopulateModelCombo();
            }
            catch (Exception ex)
            {
                GeminiKeyStatus.Text = $"Error saving key: {ex.Message}";
                GeminiKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
        }

        private void GeminiApiLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ai.google.dev/gemini-api/docs/api-key") { UseShellExecute = true });
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Gemini API link", ex); }
        }

        private void OpenGeminiImages_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _geminiService.GetImageDirectory();
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Gemini images folder", ex); }
        }

        private void GeminiModelCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (GeminiModelCombo?.SelectedItem is not string model) return;
            _geminiService.SelectedModel = model;
        }

        // ── Claude Settings ─────────────────────────────────────────

        private void SaveClaudeKey_Click(object sender, RoutedEventArgs e)
        {
            var key = ClaudeApiKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key) || key.Contains('*'))
            {
                ClaudeKeyStatus.Text = "Enter a valid API key";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("DangerBright");
                return;
            }

            try
            {
                _claudeService.SaveApiKey(key);
                ClaudeApiKeyBox.Text = _claudeService.GetMaskedApiKey();
                ClaudeKeyStatus.Text = "API key saved successfully";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("Success");
                _chatManager.PopulateModelCombo();
            }
            catch (Exception ex)
            {
                ClaudeKeyStatus.Text = $"Error saving key: {ex.Message}";
                ClaudeKeyStatus.Foreground = (Brush)FindResource("DangerBright");
            }
        }

        private void ClaudeApiLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true });
            }
            catch (Exception ex) { Managers.AppLogger.Warn("MainWindow", "Failed to open Claude API link", ex); }
        }

        private async void RefreshGeminiModels_Click(object sender, RoutedEventArgs e)
        {
            await RefreshModelsFromApi(geminiOnly: true);
        }

        private async void RefreshClaudeModels_Click(object sender, RoutedEventArgs e)
        {
            await RefreshModelsFromApi(claudeOnly: true);
        }

        private async Task RefreshModelsFromApi(bool geminiOnly = false, bool claudeOnly = false)
        {
            RefreshGeminiModelsBtn.IsEnabled = false;
            RefreshClaudeModelsBtn.IsEnabled = false;

            var statusBlock = claudeOnly ? ClaudeRefreshStatus : GeminiRefreshStatus;
            statusBlock.Text = "Fetching models from API...";
            statusBlock.Foreground = (Brush)FindResource("TextMuted");

            try
            {
                var claudeKey = claudeOnly ? _claudeService.GetApiKeyForRefresh() : null;
                var geminiKey = geminiOnly ? _geminiService.GetApiKeyForRefresh() : null;

                var (claudeCount, geminiCount, error) = await _modelConfigManager.RefreshFromApisAsync(claudeKey, geminiKey);

                Managers.ClaudeService.AvailableModels = _modelConfigManager.ClaudeModels;
                Managers.GeminiService.AvailableModels = _modelConfigManager.GeminiModels;

                var prevGeminiModel = GeminiModelCombo.SelectedItem as string;
                GeminiModelCombo.Items.Clear();
                foreach (var model in Managers.GeminiService.AvailableModels)
                    GeminiModelCombo.Items.Add(model);
                if (prevGeminiModel != null && GeminiModelCombo.Items.Contains(prevGeminiModel))
                    GeminiModelCombo.SelectedItem = prevGeminiModel;
                else if (GeminiModelCombo.Items.Count > 0)
                    GeminiModelCombo.SelectedIndex = 0;

                _chatManager.PopulateModelCombo();

                var parts = new List<string>();
                if (claudeCount > 0) parts.Add($"{claudeCount} Claude models");
                if (geminiCount > 0) parts.Add($"{geminiCount} Gemini models");

                if (parts.Count > 0)
                {
                    statusBlock.Text = $"Loaded {string.Join(" + ", parts)}";
                    statusBlock.Foreground = (Brush)FindResource("Success");
                }
                else if (error != null)
                {
                    statusBlock.Text = $"Failed: {error}";
                    statusBlock.Foreground = (Brush)FindResource("DangerBright");
                }
                else
                {
                    statusBlock.Text = "No API key configured";
                    statusBlock.Foreground = (Brush)FindResource("TextMuted");
                }
            }
            catch (Exception ex)
            {
                statusBlock.Text = $"Error: {ex.Message}";
                statusBlock.Foreground = (Brush)FindResource("DangerBright");
            }
            finally
            {
                RefreshGeminiModelsBtn.IsEnabled = true;
                RefreshClaudeModelsBtn.IsEnabled = true;
            }
        }
    }
}
