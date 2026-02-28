using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AgenticEngine.Models;

namespace AgenticEngine.Managers
{
    public class ActivityDashboardManager
    {
        private const int SparklineMaxPoints = 20;

        private readonly ObservableCollection<AgentTask> _activeTasks;
        private readonly ObservableCollection<AgentTask> _historyTasks;
        private readonly List<ProjectEntry> _savedProjects;
        private bool _isDirty = true;

        public ActivityDashboardManager(
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks,
            List<ProjectEntry> savedProjects)
        {
            _activeTasks = activeTasks;
            _historyTasks = historyTasks;
            _savedProjects = savedProjects;
        }

        public void MarkDirty() => _isDirty = true;

        public void RefreshIfNeeded(ScrollViewer container)
        {
            if (!_isDirty) return;
            RefreshContent(container);
            _isDirty = false;
        }

        public void RefreshContent(ScrollViewer container)
        {
            container.Content = BuildDashboardContent(isDialog: false);
        }

        // ── Stats computation ──────────────────────────────────────────

        public List<ProjectActivityStats> ComputeStats()
        {
            var allTasks = _historyTasks
                .Concat(_activeTasks.Where(t => t.IsFinished))
                .ToList();

            var grouped = allTasks
                .GroupBy(t => NormalizePath(t.ProjectPath))
                .ToDictionary(g => g.Key, g => g.ToList());

            var results = new List<ProjectActivityStats>();

            // Stats for each group that has tasks
            foreach (var (path, tasks) in grouped)
            {
                var stats = BuildStatsForGroup(path, tasks);
                results.Add(stats);
            }

            // Include saved projects with zero history
            foreach (var proj in _savedProjects)
            {
                var normPath = NormalizePath(proj.Path);
                if (grouped.ContainsKey(normPath)) continue;

                results.Add(new ProjectActivityStats
                {
                    ProjectPath = proj.Path,
                    ProjectName = proj.DisplayName,
                    ProjectColor = string.IsNullOrEmpty(proj.Color) ? "#666666" : proj.Color
                });
            }

            // Running tasks (not yet finished) counted per project
            foreach (var task in _activeTasks.Where(t => !t.IsFinished))
            {
                var normPath = NormalizePath(task.ProjectPath);
                var existing = results.FirstOrDefault(s => NormalizePath(s.ProjectPath) == normPath);
                if (existing != null)
                    existing.RunningTasks++;
            }

            // Sort: projects with tasks first (desc), then zero-history alphabetically
            results.Sort((a, b) =>
            {
                if (a.TotalTasks != b.TotalTasks) return b.TotalTasks.CompareTo(a.TotalTasks);
                return string.Compare(a.ProjectName, b.ProjectName, StringComparison.OrdinalIgnoreCase);
            });

            return results;
        }

        private ProjectActivityStats BuildStatsForGroup(string normalizedPath, List<AgentTask> tasks)
        {
            var completed = tasks.Count(t => t.Status == AgentTaskStatus.Completed);
            var failed = tasks.Count(t => t.Status == AgentTaskStatus.Failed);
            var cancelled = tasks.Count(t => t.Status == AgentTaskStatus.Cancelled);
            var finishedCount = completed + failed;

            var durations = tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value - t.StartTime)
                .Where(d => d > TimeSpan.Zero)
                .ToList();

            // Match to saved project for name/color
            var project = _savedProjects.FirstOrDefault(p =>
                NormalizePath(p.Path) == normalizedPath);

            var displayName = project?.DisplayName
                ?? (string.IsNullOrEmpty(normalizedPath) ? "Unassigned" : System.IO.Path.GetFileName(normalizedPath));
            var color = project?.Color;
            if (string.IsNullOrEmpty(color)) color = "#666666";

