using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UnityAgent.Dialogs
{
    public static class DarkDialog
    {
        public static bool ShowConfirm(string message, string title)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch { }

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
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var result = false;
            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
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
            dlg.KeyDown += (_, ke) => { if (ke.Key == Key.Escape) { result = false; dlg.Close(); } };
            dlg.ShowDialog();
            return result;
        }

        public static void ShowAlert(string message, string title)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch { }

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
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var stack = new StackPanel { Margin = new Thickness(24) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
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
                Background = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
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
    }
}
