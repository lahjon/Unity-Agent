using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HappyEngine.Helpers;
using HappyEngine.Managers;

namespace HappyEngine.Dialogs
{
    public static class GroupSummaryDialog
    {
        public static void Show(TaskGroupState groupState)
        {
            var dlg = DarkDialogWindow.Create(
                $"Group Summary: {groupState.GroupName}", 750, 560,
                ResizeMode.CanResize, topmost: false, backgroundResource: "BgDeep");

            var root = new DockPanel();

            // Stats bar
            var elapsed = (groupState.Tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value)
                .DefaultIfEmpty(DateTime.Now)
                .Max()) - groupState.StartTime;

            var totalTokensIn = groupState.Tasks.Sum(t => t.InputTokens);
            var totalTokensOut = groupState.Tasks.Sum(t => t.OutputTokens);

            var statsBar = new WrapPanel { Margin = new Thickness(18, 10, 18, 6) };
            AddStatChip(statsBar, $"Total: {groupState.TotalCount}", (Brush)Application.Current.FindResource("TextPrimary"));
            AddStatChip(statsBar, $"Completed: {groupState.CompletedCount}", (Brush)Application.Current.FindResource("Success"));
            if (groupState.FailedCount > 0)
                AddStatChip(statsBar, $"Failed: {groupState.FailedCount}", (Brush)Application.Current.FindResource("DangerBright"));
            AddStatChip(statsBar, $"Duration: {(int)elapsed.TotalMinutes}m {elapsed.Seconds}s", (Brush)Application.Current.FindResource("TextSubdued"));
            AddStatChip(statsBar, $"Tokens: {FormatTokenCount(totalTokensIn)} in / {FormatTokenCount(totalTokensOut)} out", (Brush)Application.Current.FindResource("TextSubdued"));

            DockPanel.SetDock(statsBar, Dock.Top);
            root.Children.Add(statsBar);

            // Scrollable task list
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(18, 6, 18, 18)
            };

            var taskPanel = new StackPanel();

            foreach (var task in groupState.Tasks)
            {
                var card = CreateTaskCard(task);
                taskPanel.Children.Add(card);
            }

            // Combined recommendations section
            var allRecs = groupState.Tasks
                .Where(t => !string.IsNullOrWhiteSpace(t.Recommendations))
                .ToList();
            if (allRecs.Count > 0)
            {
                var recsHeader = new TextBlock
                {
                    Text = "Combined Recommendations",
                    Foreground = (Brush)Application.Current.FindResource("Accent"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 12, 0, 6)
                };
                taskPanel.Children.Add(recsHeader);

                foreach (var task in allRecs)
                {
                    var recBlock = new TextBlock
                    {
                        Text = $"[#{task.TaskNumber:D4}] {task.Recommendations}",
                        Foreground = (Brush)Application.Current.FindResource("TextLight"),
                        FontSize = 11.5,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    taskPanel.Children.Add(recBlock);
                }
            }

            scroll.Content = taskPanel;
            root.Children.Add(scroll);

            dlg.SetBodyContent(root);
            dlg.ShowDialog();
        }

        private static Border CreateTaskCard(TaskGroupEntry task)
        {
            var duration = task.EndTime.HasValue
                ? (task.EndTime.Value - task.StartTime)
                : TimeSpan.Zero;

            var statusColor = task.Status switch
            {
                AgentTaskStatus.Completed => (Brush)Application.Current.FindResource("Success"),
                AgentTaskStatus.Failed => (Brush)Application.Current.FindResource("DangerBright"),
                AgentTaskStatus.Cancelled => BrushCache.Theme("WarningAmber"),
                _ => (Brush)Application.Current.FindResource("TextSubdued")
            };

            var card = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0: status + task number + description
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = statusColor,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(dot);

            var taskLabel = new TextBlock
            {
                Text = $"#{task.TaskNumber:D4}",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(taskLabel);

            var descBlock = new TextBlock
            {
                Text = task.Description.Length > 80 ? task.Description[..80] + "..." : task.Description,
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            headerPanel.Children.Add(descBlock);

            Grid.SetRow(headerPanel, 0);
            grid.Children.Add(headerPanel);

            // Row 1: stats
            var statsText = $"{task.Status} | {(int)duration.TotalMinutes}m {duration.Seconds}s | {FormatTokenCount(task.InputTokens)} in / {FormatTokenCount(task.OutputTokens)} out";
            var statsBlock = new TextBlock
            {
                Text = statsText,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 10.5,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(14, 2, 0, 0)
            };
            Grid.SetRow(statsBlock, 1);
            grid.Children.Add(statsBlock);

            // Row 2: completion summary (if present)
            if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
            {
                var summaryBlock = new TextBlock
                {
                    Text = task.CompletionSummary,
                    Foreground = (Brush)Application.Current.FindResource("TextLight"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(14, 4, 0, 0)
                };
                Grid.SetRow(summaryBlock, 2);
                grid.Children.Add(summaryBlock);
            }

            card.Child = grid;
            return card;
        }

        private static void AddStatChip(WrapPanel panel, string text, Brush foreground)
        {
            var chip = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 0)
            };
            chip.Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            };
            panel.Children.Add(chip);
        }

        private static string FormatTokenCount(long count) => Helpers.FormatHelpers.FormatTokenCount(count);
    }
}
