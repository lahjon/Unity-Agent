using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Spritely.Helpers;

namespace Spritely.Dialogs
{
    /// <summary>
    /// Shows a side-by-side comparison of before/after file content from a unified diff.
    /// Also supports a unified diff view with toggle.
    /// </summary>
    public static class IdeCompareDialog
    {
        public static void Show(string fileName, string diffContent, string? originalContent = null, string? modifiedContent = null)
        {
            var dlg = DarkDialogWindow.Create(
                $"Compare — {fileName}",
                1100, 700,
                ResizeMode.CanResizeWithGrip);

            var layout = new DockPanel { Margin = new Thickness(0) };

            // Toolbar with view toggle
            var toolbar = new DockPanel
            {
                Background = BrushCache.Get("#141414"),
                Margin = new Thickness(0)
            };

            var toolbarContent = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };

            var fileLabel = new TextBlock
            {
                Text = fileName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = BrushCache.Theme("TextLight"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            toolbarContent.Children.Add(fileLabel);

            // View toggle buttons
            var unifiedBtn = MakeToggleButton("Unified", true);
            var sideBySideBtn = MakeToggleButton("Side by Side", false);

            toolbarContent.Children.Add(unifiedBtn);
            toolbarContent.Children.Add(sideBySideBtn);

            toolbar.Children.Add(toolbarContent);

            var toolbarBorder = new Border
            {
                Child = toolbar,
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            DockPanel.SetDock(toolbarBorder, Dock.Top);
            layout.Children.Add(toolbarBorder);

            // Content area (swappable)
            var contentHost = new Border { Background = BrushCache.Get("#0A0A0A") };

            // Build both views
            var unifiedView = BuildUnifiedDiffView(diffContent);
            var sideBySideView = BuildSideBySideView(diffContent);

            contentHost.Child = unifiedView;

            // Toggle behavior
            unifiedBtn.Click += (_, _) =>
            {
                contentHost.Child = unifiedView;
                unifiedBtn.Background = BrushCache.Get("#333340");
                unifiedBtn.Foreground = BrushCache.Theme("TextPrimary");
                sideBySideBtn.Background = BrushCache.Get("#1A1A1A");
                sideBySideBtn.Foreground = BrushCache.Theme("TextMuted");
            };
            sideBySideBtn.Click += (_, _) =>
            {
                contentHost.Child = sideBySideView;
                sideBySideBtn.Background = BrushCache.Get("#333340");
                sideBySideBtn.Foreground = BrushCache.Theme("TextPrimary");
                unifiedBtn.Background = BrushCache.Get("#1A1A1A");
                unifiedBtn.Foreground = BrushCache.Theme("TextMuted");
            };

            layout.Children.Add(contentHost);

            dlg.SetBodyContent(layout);
            dlg.ShowDialog();
        }

        private static Button MakeToggleButton(string label, bool isActive)
        {
            return new Button
            {
                Content = label,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = isActive ? BrushCache.Theme("TextPrimary") : BrushCache.Theme("TextMuted"),
                Background = isActive ? BrushCache.Get("#333340") : BrushCache.Get("#1A1A1A"),
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 2, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        private static ScrollViewer BuildUnifiedDiffView(string diffContent)
        {
            var outputBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = BrushCache.Get("#0A0A0A"),
                Foreground = BrushCache.Theme("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 8, 12, 8)
            };

            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            outputBox.Resources.Add(typeof(Paragraph), paraStyle);

            var para = new Paragraph();
            foreach (var line in diffContent.Split('\n'))
            {
                if (line.StartsWith("diff --git") || line.StartsWith("index ") ||
                    line.StartsWith("---") || line.StartsWith("+++") ||
                    line.StartsWith("new file") || line.StartsWith("deleted file") ||
                    line.StartsWith("Binary"))
                    continue;

                Brush fg;
                Brush? bg = null;

                if (line.StartsWith("@@"))
                {
                    fg = BrushCache.Get("#56B6C2");
                    bg = BrushCache.Get("#1A2030");
                }
                else if (line.StartsWith("+"))
                {
                    fg = BrushCache.Get("#4EC969");
                    bg = BrushCache.Get("#0D2818");
                }
                else if (line.StartsWith("-"))
                {
                    fg = BrushCache.Get("#E06C75");
                    bg = BrushCache.Get("#2D0F0F");
                }
                else
                {
                    fg = BrushCache.Theme("TextBody");
                }

                var run = new Run(line + "\n") { Foreground = fg };
                if (bg != null)
                    run.Background = bg;
                para.Inlines.Add(run);
            }

            outputBox.Document.Blocks.Clear();
            outputBox.Document.Blocks.Add(para);

            return new ScrollViewer
            {
                Content = outputBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private static Grid BuildSideBySideView(string diffContent)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Headers
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var leftHeader = new Border
            {
                Background = BrushCache.Get("#1A0E0E"),
                Padding = new Thickness(12, 4, 12, 4),
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            leftHeader.Child = new TextBlock
            {
                Text = "REMOVED",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Get("#E06C75")
            };
            Grid.SetColumn(leftHeader, 0);
            Grid.SetRow(leftHeader, 0);
            grid.Children.Add(leftHeader);

            var rightHeader = new Border
            {
                Background = BrushCache.Get("#0D1A14"),
                Padding = new Thickness(12, 4, 12, 4),
                BorderBrush = BrushCache.Theme("BorderSubtle"),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            rightHeader.Child = new TextBlock
            {
                Text = "ADDED",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Get("#4EC969")
            };
            Grid.SetColumn(rightHeader, 2);
            Grid.SetRow(rightHeader, 0);
            grid.Children.Add(rightHeader);

            // Divider
            var divider = new Border
            {
                Width = 1,
                Background = BrushCache.Theme("BorderSubtle")
            };
            Grid.SetColumn(divider, 1);
            Grid.SetRowSpan(divider, 2);
            grid.Children.Add(divider);

            // Parse diff into left/right lines
            var leftBox = CreateSideBox();
            var rightBox = CreateSideBox();
            ParseDiffSideBySide(diffContent, leftBox, rightBox);

            var leftScroll = new ScrollViewer
            {
                Content = leftBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushCache.Get("#0A0A0A")
            };
            var rightScroll = new ScrollViewer
            {
                Content = rightBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BrushCache.Get("#0A0A0A")
            };

            // Sync scrolling between left and right
            leftScroll.ScrollChanged += (_, e) =>
            {
                rightScroll.ScrollToVerticalOffset(e.VerticalOffset);
                rightScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            };
            rightScroll.ScrollChanged += (_, e) =>
            {
                leftScroll.ScrollToVerticalOffset(e.VerticalOffset);
                leftScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            };

            Grid.SetColumn(leftScroll, 0);
            Grid.SetRow(leftScroll, 1);
            Grid.SetColumn(rightScroll, 2);
            Grid.SetRow(rightScroll, 1);
            grid.Children.Add(leftScroll);
            grid.Children.Add(rightScroll);

            return grid;
        }

        private static RichTextBox CreateSideBox()
        {
            var box = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = BrushCache.Get("#0A0A0A"),
                Foreground = BrushCache.Theme("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };

            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            box.Resources.Add(typeof(Paragraph), paraStyle);

            return box;
        }

        private static void ParseDiffSideBySide(string diffContent, RichTextBox leftBox, RichTextBox rightBox)
        {
            var leftPara = new Paragraph();
            var rightPara = new Paragraph();

            foreach (var line in diffContent.Split('\n'))
            {
                // Skip headers
                if (line.StartsWith("diff --git") || line.StartsWith("index ") ||
                    line.StartsWith("---") || line.StartsWith("+++") ||
                    line.StartsWith("new file") || line.StartsWith("deleted file") ||
                    line.StartsWith("Binary"))
                    continue;

                if (line.StartsWith("@@"))
                {
                    // Hunk header on both sides
                    var hunkRun = new Run(line + "\n")
                    {
                        Foreground = BrushCache.Get("#56B6C2"),
                        Background = BrushCache.Get("#1A2030")
                    };
                    leftPara.Inlines.Add(hunkRun);
                    rightPara.Inlines.Add(new Run(line + "\n")
                    {
                        Foreground = BrushCache.Get("#56B6C2"),
                        Background = BrushCache.Get("#1A2030")
                    });
                }
                else if (line.StartsWith("-"))
                {
                    // Removed line on left, blank on right
                    leftPara.Inlines.Add(new Run(line[1..] + "\n")
                    {
                        Foreground = BrushCache.Get("#E06C75"),
                        Background = BrushCache.Get("#2D0F0F")
                    });
                    rightPara.Inlines.Add(new Run("\n")
                    {
                        Foreground = BrushCache.Get("#333333"),
                        Background = BrushCache.Get("#141414")
                    });
                }
                else if (line.StartsWith("+"))
                {
                    // Added line on right, blank on left
                    leftPara.Inlines.Add(new Run("\n")
                    {
                        Foreground = BrushCache.Get("#333333"),
                        Background = BrushCache.Get("#141414")
                    });
                    rightPara.Inlines.Add(new Run(line[1..] + "\n")
                    {
                        Foreground = BrushCache.Get("#4EC969"),
                        Background = BrushCache.Get("#0D2818")
                    });
                }
                else
                {
                    // Context line on both sides
                    var text = line.Length > 0 ? line[1..] : line;
                    leftPara.Inlines.Add(new Run(text + "\n") { Foreground = BrushCache.Theme("TextBody") });
                    rightPara.Inlines.Add(new Run(text + "\n") { Foreground = BrushCache.Theme("TextBody") });
                }
            }

            leftBox.Document.Blocks.Clear();
            leftBox.Document.Blocks.Add(leftPara);
            rightBox.Document.Blocks.Clear();
            rightBox.Document.Blocks.Add(rightPara);
        }
    }
}
