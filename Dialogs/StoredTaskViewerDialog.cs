using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HappyEngine.Helpers;

namespace HappyEngine.Dialogs
{
    public static class StoredTaskViewerDialog
    {
        public static void Show(AgentTask task)
        {
            var dlg = DarkDialogWindow.Create("Stored Task Context", 850, 560,
                ResizeMode.CanResize, topmost: false, backgroundResource: "BgDeep",
                titleColorResource: "AccentTeal");

            // Customize title
            dlg.TitleTextBlock.Text = task.ShortDescription;
            dlg.TitleTextBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            dlg.TitleTextBlock.MaxWidth = 600;

            // Add project name to title bar
            var projectBlock = new TextBlock
            {
                Text = task.ProjectName,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            dlg.TitleBarPanel.Children.Add(projectBlock);

            var root = new DockPanel();

            // Info bar with metadata
            var infoBar = new WrapPanel { Margin = new Thickness(18, 10, 18, 0) };

            void AddInfoChip(string label, string value, Color color)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = label + ": ",
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI")
                });
                sp.Children.Add(new TextBlock
                {
                    Text = value,
                    Foreground = new SolidColorBrush(color),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold
                });
                chip.Child = sp;
                infoBar.Children.Add(chip);
            }

            AddInfoChip("Project", task.ProjectName, ((SolidColorBrush)BrushCache.Theme("AccentTeal")).Color);
            AddInfoChip("Created", task.StartTime.ToString("yyyy-MM-dd HH:mm"), ((SolidColorBrush)BrushCache.Theme("TextTabHeader")).Color);

            DockPanel.SetDock(infoBar, Dock.Top);
            root.Children.Add(infoBar);

            // Tab control for sections
            var viewCombo = new ComboBox
            {
                Width = 160,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(18, 10, 18, 0),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                VerticalAlignment = VerticalAlignment.Center
            };
            viewCombo.Items.Add(new ComboBoxItem { Content = "Full Output", Tag = "output" });
            viewCombo.Items.Add(new ComboBoxItem { Content = "Stored Prompt", Tag = "prompt" });
            viewCombo.Items.Add(new ComboBoxItem { Content = "Original Prompt", Tag = "original" });
            viewCombo.SelectedIndex = 0;

            DockPanel.SetDock(viewCombo, Dock.Top);
            root.Children.Add(viewCombo);

            // Content area
            var contentBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = (Brush)Application.Current.FindResource("BgAbyss"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(10, 8, 10, 10)
            };

            root.Children.Add(contentBox);

            void LoadContent()
            {
                var selected = viewCombo.SelectedItem as ComboBoxItem;
                var mode = selected?.Tag as string ?? "output";

                contentBox.Text = mode switch
                {
                    "prompt" => !string.IsNullOrWhiteSpace(task.StoredPrompt)
                        ? task.StoredPrompt
                        : "(No stored prompt available)",
                    "original" => !string.IsNullOrWhiteSpace(task.Description)
                        ? task.Description
                        : "(No original prompt available)",
                    _ => !string.IsNullOrWhiteSpace(task.FullOutput)
                        ? task.FullOutput
                        : "(No output captured for this stored task)"
                };
                contentBox.CaretIndex = 0;
                contentBox.ScrollToHome();
            }

            viewCombo.SelectionChanged += (_, _) => LoadContent();
            LoadContent();

            dlg.SetBodyContent(root);
            dlg.ShowDialog();
        }
    }
}
