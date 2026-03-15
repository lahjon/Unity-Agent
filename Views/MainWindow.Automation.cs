using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Spritely.Managers;
using Spritely.Models;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Automation ─────────────────────────────────────────────

        private void BuildInvestigation_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = _projectManager.ProjectPath;
            if (string.IsNullOrEmpty(projectPath)) return;

            var crashPaths = _projectManager.GetCrashLogPaths(projectPath);
            var pathsList = string.Join("\n", crashPaths.Select(p => $"- `{p}`"));

            var desc = "Investigate recent build failures and crashes for this project.\n\n" +
                       "## Instructions\n\n" +
                       "1. Read the following crash/error log files and analyze their contents:\n" +
                       $"{pathsList}\n\n" +
                       "2. Identify the root cause of the most recent errors or crashes.\n" +
                       "3. Propose and implement fixes for the issues found.\n" +
                       "4. If a log file does not exist or is empty, note it and continue with the others.\n\n" +
                       "Focus on the most recent entries first. Provide a clear summary of what went wrong and what was fixed.";

            LaunchTaskFromDescription(
                desc,
                "Crash Log Investigation",
                imagePaths: _imageManager.DetachImages(),
                planOnly: false,
                header: "Crash Log Investigation");

            ResetPerTaskToggles();
        }

        private void BuildTest_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = _projectManager.ProjectPath;
            if (string.IsNullOrEmpty(projectPath)) return;

            var isGame = _projectManager.IsGameProject(projectPath);

            string desc;
            bool forceMcp = false;

            if (isGame)
            {
                forceMcp = true;
                desc = "Fix all compilation errors in this Unity project until the console is clean.\n\n" +
                       "## Instructions\n\n" +
                       "1. Use the MCP `get_console_logs` tool to read the Unity Editor console and retrieve all current errors and warnings.\n" +
                       "2. If there are no errors, report success.\n" +
                       "3. If there are errors:\n" +
                       "   a. Analyze each error to determine the root cause (read the referenced scripts as needed).\n" +
                       "   b. Implement fixes for each error in the source files.\n" +
                       "   c. After fixing, wait a few seconds for Unity to recompile, then call `get_console_logs` again to verify.\n" +
                       "   d. Repeat until the console shows zero errors.\n" +
                       "4. Warnings are lower priority — fix them only if trivial, but always report them.\n\n" +
                       "Provide a clear summary of what errors were found and how they were fixed.";
            }
            else
            {
                desc = "Build this project and fix all build errors until it compiles successfully.\n\n" +
                       "## Instructions\n\n" +
                       "1. Determine the build system used by this project (e.g. `dotnet build`, Unity, CMake, npm, etc.).\n" +
                       "2. Run a full build of the project.\n" +
                       "3. If the build succeeds, report success.\n" +
                       "4. If the build fails:\n" +
                       "   a. Analyze each build error to determine the root cause.\n" +
                       "   b. Implement fixes for the errors.\n" +
                       "   c. Re-run the build to verify the fixes.\n" +
                       "   d. Repeat until the build succeeds with no errors.\n\n" +
                       "Provide a clear summary of what errors were found and how they were fixed.";
            }

            LaunchTaskFromDescription(
                desc,
                "Build Test",
                imagePaths: _imageManager.DetachImages(),
                planOnly: false,
                header: "Build Test",
                forceMcp: forceMcp ? true : null);

            ResetPerTaskToggles();
        }

        private void TestVerification_Click(object sender, RoutedEventArgs e)
        {
            var projectPath = _projectManager.ProjectPath;
            if (string.IsNullOrEmpty(projectPath)) return;

            var desc = "Run all tests in this project and fix any failures.\n\n" +
                       "## Instructions\n\n" +
                       "1. Discover the test framework used by this project (e.g. NUnit, xUnit, MSTest, Jest, pytest, etc.).\n" +
                       "2. Run the full test suite using the appropriate command (e.g. `dotnet test`, `npm test`, `pytest`, etc.).\n" +
                       "3. If all tests pass, report success and provide a summary of the test results.\n" +
                       "4. If any tests fail:\n" +
                       "   a. Analyze each failure to determine the root cause.\n" +
                       "   b. Determine whether the issue is in the test itself or in the source code being tested.\n" +
                       "   c. Implement fixes for the failing tests or the underlying code.\n" +
                       "   d. Re-run the tests to verify the fixes resolve the failures.\n" +
                       "   e. Repeat until all tests pass.\n\n" +
                       "Provide a clear summary of which tests failed, why they failed, and what was done to fix them.";

            LaunchTaskFromDescription(
                desc,
                "Test Verification",
                imagePaths: _imageManager.DetachImages(),
                planOnly: false,
                header: "Test Verification");

            ResetPerTaskToggles();
        }

        private void RestartProject_Click(object sender, RoutedEventArgs e)
        {
            var buildBatPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "build.bat");
            if (!System.IO.File.Exists(buildBatPath))
            {
                Managers.AppLogger.Warn("RestartProject", "build.bat not found at " + buildBatPath);
                return;
            }

            RestartProjectBtn.IsEnabled = false;
            RestartProjectBtn.Content = "Restarting...";

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{buildBatPath}\"",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);

                // Close current instance so build.bat can replace the exe and launch fresh
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Managers.AppLogger.Warn("RestartProject", $"Failed to launch build.bat: {ex.Message}", ex);
                RestartProjectBtn.IsEnabled = true;
                RestartProjectBtn.Content = "Restart Project";
            }
        }

        // ── Suggestions ─────────────────────────────────────────────

        private async void GenerateSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (!_projectManager.HasProjects) return;
            if (_helperManager.IsGenerating) return;

            var category = SuggestionCategory.General;
            if (HelperCategoryCombo.SelectedItem is ComboBoxItem catItem)
            {
                var tag = catItem.Tag?.ToString() ?? "General";
                Enum.TryParse(tag, out category);
            }

            var guidance = SuggestionGuidanceInput.Text?.Trim();
            if (!string.IsNullOrEmpty(guidance))
                SuggestionGuidanceInput.Clear();
            await _helperManager.GenerateSuggestionsAsync(_projectManager.ProjectPath, category, guidance, _windowCts.Token);
        }

        private void SuggestionGuidanceInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                GenerateSuggestions_Click(sender, e);
                e.Handled = true;
            }
        }

        private void ClearSuggestions_Click(object sender, RoutedEventArgs e)
        {
            _helperManager.ClearSuggestions();
            HelperStatusText.Text = "";
        }

        private void RunSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;

            if (suggestion.Category == SuggestionCategory.Rules)
            {
                _projectManager.AddProjectRule($"{suggestion.Title}: {suggestion.Description}");
                _helperManager.RemoveSuggestion(suggestion);
                return;
            }

            var desc = $"Implement the following improvement:\n\n" +
                       $"## {suggestion.Title}\n\n" +
                       $"{suggestion.Description}\n\n" +
                       "You MUST fully implement this change — write the actual code, do not just analyze or produce a plan.";

            var selectedModel = ModelType.ClaudeCode;
            if (ModelCombo?.SelectedItem is ComboBoxItem modelItem && modelItem.Tag?.ToString() == "Gemini")
                selectedModel = ModelType.Gemini;

            LaunchTaskFromDescription(
                desc,
                suggestion.Title,
                selectedModel,
                imagePaths: _imageManager.DetachImages(),
                planOnly: false);

            ResetPerTaskToggles();
            _helperManager.RemoveSuggestion(suggestion);
        }

        private void RemoveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            _helperManager.RemoveSuggestion(suggestion);
        }

        private void CopySuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            var text = $"{suggestion.Title}\n\n{suggestion.Description}";
            Clipboard.SetText(text);
        }

        private void IgnoreSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            _helperManager.IgnoreSuggestion(suggestion);
        }

        private void SaveSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement el || el.Tag is not Suggestion suggestion) return;
            var text = $"{suggestion.Title}\n\n{suggestion.Description}";
            var entry = new SavedPromptEntry
            {
                PromptText = text,
                DisplayName = suggestion.Title.Length > 40 ? suggestion.Title.Substring(0, 40) + "..." : suggestion.Title,
            };
            _savedPrompts.Insert(0, entry);
            PersistSavedPrompts();
            _helperManager.RemoveSuggestion(suggestion);
        }

        // ── Helper Animation ─────────────────────────────────────────

        private static readonly string[] _helperAnimPhases = [
            "Analyzing project",
            "Scanning files",
            "Generating suggestions",
            "Thinking",
        ];

        private void StartHelperAnimation()
        {
            _helperAnimTick = 0;
            _helperAnimTimer?.Stop();
            _helperAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _helperAnimTimer.Tick += (_, _) =>
            {
                var dots = new string('.', (_helperAnimTick % 3) + 1);
                var phase = _helperAnimPhases[(_helperAnimTick / 6) % _helperAnimPhases.Length];
                HelperStatusText.Text = phase + dots;
                _helperAnimTick++;
            };
            _helperAnimTimer.Start();
        }

        private void StopHelperAnimation()
        {
            _helperAnimTimer?.Stop();
            _helperAnimTimer = null;
            GenerateSuggestionsBtn.BeginAnimation(OpacityProperty, null);
            GenerateSuggestionsBtn.Opacity = 1.0;
        }

        private void OnHelperGenerationStarted()
        {
            Dispatcher.BeginInvoke(() =>
            {
                App.TraceUi("HelperGenerationStarted");
                GenerateSuggestionsBtn.IsEnabled = false;
                GenerateSuggestionsBtn.Content = "Generating...";
                HelperStatusText.Text = "Analyzing project...";

                var pulse = new DoubleAnimation(1.0, 0.5, TimeSpan.FromSeconds(0.8))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase()
                };
                GenerateSuggestionsBtn.BeginAnimation(OpacityProperty, pulse);

                StartHelperAnimation();
            });
        }

        private void OnHelperGenerationCompleted()
        {
            Dispatcher.BeginInvoke(() =>
            {
                StopHelperAnimation();
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Generate Suggestions";
                HelperStatusText.Text = $"{_helperManager.Suggestions.Count} suggestions generated";
            });
        }

        private void OnHelperGenerationFailed(string error)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StopHelperAnimation();
                GenerateSuggestionsBtn.IsEnabled = true;
                GenerateSuggestionsBtn.Content = "Generate Suggestions";
                HelperStatusText.Text = error;
            });
        }

        private void OnBusMessageReceived(string projectPath, BusMessage message)
        {
            // Bus status messages are internal coordination noise — suppress from task output
        }

        // ── Chat Panel (delegated to ChatManager) ─────────────────

        private void NewChat_Click(object sender, RoutedEventArgs e) => _chatManager.HandleNewChat();
        private void ChatSend_Click(object sender, RoutedEventArgs e) => _chatManager.HandleSendClick();
        private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_chatManager.HandlePaste())
                {
                    e.Handled = true;
                    return;
                }
            }
            _chatManager.HandleInputKeyDown(e);
        }
        private void ChatInput_DragOver(object sender, DragEventArgs e) => _chatManager.HandleDragOver(e);
        private void ChatInput_Drop(object sender, DragEventArgs e) => _chatManager.HandleDrop(e);
        private void ChatModelCombo_Changed(object sender, SelectionChangedEventArgs e) => _chatManager.HandleModelComboChanged();
    }
}
