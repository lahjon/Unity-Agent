using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgenticEngine.Managers;
using AgenticEngine.Models;

namespace AgenticEngine.Dialogs
{
    public static class ActivityDashboardDialog
    {
        public static void Show(
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks,
            List<ProjectEntry> savedProjects)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("ActivityDashboard", $"MainWindow not available: {ex.Message}"); }

            var dlg = new Window
            {
                Title = "Activity Dashboard",
                Width = 900,
                Height = 620,
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
                Text = "Activity Dashboard",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
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
                Background = (Brush)Application.Current.FindResource("BorderSubtle"),
                Foreground = (Brush)Application.Current.FindResource("TextTabHeader"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };

            btnBar.Children.Add(refreshBtn);

            DockPanel.SetDock(btnBar, Dock.Top);
            root.Children.Add(btnBar);

            // Main content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var manager = new ActivityDashboardManager(activeTasks, historyTasks, savedProjects);

            void LoadContent()
            {
                scrollViewer.Content = manager.BuildDashboardContent(isDialog: true);
            }

            LoadContent();

            refreshBtn.Click += (_, _) => LoadContent();

            root.Children.Add(scrollViewer);
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
