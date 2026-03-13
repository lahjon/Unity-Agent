using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Spritely.Dialogs;
using Spritely.Helpers;

namespace Spritely.Managers
{
    public partial class IdePanelManager
    {
        // ── Main Content Builder ─────────────────────────────────────────

        private StackPanel BuildContent(List<AgentTask> tasks)
        {
            _cachedRoot = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };

            if (_selectedTask != null)
            {
                // Detail view for selected task
                _cachedRoot.Children.Add(BuildDetailView());
            }
            else
            {
                // Task list overview
                _cachedRoot.Children.Add(BuildTaskListView(tasks));
            }

            return _cachedRoot;
        }

        private StackPanel BuildErrorContent(string message)
        {
            var root = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
            root.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = BrushCache.Theme("ErrorRed"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            });
            return root;
        }

        // ── Task List View ───────────────────────────────────────────────

        private StackPanel BuildTaskListView(List<AgentTask> tasks)
        {
            var panel = new StackPanel();

            // Header
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock
            {
                Text = "\uE943",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = BrushCache.Theme("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            header.Children.Add(new TextBlock
            {
                Text = "IDE — Code Changes",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(header);

            if (tasks.Count == 0)
            {
                panel.Children.Add(BuildEmptyState());
                return panel;
            }

            // Summary badge
            var totalFiles = tasks.Sum(t => t.ChangedFiles.Count);
            var runningCount = tasks.Count(t => t.IsRunning);
            var summaryText = $"{tasks.Count} task{(tasks.Count != 1 ? "s" : "")} with changes  \u2022  {totalFiles} file{(totalFiles != 1 ? "s" : "")} modified";
            if (runningCount > 0)
                summaryText += $"  \u2022  {runningCount} running";

            var summaryBorder = new Border
            {
                Background = BrushCache.Get("#1A2332"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 10)
            };
            summaryBorder.Child = new TextBlock
            {
                Text = summaryText,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("AccentBlue")
            };
            panel.Children.Add(summaryBorder);

            // Task cards
            foreach (var task in tasks)
            {
                panel.Children.Add(BuildTaskCard(task));
            }

            return panel;
        }

        private Border BuildEmptyState()
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = "\uE8A5",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 36,
                Foreground = BrushCache.Theme("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "No code changes yet",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushCache.Theme("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Run tasks to see their file modifications here",
                FontSize = 12,
                Foreground = BrushCache.Theme("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            return new Border
            {
                Background = BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                Child = panel
            };
        }

        // ── Task Card ────────────────────────────────────────────────────

        private Border BuildTaskCard(AgentTask task)
        {
            var card = new StackPanel();

            // Task header row
            var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            // Status indicator dot
            var statusDot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = GetTaskStatusBrush(task),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            // Pulse animation for running tasks
            if (task.IsRunning)
            {
                var pulseAnim = new DoubleAnimation
                {
                    From = 1.0, To = 0.3,
                    Duration = TimeSpan.FromSeconds(1),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                statusDot.BeginAnimation(UIElement.OpacityProperty, pulseAnim);
            }

            headerRow.Children.Add(statusDot);

            // Task number badge
            var numberBadge = new Border
            {
                Background = BrushCache.Get("#2A2A3A"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            numberBadge.Child = new TextBlock
            {
                Text = $"#{task.TaskNumber:D4}",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("Accent")
            };
            headerRow.Children.Add(numberBadge);

            // Task description
            headerRow.Children.Add(new TextBlock
            {
                Text = task.ShortDescription,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextLight"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });

            card.Children.Add(headerRow);

            // File count and status row
            var infoRow = new DockPanel { Margin = new Thickness(16, 0, 0, 2) };

            var fileCount = task.ChangedFiles.Count;
            var fileCountText = new TextBlock
            {
                Text = $"{fileCount} file{(fileCount != 1 ? "s" : "")} changed",
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center
            };
            infoRow.Children.Add(fileCountText);

            // Status badge
            var statusBadge = new Border
            {
                Background = GetTaskStatusBadgeBg(task),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            statusBadge.Child = new TextBlock
            {
                Text = task.StatusText,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = GetTaskStatusBrush(task)
            };
            infoRow.Children.Add(statusBadge);

            // Commit hash if available
            if (!string.IsNullOrEmpty(task.CommitHash))
            {
                var hashText = new TextBlock
                {
                    Text = task.CommitHash.Length > 7 ? task.CommitHash[..7] : task.CommitHash,
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = BrushCache.Theme("TextDim"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                infoRow.Children.Add(hashText);
            }

            card.Children.Add(infoRow);

            // Preview of changed files (first 3)
            var previewFiles = task.ChangedFiles.Take(3).ToList();
            if (previewFiles.Count > 0)
            {
                var filePreview = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
                foreach (var file in previewFiles)
                {
                    var fileName = Path.GetFileName(file);
                    var dir = Path.GetDirectoryName(file)?.Replace('\\', '/');
                    var displayText = string.IsNullOrEmpty(dir) ? fileName : $"{dir}/{fileName}";

                    filePreview.Children.Add(new TextBlock
                    {
                        Text = $"\uE8A5  {displayText}",
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = BrushCache.Theme("TextDim"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }

                if (task.ChangedFiles.Count > 3)
                {
                    filePreview.Children.Add(new TextBlock
                    {
                        Text = $"  +{task.ChangedFiles.Count - 3} more",
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe UI"),
                        Foreground = BrushCache.Theme("TextMuted"),
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                }

                card.Children.Add(filePreview);
            }

            // Wrap in clickable border
            var border = new Border
            {
                Background = BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = card,
                BorderThickness = new Thickness(1),
                BorderBrush = BrushCache.Theme("BorderSubtle")
            };

            // Hover animation
            var normalBg = ((SolidColorBrush)BrushCache.Theme("BgSection")).Color;
            var hoverBg = ((SolidColorBrush)BrushCache.Get("#282828")).Color;

            border.MouseEnter += (_, _) =>
            {
                var anim = new ColorAnimation(hoverBg, TimeSpan.FromMilliseconds(150));
                ((SolidColorBrush)border.Background).BeginAnimation(SolidColorBrush.ColorProperty, anim);
                border.BorderBrush = BrushCache.Theme("Accent");
            };
            border.MouseLeave += (_, _) =>
            {
                var anim = new ColorAnimation(normalBg, TimeSpan.FromMilliseconds(200));
                ((SolidColorBrush)border.Background).BeginAnimation(SolidColorBrush.ColorProperty, anim);
                border.BorderBrush = BrushCache.Theme("BorderSubtle");
            };

            // Set non-frozen background for animation
            border.Background = new SolidColorBrush(normalBg);

            // Click handler — find parent ScrollViewer
            border.MouseLeftButtonUp += (_, _) =>
            {
                var sv = FindParentScrollViewer(border);
                if (sv != null)
                    SelectTask(task, sv);
            };

            return border;
        }

        // ── Detail View ──────────────────────────────────────────────────

        private StackPanel BuildDetailView()
        {
            var panel = new StackPanel();
            var task = _selectedTask!;

            // Back button row
            var backRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var backBtn = new Button
            {
                Content = "\uE72B",
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("Accent"),
                ToolTip = "Back to task list",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            backBtn.Click += (_, _) =>
            {
                var sv = FindParentScrollViewer(backBtn);
                if (sv != null) ClearSelection(sv);
            };
            backRow.Children.Add(backBtn);

            // Task title
            backRow.Children.Add(new TextBlock
            {
                Text = $"#{task.TaskNumber:D4} \u2014 {task.ShortDescription}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            panel.Children.Add(backRow);

            // Task meta info
            var metaPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

            // Status badge
            metaPanel.Children.Add(MakeMetaBadge(task.StatusText, GetTaskStatusBrush(task), GetTaskStatusBadgeBg(task)));

            // File count
            metaPanel.Children.Add(MakeMetaBadge(
                $"{task.ChangedFiles.Count} files",
                BrushCache.Theme("AccentBlue"),
                BrushCache.Get("#1A2332")));

            // Commit hash
            if (!string.IsNullOrEmpty(task.CommitHash))
            {
                var hash = task.CommitHash.Length > 7 ? task.CommitHash[..7] : task.CommitHash;
                metaPanel.Children.Add(MakeMetaBadge(hash, BrushCache.Theme("TextSecondary"), BrushCache.Get("#252530")));
            }

            // Project name
            if (!string.IsNullOrEmpty(task.ProjectName))
            {
                metaPanel.Children.Add(MakeMetaBadge(task.ProjectName, BrushCache.Theme("AccentTeal"), BrushCache.Get("#1A2A28")));
            }

            panel.Children.Add(metaPanel);

            // Separator
            panel.Children.Add(MakeSeparator());

            // File tree header
            var fileHeader = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            fileHeader.Children.Add(new TextBlock
            {
                Text = "CHANGED FILES",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,

            });
            panel.Children.Add(fileHeader);

            // File entries
            if (_fileEntries.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No file change details available.",
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = BrushCache.Theme("TextDim"),
                    Margin = new Thickness(4, 8, 0, 0)
                });
            }
            else
            {
                string? lastDir = null;
                foreach (var entry in _fileEntries)
                {
                    // Directory header if different
                    if (entry.Directory != lastDir && !string.IsNullOrEmpty(entry.Directory))
                    {
                        lastDir = entry.Directory;
                        panel.Children.Add(BuildDirectoryHeader(entry.Directory));
                    }
                    else if (string.IsNullOrEmpty(entry.Directory) && lastDir != null)
                    {
                        lastDir = null;
                    }

                    panel.Children.Add(BuildFileEntry(entry, task));
                }
            }

            // Commit diff summary if available
            if (!string.IsNullOrEmpty(task.CommitDiff))
            {
                panel.Children.Add(MakeSeparator());
                panel.Children.Add(BuildCommitDiffSummary(task.CommitDiff));
            }

            return panel;
        }

        // ── File Entry Builder ───────────────────────────────────────────

        private StackPanel BuildFileEntry(IdeFileEntry entry, AgentTask task)
        {
            var wrapper = new StackPanel();

            // File row
            var fileRow = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };

            // Status indicator
            var statusColor = GetFileStatusColor(entry.Status);
            var statusText = GetFileStatusText(entry.Status);

            var statusBadge = new Border
            {
                Background = BrushCache.Get("#1A1A1A"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32
            };
            statusBadge.Child = new TextBlock
            {
                Text = statusText,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = statusColor,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            fileRow.Children.Add(statusBadge);

            // File icon based on extension
            var icon = GetFileIcon(entry.Extension);
            fileRow.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = GetFileIconColor(entry.Extension),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            // File name
            fileRow.Children.Add(new TextBlock
            {
                Text = entry.FileName,
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("TextLight"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // Line stats on the right
            if (entry.LinesAdded > 0 || entry.LinesRemoved > 0)
            {
                var statsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                DockPanel.SetDock(statsPanel, Dock.Right);

                if (entry.LinesAdded > 0)
                {
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"+{entry.LinesAdded}",
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = BrushCache.Get("#4EC969"),
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                if (entry.LinesRemoved > 0)
                {
                    statsPanel.Children.Add(new TextBlock
                    {
                        Text = $"-{entry.LinesRemoved}",
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = BrushCache.Get("#E06C75"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                // Insert before the file name so DockPanel docks it right
                fileRow.Children.Insert(fileRow.Children.Count - 1, statsPanel);
            }

            // Expand/collapse chevron
            var isExpanded = _expandedFilePath == entry.RelativePath;
            var chevron = new TextBlock
            {
                Text = isExpanded ? "\uE70D" : "\uE76C",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = BrushCache.Theme("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(chevron, Dock.Right);
            fileRow.Children.Insert(0, chevron); // Will be docked right

            // Wrap file row in clickable border
            var fileBorder = new Border
            {
                Background = BrushCache.Theme("BgSurface"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = fileRow
            };

            // Hover effect
            fileBorder.MouseEnter += (_, _) => fileBorder.Background = BrushCache.Get("#2A2A2A");
            fileBorder.MouseLeave += (_, _) => fileBorder.Background = BrushCache.Theme("BgSurface");

            fileBorder.MouseLeftButtonUp += (_, _) =>
            {
                var sv = FindParentScrollViewer(fileBorder);
                if (sv != null) ToggleFileDiff(entry.RelativePath, sv);
            };

            wrapper.Children.Add(fileBorder);

            // Inline diff if expanded
            if (isExpanded)
            {
                wrapper.Children.Add(BuildInlineDiff(entry, task));
            }

            return wrapper;
        }

        private Border BuildInlineDiff(IdeFileEntry entry, AgentTask task)
        {
            var diffPanel = new StackPanel();

            // Action buttons toolbar
            var actionBar = new WrapPanel
            {
                Margin = new Thickness(8, 6, 8, 4)
            };

            // View File button
            if (entry.Exists)
            {
                var viewBtn = MakeFileActionButton("\uE8A5", "View File");
                viewBtn.Click += (_, _) =>
                {
                    IdeFileViewerDialog.Show(entry.FullPath);
                };
                actionBar.Children.Add(viewBtn);
            }

            // Compare (side-by-side) button
            var compareBtn = MakeFileActionButton("\uE89A", "Compare");
            // Capture diff for the compare dialog
            string? capturedDiff = null;
            compareBtn.Click += async (_, _) =>
            {
                capturedDiff ??= await GetFileDiffAsync(task, entry.RelativePath);
                IdeCompareDialog.Show(entry.FileName, capturedDiff);
            };
            actionBar.Children.Add(compareBtn);

            // Open in Editor button
            if (entry.Exists)
            {
                var openBtn = MakeFileActionButton("\uE7AC", "Open in Editor");
                openBtn.Click += (_, _) =>
                {
                    IdeFileViewerDialog.OpenFileExternal(entry.FullPath);
                };
                actionBar.Children.Add(openBtn);
            }

            // Show in Explorer button
            if (entry.Exists)
            {
                var explorerBtn = MakeFileActionButton("\uEC50", "Show in Explorer");
                explorerBtn.Click += (_, _) =>
                {
                    IdeFileViewerDialog.ShowInExplorer(entry.FullPath);
                };
                actionBar.Children.Add(explorerBtn);
            }

            // Copy path button
            var copyBtn = MakeFileActionButton("\uE8C8", "Copy Path");
            copyBtn.Click += (_, _) =>
            {
                try { System.Windows.Clipboard.SetText(entry.FullPath); }
                catch { /* clipboard access can fail */ }
            };
            actionBar.Children.Add(copyBtn);

            // Path display
            actionBar.Children.Add(new TextBlock
            {
                Text = entry.RelativePath,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 0, 0)
            });

            diffPanel.Children.Add(actionBar);

            // Separator between toolbar and diff
            diffPanel.Children.Add(new Border
            {
                Height = 1,
                Background = BrushCache.Get("#252525"),
                Margin = new Thickness(0, 2, 0, 0)
            });

            // Loading indicator
            var loadingText = new TextBlock
            {
                Text = "Loading diff...",
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("TextMuted"),
                Margin = new Thickness(8, 4, 4, 4)
            };

            var richTextBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushCache.Get("#0D0D0D"),
                Foreground = BrushCache.Theme("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                MaxHeight = 400,
                Visibility = Visibility.Collapsed
            };

            // Zero-margin paragraphs
            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            richTextBox.Resources.Add(typeof(Paragraph), paraStyle);

            diffPanel.Children.Add(loadingText);
            diffPanel.Children.Add(richTextBox);

            // Load diff asynchronously
            _dispatcher.InvokeAsync(async () =>
            {
                var diff = await GetFileDiffAsync(task, entry.RelativePath);
                capturedDiff = diff;
                RenderDiffContent(richTextBox, diff);
                loadingText.Visibility = Visibility.Collapsed;
                richTextBox.Visibility = Visibility.Visible;
            });

            return new Border
            {
                Background = BrushCache.Get("#0D0D0D"),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                BorderBrush = BrushCache.Get("#333333"),
                BorderThickness = new Thickness(1, 0, 1, 1),
                Margin = new Thickness(8, 0, 0, 4),
                Child = diffPanel
            };
        }

        private static Button MakeFileActionButton(string icon, string tooltip)
        {
            var btn = new Button
            {
                Content = icon,
                ToolTip = tooltip,
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 2, 0),
                Foreground = BrushCache.Theme("AccentBlue")
            };

            btn.MouseEnter += (_, _) => btn.Foreground = BrushCache.Theme("TextPrimary");
            btn.MouseLeave += (_, _) => btn.Foreground = BrushCache.Theme("AccentBlue");

            return btn;
        }

        private static void RenderDiffContent(RichTextBox richTextBox, string diffContent)
        {
            var greenBrush = BrushCache.Get("#4EC969");
            var redBrush = BrushCache.Get("#E06C75");
            var cyanBrush = BrushCache.Get("#56B6C2");
            var mutedBrush = BrushCache.Get("#555555");
            var bodyBrush = BrushCache.Theme("TextBody");

            // Background highlights for add/remove lines
            var addBg = BrushCache.Get("#0D2818");
            var removeBg = BrushCache.Get("#2D0F0F");

            var para = new Paragraph();
            var lines = diffContent.Split('\n');
            var lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                // Skip header lines
                if (line.StartsWith("diff --git") || line.StartsWith("index ") ||
                    line.StartsWith("---") || line.StartsWith("+++") ||
                    line.StartsWith("new file") || line.StartsWith("deleted file") ||
                    line.StartsWith("Binary"))
                    continue;

                Brush fg;
                if (line.StartsWith("@@"))
                    fg = cyanBrush;
                else if (line.StartsWith("+"))
                    fg = greenBrush;
                else if (line.StartsWith("-"))
                    fg = redBrush;
                else
                    fg = bodyBrush;

                var run = new Run(line + "\n") { Foreground = fg };

                // Add subtle background for add/remove lines
                if (line.StartsWith("+") && !line.StartsWith("@@"))
                    run.Background = addBg;
                else if (line.StartsWith("-") && !line.StartsWith("@@"))
                    run.Background = removeBg;

                para.Inlines.Add(run);
            }

            richTextBox.Document.Blocks.Clear();
            richTextBox.Document.Blocks.Add(para);
        }

        // ── Directory Header ─────────────────────────────────────────────

        private static Border BuildDirectoryHeader(string directory)
        {
            return new Border
            {
                Margin = new Thickness(0, 6, 0, 2),
                Child = new TextBlock
                {
                    Text = $"\uE8B7  {directory}",
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = BrushCache.Theme("TextMuted"),
                    FontWeight = FontWeights.SemiBold
                }
            };
        }

        // ── Commit Diff Summary ──────────────────────────────────────────

        private static Border BuildCommitDiffSummary(string commitDiff)
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "COMMIT SUMMARY",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextMuted"),
                Margin = new Thickness(0, 4, 0, 6),

            });

            var diffBox = new TextBlock
            {
                Text = commitDiff,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("TextBody"),
                TextWrapping = TextWrapping.Wrap
            };

            panel.Children.Add(diffBox);

            return new Border
            {
                Background = BrushCache.Get("#141418"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 4, 0, 0),
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(1),
                Child = panel
            };
        }

        // ── UI Helpers ───────────────────────────────────────────────────

        private static Border MakeMetaBadge(string text, Brush fg, Brush bg)
        {
            return new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = fg
                }
            };
        }

        private static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = BrushCache.Theme("BgElevated"),
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        private static SolidColorBrush GetTaskStatusBrush(AgentTask task) => task.Status switch
        {
            AgentTaskStatus.Running => BrushCache.Get("#4EC969"),
            AgentTaskStatus.Completed => BrushCache.Get("#5CB85C"),
            AgentTaskStatus.Failed => BrushCache.Get("#E06C75"),
            AgentTaskStatus.Cancelled => BrushCache.Get("#E0A030"),
            AgentTaskStatus.Committing => BrushCache.Get("#61AFEF"),
            AgentTaskStatus.Paused => BrushCache.Get("#64B5F6"),
            _ => BrushCache.Theme("TextMuted")
        };

        private static SolidColorBrush GetTaskStatusBadgeBg(AgentTask task) => task.Status switch
        {
            AgentTaskStatus.Running => BrushCache.Get("#0D2818"),
            AgentTaskStatus.Completed => BrushCache.Get("#122812"),
            AgentTaskStatus.Failed => BrushCache.Get("#2D0F0F"),
            AgentTaskStatus.Cancelled => BrushCache.Get("#2D2010"),
            AgentTaskStatus.Committing => BrushCache.Get("#0F1A2D"),
            AgentTaskStatus.Paused => BrushCache.Get("#0F1A2D"),
            _ => BrushCache.Get("#1A1A1A")
        };

        private static SolidColorBrush GetFileStatusColor(string status) => status switch
        {
            "M" or "MM" => BrushCache.Get("#E5C07B"),
            "A" or "AM" or "??" => BrushCache.Get("#4EC969"),
            "D" => BrushCache.Get("#E06C75"),
            "R" or "C" => BrushCache.Get("#61AFEF"),
            _ => BrushCache.Theme("TextMuted")
        };

        private static string GetFileStatusText(string status) => status switch
        {
            "M" or "MM" => "MOD",
            "A" or "AM" or "??" => "ADD",
            "D" => "DEL",
            "R" or "C" => "REN",
            _ => status
        };

        private static string GetFileIcon(string extension) => extension switch
        {
            "CS" => "\uE943",
            "XAML" or "XML" => "\uE943",
            "JSON" => "\uE8A5",
            "MD" or "TXT" => "\uE8A5",
            "SLN" or "CSPROJ" => "\uE74C",
            "PNG" or "JPG" or "JPEG" or "GIF" or "SVG" => "\uEB9F",
            _ => "\uE8A5"
        };

        private static SolidColorBrush GetFileIconColor(string extension) => extension switch
        {
            "CS" => BrushCache.Get("#68D391"),
            "XAML" or "XML" => BrushCache.Get("#61AFEF"),
            "JSON" => BrushCache.Get("#E5C07B"),
            "MD" or "TXT" => BrushCache.Theme("TextDim"),
            _ => BrushCache.Theme("TextDim")
        };

        private static ScrollViewer? FindParentScrollViewer(DependencyObject child)
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is ScrollViewer sv) return sv;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

    }
}
