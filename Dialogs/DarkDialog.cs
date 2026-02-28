using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgenticEngine.Dialogs
{
    public static class DarkDialog
    {
        public static bool ShowConfirm(string message, string title)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("DarkDialog", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 190,
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

            var result = false;
            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            };

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
                Style = Application.Current.TryFindResource("SecondaryBtn") as Style
            };
            cancelBtn.Click += (_, _) => { result = false; dlg.Close(); };

            var confirmBtn = new Button
            {
                Content = "Confirm",
                Padding = new Thickness(18, 8, 18, 8),
                Style = Application.Current.TryFindResource("DangerBtn") as Style
            };
            confirmBtn.Click += (_, _) => { result = true; dlg.Close(); };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(confirmBtn);

            stack.Children.Add(titleBlock);
            stack.Children.Add(msgBlock);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) { result = false; dlg.Close(); }
                if (ke.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) confirmBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            dlg.ShowDialog();
            return result;
        }

        public static void ShowAlert(string message, string title)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("DarkDialog", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 170,
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

            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            };

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okBtn = new Button
            {
                Content = "OK",
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(24, 8, 24, 8),
                Style = Application.Current.TryFindResource("Btn") as Style
            };
            okBtn.Click += (_, _) => dlg.Close();

            btnPanel.Children.Add(okBtn);

            stack.Children.Add(titleBlock);
            stack.Children.Add(msgBlock);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;
            dlg.KeyDown += (_, ke) => { if (ke.Key == Key.Escape || ke.Key == Key.Enter) dlg.Close(); };
            dlg.ShowDialog();
        }

        public static string? ShowTextInput(string title, string prompt, string defaultValue = "")
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("DarkDialog", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = title,
                Width = 420,
                Height = 200,
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

            string? result = null;
            var stack = new StackPanel { Margin = new Thickness(24) };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            stack.Children.Add(new TextBlock
            {
                Text = prompt,
                Foreground = (Brush)Application.Current.FindResource("TextLight"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            var inputBox = new TextBox
            {
                Text = defaultValue,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
                SelectionBrush = (Brush)Application.Current.FindResource("Accent"),
            };
            stack.Children.Add(inputBox);

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

            var okBtn = new Button
            {
                Content = "OK",
                Background = (Brush)Application.Current.FindResource("Accent"),
                Padding = new Thickness(18, 8, 18, 8),
                Style = Application.Current.TryFindResource("Btn") as Style
            };
            okBtn.Click += (_, _) =>
            {
                var text = inputBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    result = text;
                    dlg.Close();
                }
            };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(okBtn);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) dlg.Close();
                if (ke.Key == Key.Enter) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };

            dlg.ContentRendered += (_, _) => { inputBox.Focus(); inputBox.SelectAll(); };
            dlg.ShowDialog();
            return result;
        }
    }
}
