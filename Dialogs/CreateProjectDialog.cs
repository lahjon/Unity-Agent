using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AgenticEngine.Dialogs
{
    public class CreateProjectResult
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool InitGit { get; set; }
        public bool IsGame { get; set; }
    }

    public static class CreateProjectDialog
    {
        public static CreateProjectResult? Show()
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("CreateProjectDialog", "Could not get MainWindow", ex); }

            var dlg = new Window
            {
                Title = "Create Project",
                Width = 440,
                Height = 390,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Topmost = true,
                ShowInTaskbar = true
            };

            var outerBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgSurface"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            CreateProjectResult? result = null;
            var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Create Project",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Project type selector
            bool isGame = false;
            var typeGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentBrush = (Brush)Application.Current.FindResource("Accent");
            var subtleBrush = (Brush)Application.Current.FindResource("BorderSubtle");
            var surfaceBrush = (Brush)Application.Current.FindResource("BgElevated");

            var appBorder = new Border
            {
                Background = surfaceBrush,
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0, 12, 0, 12)
            };
            var appStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            appStack.Children.Add(new TextBlock
            {
                Text = "\uE770",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            appStack.Children.Add(new TextBlock
            {
                Text = "App",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });
            appBorder.Child = appStack;
            Grid.SetColumn(appBorder, 0);

            var gameBorder = new Border
            {
                Background = surfaceBrush,
                BorderBrush = subtleBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0, 12, 0, 12)
            };
            var gameStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            gameStack.Children.Add(new TextBlock
            {
                Text = "\uE7FC",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            gameStack.Children.Add(new TextBlock
            {
                Text = "Game",
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });
            gameBorder.Child = gameStack;
            Grid.SetColumn(gameBorder, 2);

            void SelectType(bool game)
            {
                isGame = game;
                appBorder.BorderBrush = game ? subtleBrush : accentBrush;
                ((TextBlock)appStack.Children[0]).Foreground = game
                    ? (Brush)Application.Current.FindResource("TextDim")
                    : accentBrush;
                ((TextBlock)appStack.Children[1]).Foreground = game
                    ? (Brush)Application.Current.FindResource("TextDim")
                    : (Brush)Application.Current.FindResource("TextPrimary");

                gameBorder.BorderBrush = game ? accentBrush : subtleBrush;
                ((TextBlock)gameStack.Children[0]).Foreground = game
                    ? accentBrush
                    : (Brush)Application.Current.FindResource("TextDim");
                ((TextBlock)gameStack.Children[1]).Foreground = game
                    ? (Brush)Application.Current.FindResource("TextPrimary")
                    : (Brush)Application.Current.FindResource("TextDim");
            }

            appBorder.MouseLeftButtonDown += (_, _) => SelectType(false);
            gameBorder.MouseLeftButtonDown += (_, _) => SelectType(true);

            typeGrid.Children.Add(appBorder);
            typeGrid.Children.Add(gameBorder);
            stack.Children.Add(typeGrid);

            // Name field
            stack.Children.Add(new TextBlock
            {
                Text = "Project Name",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            var nameBox = new TextBox
            {
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                CaretBrush = (Brush)Application.Current.FindResource("TextPrimary")
            };
            stack.Children.Add(nameBox);

            // Path field
            stack.Children.Add(new TextBlock
            {
                Text = "Location",
                Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 10, 0, 4)
            });

            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathBox = new TextBox
            {
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                CaretBrush = (Brush)Application.Current.FindResource("TextPrimary")
            };
            Grid.SetColumn(pathBox, 0);
            pathGrid.Children.Add(pathBox);

            var browseBtn = new Button
            {
                Content = "Browse",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style
            };
            browseBtn.Click += (_, _) =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select parent folder for the new project",
                    UseDescriptionForTitle = true
                };
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    pathBox.Text = dialog.SelectedPath;
                }
            };
            Grid.SetColumn(browseBtn, 1);
            pathGrid.Children.Add(browseBtn);
            stack.Children.Add(pathGrid);

            // Git init toggle
            var gitToggle = new ToggleButton
            {
                Content = "Initialize Git Repository",
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                IsChecked = true,
                Margin = new Thickness(0, 12, 0, 0),
                Style = Application.Current.TryFindResource("ToggleSwitch") as Style
            };
            stack.Children.Add(gitToggle);

            // Full path preview
            var fullPathBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(fullPathBlock);

            void UpdateFullPath()
            {
                var n = nameBox.Text?.Trim() ?? "";
                var p = pathBox.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(p))
                {
                    var combined = string.IsNullOrEmpty(n) ? p : System.IO.Path.Combine(p, n);
                    fullPathBlock.Text = combined;
                    fullPathBlock.ToolTip = combined;
                    fullPathBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    fullPathBlock.Visibility = Visibility.Collapsed;
                }
            }

            nameBox.TextChanged += (_, _) => UpdateFullPath();
            pathBox.TextChanged += (_, _) => UpdateFullPath();

            // Error label
            var errorBlock = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("Danger"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(errorBlock);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style
            };
            cancelBtn.Click += (_, _) => dlg.Close();

            var createBtn = new Button
            {
                Content = "Create",
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(18, 8, 18, 8),
                Style = Application.Current.TryFindResource("Btn") as Style
            };
            createBtn.Click += (_, _) =>
            {
                var name = nameBox.Text?.Trim() ?? "";
                var path = pathBox.Text?.Trim() ?? "";

                if (string.IsNullOrEmpty(name))
                {
                    errorBlock.Text = "Project name is required.";
                    errorBlock.Visibility = Visibility.Visible;
                    nameBox.Focus();
                    return;
                }

                if (string.IsNullOrEmpty(path))
                {
                    errorBlock.Text = "Location is required.";
                    errorBlock.Visibility = Visibility.Visible;
                    pathBox.Focus();
                    return;
                }

                try
                {
                    // Validate path characters
                    var _ = System.IO.Path.GetFullPath(path);
                }
                catch
                {
                    errorBlock.Text = "Invalid path.";
                    errorBlock.Visibility = Visibility.Visible;
                    pathBox.Focus();
                    return;
                }

                result = new CreateProjectResult
                {
                    Name = name,
                    Path = System.IO.Path.Combine(path, name),
                    InitGit = gitToggle.IsChecked == true,
                    IsGame = isGame
                };
                dlg.Close();
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(createBtn);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) dlg.Close();
                if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                    createBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            nameBox.Focus();
            dlg.ShowDialog();
            return result;
        }
    }
}
