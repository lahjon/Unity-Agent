using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AgenticEngine.Dialogs
{
    public static class DialogFactory
    {
        /// <summary>
        /// Creates a pre-configured dark-themed Window with transparent background,
        /// standard border, drag support, owner centering, and Escape-to-close handler.
        /// Sets dlg.Content = outerBorder and dlg.Owner automatically.
        /// Caller should set outerBorder.Child to their content, then call dlg.ShowDialog().
        /// </summary>
        public static (Window dlg, Border outerBorder) CreateDarkWindow(
            string title, double width, double height,
            ResizeMode resizeMode = ResizeMode.NoResize,
            bool topmost = true,
            string backgroundResource = "BgSurface")
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; }
            catch (Exception ex) { Managers.AppLogger.Debug("DialogFactory", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                ResizeMode = resizeMode,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Topmost = topmost,
                ShowInTaskbar = true
            };

            var outerBorder = new Border
            {
                Background = (Brush)Application.Current.FindResource(backgroundResource),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            if (owner != null) dlg.Owner = owner;
            dlg.KeyDown += (_, ke) => { if (ke.Key == Key.Escape) dlg.Close(); };
            dlg.Content = outerBorder;

            return (dlg, outerBorder);
        }

        /// <summary>
        /// Creates a standard title bar DockPanel with a close button (docked right) and title TextBlock.
        /// Additional elements can be added to the returned titleBar after the titleBlock.
        /// </summary>
        public static (DockPanel titleBar, TextBlock titleBlock) CreateTitleBar(
            Window dlg, string title, string titleColorResource = "Accent")
        {
            var titleBar = new DockPanel { Margin = new Thickness(18, 14, 18, 0) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = (Brush)Application.Current.FindResource(titleColorResource),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
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

            return (titleBar, titleBlock);
        }
    }
}