            // Also try to get name from the tasks themselves
            if (displayName == "Unassigned" || displayName == System.IO.Path.GetFileName(normalizedPath))
            {
                var taskName = tasks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ProjectDisplayName))?.ProjectDisplayName;
                if (!string.IsNullOrEmpty(taskName)) displayName = taskName;
            }

            var recentCompleted = tasks
                .Where(t => t.EndTime.HasValue)
                .OrderByDescending(t => t.EndTime!.Value)
                .Take(SparklineMaxPoints)
                .Reverse()
                .Select(t => new SparklinePoint
                {
                    Timestamp = t.EndTime!.Value,
                    Succeeded = t.Status == AgentTaskStatus.Completed,
                    Duration = t.EndTime.Value - t.StartTime
                })
                .ToList();

            var mostRecentEnd = tasks
                .Where(t => t.EndTime.HasValue)
                .Select(t => t.EndTime!.Value)
                .DefaultIfEmpty()
                .Max();

            return new ProjectActivityStats
            {
                ProjectPath = normalizedPath,
                ProjectName = displayName,
                ProjectColor = color,
                TotalTasks = tasks.Count,
                CompletedTasks = completed,
                FailedTasks = failed,
                CancelledTasks = cancelled,
                SuccessRate = finishedCount > 0 ? (double)completed / finishedCount : 0,
                FailureRate = finishedCount > 0 ? (double)failed / finishedCount : 0,
                AverageDuration = durations.Count > 0
                    ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks))
                    : TimeSpan.Zero,
                TotalDuration = durations.Count > 0
                    ? TimeSpan.FromTicks(durations.Sum(d => d.Ticks))
                    : TimeSpan.Zero,
                ShortestDuration = durations.Count > 0 ? durations.Min() : null,
                LongestDuration = durations.Count > 0 ? durations.Max() : null,
                TotalInputTokens = tasks.Sum(t => t.InputTokens),
                TotalOutputTokens = tasks.Sum(t => t.OutputTokens),
                MostRecentTaskTime = mostRecentEnd != default ? mostRecentEnd : null,
                RecentActivity = recentCompleted
            };
        }

        // ── UI Building ────────────────────────────────────────────────

        public StackPanel BuildDashboardContent(bool isDialog)
        {
            var stats = ComputeStats();
            var root = new StackPanel { Margin = new Thickness(isDialog ? 18 : 4, 0, isDialog ? 18 : 4, 12) };

            root.Children.Add(BuildSummaryHeader(stats, isDialog));
            root.Children.Add(new Border { Height = 1, Background = Brush("#2C2C2C"), Margin = new Thickness(0, 12, 0, 8) });

            if (stats.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "No projects found. Add a project to start tracking activity.",
                    Foreground = Brush("#666666"),
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 12, 0, 0)
                });
            }
            else
            {
                foreach (var stat in stats)
                    root.Children.Add(BuildProjectCard(stat, isDialog));
            }

            return root;
        }

        private Border BuildSummaryHeader(List<ProjectActivityStats> allStats, bool isDialog)
        {
            var totalTasks = allStats.Sum(s => s.TotalTasks);
            var totalCompleted = allStats.Sum(s => s.CompletedTasks);
            var totalFailed = allStats.Sum(s => s.FailedTasks);
            var totalFinished = totalCompleted + totalFailed;
            var overallSuccessRate = totalFinished > 0 ? (double)totalCompleted / totalFinished : 0;
            var totalRuntime = TimeSpan.FromTicks(allStats.Sum(s => s.TotalDuration.Ticks));
            var activeNow = allStats.Sum(s => s.RunningTasks);
            var totalTokens = allStats.Sum(s => s.TotalTokens);

            var grid = new Grid();
            if (isDialog)
            {
                for (int c = 0; c < 5; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                // 3-column + 2 row grid for narrow settings panel
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var successColor = overallSuccessRate >= 0.7 ? "#5CB85C" : overallSuccessRate >= 0.4 ? "#E0A030" : "#E05555";

            var metrics = new (string label, string value, string color)[]
            {
                ("Total Tasks", totalTasks.ToString(), "#E8E8E8"),
                ("Success Rate", totalFinished > 0 ? $"{overallSuccessRate:P0}" : "N/A", successColor),
                ("Total Runtime", FormatDuration(totalRuntime), "#E8E8E8"),
                ("Active Now", activeNow.ToString(), activeNow > 0 ? "#64B5F6" : "#888888"),
                ("Total Tokens", totalTokens > 0 ? FormatTokenCount(totalTokens) : "N/A", totalTokens > 0 ? "#8899AA" : "#888888")
            };

            for (int i = 0; i < metrics.Length; i++)
            {
                var (label, value, color) = metrics[i];
                var cell = new StackPanel { Margin = new Thickness(0, 4, 8, 4) };
                cell.Children.Add(new TextBlock
                {
                    Text = label,
                    Foreground = Brush("#888888"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI")
                });
                cell.Children.Add(new TextBlock
                {
                    Text = value,
                    Foreground = Brush(color),
                    FontSize = isDialog ? 20 : 16,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 2, 0, 0)
                });

                if (isDialog)
                {
                    Grid.SetColumn(cell, i);
                }
                else
                {
                    Grid.SetColumn(cell, i % 3);
                    Grid.SetRow(cell, i / 3);
                }
                grid.Children.Add(cell);
            }

            return new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = Brush("#1E1E1E"),
                BorderBrush = Brush("#2C2C2C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = grid
            };
        }

        private Border BuildProjectCard(ProjectActivityStats stats, bool isDialog)
        {
            var card = new StackPanel();

            // Header row: color dot + name + task count
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = Brush(stats.ProjectColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var nameBlock = new TextBlock
            {
                Text = stats.ProjectName,
                Foreground = Brush(stats.ProjectColor),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var countBlock = new TextBlock
            {
                Text = stats.TotalTasks == 0 ? "No tasks" :
                       stats.TotalTasks == 1 ? "1 task" :
                       $"{stats.TotalTasks} tasks",
                Foreground = Brush("#888888"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };

            DockPanel.SetDock(countBlock, Dock.Right);
            header.Children.Add(countBlock);
            header.Children.Add(dot);
            header.Children.Add(nameBlock);
            card.Children.Add(header);

            // No tasks? Show placeholder
            if (stats.TotalTasks == 0)
            {
                card.Children.Add(new TextBlock
                {
                    Text = "No activity recorded yet.",
                    Foreground = Brush("#555555"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(18, 0, 0, 0)
                });
                return WrapInCardBorder(card);
            }

            // Running indicator
            if (stats.RunningTasks > 0)
            {
                var runningText = new TextBlock
                {
                    Text = stats.RunningTasks == 1 ? "1 task running" : $"{stats.RunningTasks} tasks running",
                    Foreground = Brush("#64B5F6"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(18, 0, 0, 4)
                };
                card.Children.Add(runningText);
            }

            // Success/failure rate bar
            var finishedCount = stats.CompletedTasks + stats.FailedTasks;
            if (finishedCount > 0)
            {
                var ratePanel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };

                var barWidth = isDialog ? 350.0 : 200.0;
                var rateBar = BuildRateBar(stats.SuccessRate, barWidth);
                ratePanel.Children.Add(rateBar);

                var rateText = new TextBlock
                {
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 3, 0, 0)
                };

                var rateRun = new System.Windows.Documents.Run($"{stats.CompletedTasks} passed")
                    { Foreground = Brush("#5CB85C") };
                var sepRun = new System.Windows.Documents.Run("  |  ")
                    { Foreground = Brush("#444444") };
                var failRun = new System.Windows.Documents.Run($"{stats.FailedTasks} failed")
                    { Foreground = Brush("#E05555") };

                rateText.Inlines.Add(rateRun);
                rateText.Inlines.Add(sepRun);
                rateText.Inlines.Add(failRun);

                if (stats.CancelledTasks > 0)
                {
                    rateText.Inlines.Add(new System.Windows.Documents.Run("  |  ") { Foreground = Brush("#444444") });
                    rateText.Inlines.Add(new System.Windows.Documents.Run($"{stats.CancelledTasks} cancelled") { Foreground = Brush("#E0A030") });
                }

                ratePanel.Children.Add(rateText);
                card.Children.Add(ratePanel);
            }

            // Duration stats
            if (stats.AverageDuration > TimeSpan.Zero)
            {
                var durPanel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

                var avgLabel = new TextBlock
                {
                    Text = "Avg ",
                    Foreground = Brush("#666666"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var avgValue = new TextBlock
                {
                    Text = FormatDuration(stats.AverageDuration),
                    Foreground = Brush("#CCCCCC"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var rangeBlock = new TextBlock
                {
                    Foreground = Brush("#555555"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (stats.ShortestDuration.HasValue && stats.LongestDuration.HasValue &&
                    stats.ShortestDuration.Value != stats.LongestDuration.Value)
                {
                    rangeBlock.Text = $"({FormatDuration(stats.ShortestDuration.Value)} – {FormatDuration(stats.LongestDuration.Value)})";
                    rangeBlock.Margin = new Thickness(8, 0, 0, 0);
                }

                DockPanel.SetDock(rangeBlock, Dock.Right);
                durPanel.Children.Add(rangeBlock);
                durPanel.Children.Add(avgLabel);
                durPanel.Children.Add(avgValue);
                card.Children.Add(durPanel);
            }

            // Token usage
            if (stats.TotalTokens > 0)
            {
                var tokenPanel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var tokenLabel = new TextBlock
                {
                    Text = "Tokens ",
                    Foreground = Brush("#666666"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var tokenValue = new TextBlock
                {
                    Text = $"{FormatTokenCount(stats.TotalInputTokens)} in / {FormatTokenCount(stats.TotalOutputTokens)} out",
                    Foreground = Brush("#8899AA"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                tokenPanel.Children.Add(tokenLabel);
                tokenPanel.Children.Add(tokenValue);
                card.Children.Add(tokenPanel);
            }

            // Sparkline
            if (stats.RecentActivity.Count >= 2)
            {
                var sparkWidth = isDialog ? 400.0 : 200.0;
                var sparkHeight = isDialog ? 40.0 : 28.0;
                var sparkline = BuildSparkline(stats.RecentActivity, sparkWidth, sparkHeight, stats.ProjectColor);
                sparkline.Margin = new Thickness(0, 6, 0, 0);
                card.Children.Add(sparkline);
            }

            return WrapInCardBorder(card);
        }

        private static Border WrapInCardBorder(UIElement child)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brush("#1E1E1E"),
                BorderBrush = Brush("#2C2C2C"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 4, 0, 0),
                Child = child
            };
        }

        private static Border BuildRateBar(double successRate, double width)
        {
            var outer = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = Brush("#333333"),
                Height = 5,
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = true
            };

            var grid = new Grid();
            // Green portion
            if (successRate > 0)
            {
                var green = new Border
                {
                    Background = Brush("#5CB85C"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = width * successRate,
                    CornerRadius = new CornerRadius(3, successRate >= 1 ? 3 : 0, successRate >= 1 ? 3 : 0, 3)
                };
                grid.Children.Add(green);
            }
            // Red portion
            if (successRate < 1)
            {
                var red = new Border
                {
                    Background = Brush("#E05555"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Width = width * (1 - successRate),
                    CornerRadius = new CornerRadius(successRate <= 0 ? 3 : 0, 3, 3, successRate <= 0 ? 3 : 0)
                };
                grid.Children.Add(red);
            }

            outer.Child = grid;
            return outer;
        }

        // ── Sparkline ──────────────────────────────────────────────────

        private static Canvas BuildSparkline(List<SparklinePoint> points, double width, double height, string projectColor)
        {
            var canvas = new Canvas
            {
                Width = width,
                Height = height,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (points.Count < 2) return canvas;

            var paddingY = 4.0;
            var dotRadius = 3.0;
            var usableHeight = height - paddingY * 2;

            var durations = points.Select(p => p.Duration.TotalSeconds).ToList();
            var minDur = durations.Min();
            var maxDur = durations.Max();
            var range = maxDur - minDur;

            // If all durations are same, draw flat line at center
            if (range < 1) range = 1;

            var pointCoords = new List<(double x, double y, SparklinePoint pt)>();
            var step = (width - dotRadius * 2) / (points.Count - 1);

            for (int i = 0; i < points.Count; i++)
            {
                var x = dotRadius + i * step;
                // Invert Y so longer durations are higher
                var normalized = (durations[i] - minDur) / range;
                var y = paddingY + usableHeight * (1 - normalized);
                pointCoords.Add((x, y, points[i]));
            }

            // Fill area under the line
            var fillPoints = new PointCollection();
            fillPoints.Add(new Point(pointCoords[0].x, height));
            foreach (var (x, y, _) in pointCoords)
                fillPoints.Add(new Point(x, y));
            fillPoints.Add(new Point(pointCoords[^1].x, height));

            var fillPolygon = new Polygon
            {
                Points = fillPoints,
                Fill = Brush(projectColor),
                Opacity = 0.1
            };
            canvas.Children.Add(fillPolygon);

            // Line
            var linePoints = new PointCollection();
            foreach (var (x, y, _) in pointCoords)
                linePoints.Add(new Point(x, y));

            var polyline = new Polyline
            {
                Points = linePoints,
                Stroke = Brush(projectColor),
                StrokeThickness = 1.5,
                Opacity = 0.6
            };
            canvas.Children.Add(polyline);

            // Dots
            foreach (var (x, y, pt) in pointCoords)
            {
                var dot = new Ellipse
                {
                    Width = dotRadius * 2,
                    Height = dotRadius * 2,
                    Fill = Brush(pt.Succeeded ? "#5CB85C" : "#E05555")
                };
                dot.ToolTip = $"{FormatDuration(pt.Duration)} - {(pt.Succeeded ? "Completed" : "Failed")} - {pt.Timestamp:HH:mm}";
                Canvas.SetLeft(dot, x - dotRadius);
                Canvas.SetTop(dot, y - dotRadius);
                canvas.Children.Add(dot);
            }

            return canvas;
        }

        // ── Helpers ────────────────────────────────────────────────────

        internal static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        internal static string FormatTokenCount(long count) => Helpers.FormatHelpers.FormatTokenCount(count);

        private static string NormalizePath(string? path) => Helpers.FormatHelpers.NormalizePath(path);

        private static SolidColorBrush Brush(string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                var brush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                brush.Freeze();
                return brush;
            }
        }
    }
}
