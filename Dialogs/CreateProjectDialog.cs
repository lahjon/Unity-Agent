using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace UnityAgent.Dialogs
{
    public class CreateProjectResult
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool InitGit { get; set; }
    }

    public static class CreateProjectDialog
    {
        public static CreateProjectResult? Show()
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("CreateProjectDialog", $"Could not get MainWindow: {ex.Message}"); }

            var dlg = new Window
            {
                Title = "Create Project",
                Width = 440,
                Height = 295,
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
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Name field
            stack.Children.Add(new TextBlock
            {
                Text = "Project Name",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            });

            var nameBox = new TextBox
            {
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
            };
            stack.Children.Add(nameBox);

            // Path field
            stack.Children.Add(new TextBlock
            {
                Text = "Location",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
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
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
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
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
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
                Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
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
                Background = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
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
                    InitGit = gitToggle.IsChecked == true
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
            };

            nameBox.Focus();
            dlg.ShowDialog();
            return result;
        }
    }
}
