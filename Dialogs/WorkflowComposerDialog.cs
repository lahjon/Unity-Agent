using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HappyEngine.Managers;

namespace HappyEngine.Dialogs
{
    public class WorkflowStep
    {
        public string TaskName { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> DependsOn { get; set; } = new();
    }

    public class WorkflowComposerResult
    {
        public List<WorkflowStep> Steps { get; set; } = new();
    }

    public static class WorkflowComposerDialog
    {
        private const string SystemPrompt =
            "You are a workflow decomposition assistant. The user will describe a multi-step workflow in plain English. " +
            "Your job is to break it down into discrete tasks with dependency relationships.\n\n" +
            "Respond with ONLY a valid JSON array (no markdown fences, no explanation). Each element must have:\n" +
            "- \"taskName\": a short name for the task (max 60 chars)\n" +
            "- \"description\": a detailed description of what the task should accomplish\n" +
            "- \"dependsOn\": an array of taskName strings this task depends on (empty array if no dependencies)\n\n" +
            "Rules:\n" +
            "- Tasks should be ordered logically\n" +
            "- Only reference taskNames that appear earlier in the array\n" +
            "- Keep task names concise but descriptive\n" +
            "- Make descriptions actionable and specific\n" +
            "- Identify parallelizable work (tasks with no mutual dependencies)\n" +
            "- Ensure the dependency graph is a valid DAG (no cycles)\n\n" +
            "Example output:\n" +
            "[{\"taskName\":\"Refactor auth module\",\"description\":\"Refactor the authentication module to use JWT tokens instead of session cookies\",\"dependsOn\":[]}," +
            "{\"taskName\":\"Update API endpoints\",\"description\":\"Update all API endpoints to use the new JWT-based auth from the refactored auth module\",\"dependsOn\":[\"Refactor auth module\"]}," +
            "{\"taskName\":\"Run integration tests\",\"description\":\"Run the full integration test suite to verify all endpoints work with the new auth system\",\"dependsOn\":[\"Update API endpoints\"]}]";

