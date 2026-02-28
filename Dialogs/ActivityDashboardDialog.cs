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
            var (dlg, outerBorder) = DialogFactory.CreateDarkWindow("Activity Dashboard", 900, 620,
                ResizeMode.CanResize, topmost: false, backgroundResource: "BgDeep");

            var root = new DockPanel();

            // Title bar
            var (titleBar, _) = DialogFactory.CreateTitleBar(dlg, "Activity Dashboard");

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

            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.F5) LoadContent();
            };

            dlg.ShowDialog();
        }
    }
}
