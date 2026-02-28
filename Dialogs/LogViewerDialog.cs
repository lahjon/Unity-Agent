using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnityAgent.Managers;

namespace UnityAgent.Dialogs
{
    public static class LogViewerDialog
    {
        public static void Show()
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch { }

            var dlg = new Window
            {
                Title = "Application Log",
                Width = 800,
                Height = 520,
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
                Background = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var root = new DockPanel();

            // Title bar
            var titleBar = new DockPanel { Margin = new Thickness(18, 14, 18, 0) };

            var titleBlock = new TextBlock
            {
                Text = "Application Log",
                Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var logPathBlock = new TextBlock
            {
                Text = AppLogger.GetLogFilePath(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var closeBtn = new Button
            {
                Content = "\u2715",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Padding = new Thickness(6, 2, 6, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.Click += (_, _) => dlg.Close();
            closeBtn.MouseEnter += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            closeBtn.MouseLeave += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);
            titleBar.Children.Add(titleBlock);
            titleBar.Children.Add(logPathBlock);

            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            // Button bar
            var btnBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(18, 10, 18, 0)
            };

            var refreshBtn = new Button
            {
                Content = "Refresh",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };

            var openFileBtn = new Button
            {
                Content = "Open Log File",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            openFileBtn.Click += (_, _) =>
            {
                try
                {
                    var path = AppLogger.GetLogFilePath();
                    if (System.IO.File.Exists(path))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    else
                        DarkDialog.ShowAlert("Log file does not exist yet. Logs will appear once the application starts generating them.", "No Log File");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("LogViewer", $"Failed to open log file: {ex.Message}", ex);
                }
            };

            var viewCombo = new ComboBox
            {
                Width = 130,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                VerticalAlignment = VerticalAlignment.Center
            };
            viewCombo.Items.Add(new ComboBoxItem { Content = "Memory Buffer", Tag = "memory" });
            viewCombo.Items.Add(new ComboBoxItem { Content = "Log File", Tag = "file" });
            viewCombo.SelectedIndex = 0;

            btnBar.Children.Add(viewCombo);
            btnBar.Children.Add(refreshBtn);
            btnBar.Children.Add(openFileBtn);

            DockPanel.SetDock(btnBar, Dock.Top);
            root.Children.Add(btnBar);

            // Log content
            var logBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(10, 8, 10, 10)
            };

            root.Children.Add(logBox);

            void LoadContent()
            {
                var selected = viewCombo.SelectedItem as ComboBoxItem;
                var mode = selected?.Tag as string ?? "memory";

                if (mode == "file")
                {
                    logBox.Text = AppLogger.ReadLogFile();
                }
                else
                {
                    var entries = AppLogger.GetRecentEntries();
                    logBox.Text = entries.Length > 0
                        ? string.Join("\n", entries)
                        : "(No log entries in memory buffer yet)";
                }
                logBox.CaretIndex = logBox.Text.Length;
                logBox.ScrollToEnd();
            }

            refreshBtn.Click += (_, _) => LoadContent();
            viewCombo.SelectionChanged += (_, _) => LoadContent();

            LoadContent();

            outerBorder.Child = root;
            dlg.Content = outerBorder;
            if (owner != null) dlg.Owner = owner;

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) dlg.Close();
                if (ke.Key == Key.F5) LoadContent();
            };

            dlg.ShowDialog();
        }
    }
}