        public static Task<WorkflowComposerResult?> ShowAsync(
            ClaudeService claudeService,
            string projectPath)
        {
            if (!claudeService.IsConfigured)
            {
                DarkDialog.ShowAlert(
                    "Claude API key not configured.\n\nGo to Settings > Claude tab to set your API key.",
                    "Claude Not Configured");
                return Task.FromResult<WorkflowComposerResult?>(null);
            }

            var dlg = DarkDialogWindow.Create("Compose Workflow", 620, 520, ResizeMode.CanResize);

            WorkflowComposerResult? result = null;
            var cts = new CancellationTokenSource();

            var rootStack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            // Title
            rootStack.Children.Add(new TextBlock
            {
                Text = "Workflow Composer",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Subtitle
            rootStack.Children.Add(new TextBlock
            {
                Text = "Describe your workflow in plain English and AI will generate a task DAG.",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Input label
            rootStack.Children.Add(new TextBlock
            {
                Text = "Workflow Description",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Multi-line text input
            var inputBox = new TextBox
            {
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(10, 8, 10, 8),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 120,
                MaxHeight = 200
            };
            rootStack.Children.Add(inputBox);

            // Hint
            rootStack.Children.Add(new TextBlock
            {
                Text = "Example: \"First refactor the auth module, then update all API endpoints in parallel, finally run integration tests\"",
                Foreground = (Brush)Application.Current.FindResource("TextDisabled"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8)
            });

            // Status / preview area
            var statusBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };
            rootStack.Children.Add(statusBlock);

            // Preview list (shows parsed tasks before confirming)
            var previewList = new ItemsControl
            {
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var previewScroll = new ScrollViewer
            {
                Content = previewList,
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Visibility = Visibility.Collapsed
            };
            rootStack.Children.Add(previewScroll);

            // Error label
            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("Danger"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            rootStack.Children.Add(errorBlock);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style
            };

            var composeBtn = new Button
            {
                Content = "Compose",
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Style = Application.Current.TryFindResource("Btn") as Style,
                FontWeight = FontWeights.SemiBold
            };

            var confirmBtn = new Button
            {
                Content = "Create Tasks",
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(18, 8, 18, 8),
                Style = Application.Current.TryFindResource("Btn") as Style,
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed
            };

            List<WorkflowStep>? parsedSteps = null;

            cancelBtn.Click += (_, _) =>
            {
                cts.Cancel();
                dlg.Close();
            };

            composeBtn.Click += async (_, _) =>
            {
                var description = inputBox.Text?.Trim();
                if (string.IsNullOrEmpty(description))
                {
                    errorBlock.Text = "Please describe your workflow.";
                    errorBlock.Visibility = Visibility.Visible;
                    return;
                }

                errorBlock.Visibility = Visibility.Collapsed;
                composeBtn.IsEnabled = false;
                composeBtn.Content = "Composing...";
                statusBlock.Text = "Sending workflow to Claude for decomposition...";
                statusBlock.Visibility = Visibility.Visible;
                previewScroll.Visibility = Visibility.Collapsed;
                confirmBtn.Visibility = Visibility.Collapsed;

                try
                {
                    parsedSteps = await ParseWorkflowAsync(claudeService, description, cts.Token);

                    if (parsedSteps == null || parsedSteps.Count == 0)
                    {
                        errorBlock.Text = "Failed to parse workflow. Please try rephrasing your description.";
                        errorBlock.Visibility = Visibility.Visible;
                        statusBlock.Visibility = Visibility.Collapsed;
                        return;
                    }

                    // Show preview
                    statusBlock.Text = $"Generated {parsedSteps.Count} tasks:";
                    previewList.Items.Clear();
                    foreach (var step in parsedSteps)
                    {
                        var stepPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

                        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
                        namePanel.Children.Add(new TextBlock
                        {
                            Text = "\u2022 ",
                            Foreground = (Brush)Application.Current.FindResource("Accent"),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        namePanel.Children.Add(new TextBlock
                        {
                            Text = step.TaskName,
                            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold,
                            FontFamily = new FontFamily("Segoe UI")
                        });

                        if (step.DependsOn.Count > 0)
                        {
                            namePanel.Children.Add(new TextBlock
                            {
                                Text = $"  (after: {string.Join(", ", step.DependsOn)})",
                                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                                FontSize = 10,
                                FontFamily = new FontFamily("Segoe UI"),
                                VerticalAlignment = VerticalAlignment.Center
                            });
                        }

                        stepPanel.Children.Add(namePanel);
                        stepPanel.Children.Add(new TextBlock
                        {
                            Text = step.Description,
                            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                            FontSize = 11,
                            FontFamily = new FontFamily("Segoe UI"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(14, 0, 0, 0)
                        });

                        previewList.Items.Add(stepPanel);
                    }

                    previewScroll.Visibility = Visibility.Visible;
                    confirmBtn.Visibility = Visibility.Visible;
                    composeBtn.Content = "Recompose";
                }
                catch (OperationCanceledException)
                {
                    // Dialog was closed during compose
                }
                catch (Exception ex)
                {
                    errorBlock.Text = $"Error: {ex.Message}";
                    errorBlock.Visibility = Visibility.Visible;
                    statusBlock.Visibility = Visibility.Collapsed;
                }
                finally
                {
                    composeBtn.IsEnabled = true;
                    if (composeBtn.Content is string s && s == "Composing...")
                        composeBtn.Content = "Compose";
                }
            };

            confirmBtn.Click += (_, _) =>
            {
                if (parsedSteps != null && parsedSteps.Count > 0)
                {
                    result = new WorkflowComposerResult { Steps = parsedSteps };
                    dlg.Close();
                }
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(composeBtn);
            btnPanel.Children.Add(confirmBtn);
            rootStack.Children.Add(btnPanel);

            dlg.SetBodyContent(rootStack);

            // Ctrl+Enter triggers compose
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && composeBtn.IsEnabled)
                    composeBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            };

            inputBox.Focus();
            dlg.ShowDialog();

            cts.Dispose();
            return Task.FromResult(result);
        }

        /// <summary>
        /// Sends the workflow description to Claude and parses the response into a list of WorkflowSteps.
        /// </summary>
        public static async Task<List<WorkflowStep>?> ParseWorkflowAsync(
            ClaudeService claudeService,
            string workflowDescription,
            CancellationToken cancellationToken = default)
        {
            var fullResponse = new System.Text.StringBuilder();

            var response = await claudeService.SendChatMessageStreamingAsync(
                history: new List<ChatMessage>(),
                userMessage: workflowDescription,
                onTextChunk: chunk => fullResponse.Append(chunk),
                systemInstruction: SystemPrompt,
                cancellationToken: cancellationToken);

            if (response.StartsWith("[Error]") || response.StartsWith("[Cancelled]"))
                return null;

            return ParseStepsFromJson(response);
        }

        private static List<WorkflowStep>? ParseStepsFromJson(string jsonText)
        {
            try
            {
                // Strip markdown fences if present
                var text = jsonText.Trim();
                if (text.StartsWith("```"))
                {
                    var firstNewline = text.IndexOf('\n');
                    if (firstNewline >= 0)
                        text = text.Substring(firstNewline + 1);
                    if (text.EndsWith("```"))
                        text = text.Substring(0, text.Length - 3);
                    text = text.Trim();
                }

                // Find the JSON array boundaries
                var start = text.IndexOf('[');
                var end = text.LastIndexOf(']');
                if (start < 0 || end < 0 || end <= start)
                    return null;

                text = text.Substring(start, end - start + 1);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var steps = JsonSerializer.Deserialize<List<WorkflowStep>>(text, options);
                if (steps == null || steps.Count == 0)
                    return null;

                // Validate: ensure dependsOn references only earlier task names
                var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var step in steps)
                {
                    step.DependsOn.RemoveAll(dep => !knownNames.Contains(dep));
                    knownNames.Add(step.TaskName);
                }

                return steps;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WorkflowComposer", $"Failed to parse workflow JSON: {ex.Message}");
                return null;
            }
        }
    }
}
