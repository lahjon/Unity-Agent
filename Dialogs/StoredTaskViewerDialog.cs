using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgenticEngine.Dialogs
{
    public static class StoredTaskViewerDialog
    {
        public static void Show(AgentTask task)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("StoredTaskViewer", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = "Stored Task Context",
                Width = 850,
                Height = 560,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Topmost = false,
                ShowInTaskbar = true
            };

            var outerBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgDeep"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var root = new DockPanel();

            // Title bar
            var titleBar = new DockPanel { Margin = new Thickness(18, 14, 18, 0) };

            var titleBlock = new TextBlock
            {
                Text = task.ShortDescription,
                Foreground = (Brush)Application.Current.FindResource("AccentTeal"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 600
            };

            var projectBlock = new TextBlock
            {
                Text = task.ProjectName,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            var closeBtn = new Button
            {
                Content = "\u2715",
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Padding = new Thickness(6, 2, 6, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (_, _) => dlg.Close();
            closeBtn.MouseEnter += (s, _) => ((Button)s).Foreground = (Brush)Application.Current.FindResource("TextPrimary");
            closeBtn.MouseLeave += (s, _) => ((Button)s).Foreground = (Brush)Application.Current.FindResource("TextSubdued");

            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleBlock);
            titleBar.Children.Add(projectBlock);

            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

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

            AddInfoChip("Project", task.ProjectName, Color.FromRgb(0x4D, 0xB6, 0xAC));
            AddInfoChip("Created", task.StartTime.ToString("yyyy-MM-dd HH:mm"), Color.FromRgb(0xAA, 0xAA, 0xAA));

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

            outerBorder.Child = root;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) dlg.Close();
            };

            dlg.ShowDialog();
        }
    }
}
