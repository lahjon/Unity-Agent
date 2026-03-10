using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Splitter drag (Thumb-based, window-relative DIP tracking) ──

        private double _tmDragStartTopHeight;
        private double _tmDragStartBottomHeight;
        private double _tmDragStartMouseY;

        private void TopMiddleSplitter_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isSplitterDragging = true;

            var topRow = RootGrid.RowDefinitions[0];
            var bottomRow = RootGrid.RowDefinitions[2];

            _tmDragStartTopHeight = topRow.ActualHeight;
            _tmDragStartBottomHeight = bottomRow.ActualHeight;
            _tmDragStartMouseY = Mouse.GetPosition(this).Y;

            topRow.Height = new GridLength(_tmDragStartTopHeight);
            bottomRow.Height = new GridLength(_tmDragStartBottomHeight);
        }

        private void TopMiddleSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var topRow = RootGrid.RowDefinitions[0];
            var bottomRow = RootGrid.RowDefinitions[2];

            double currentY = Mouse.GetPosition(this).Y;
            double offset = currentY - _tmDragStartMouseY;

            double newTop = _tmDragStartTopHeight + offset;
            double newBottom = _tmDragStartBottomHeight - offset;

            if (newTop < topRow.MinHeight) { newTop = topRow.MinHeight; newBottom = _tmDragStartTopHeight + _tmDragStartBottomHeight - topRow.MinHeight; }
            if (newBottom < bottomRow.MinHeight) { newBottom = bottomRow.MinHeight; newTop = _tmDragStartTopHeight + _tmDragStartBottomHeight - bottomRow.MinHeight; }

            topRow.Height = new GridLength(newTop);
            bottomRow.Height = new GridLength(newBottom);
        }

        private void TopMiddleSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isSplitterDragging = false;

            var topRow = RootGrid.RowDefinitions[0];
            var bottomRow = RootGrid.RowDefinitions[2];

            double totalHeight = topRow.ActualHeight + bottomRow.ActualHeight;
            if (totalHeight <= 0) return;
            double topProportion = topRow.ActualHeight / totalHeight;
            double bottomProportion = bottomRow.ActualHeight / totalHeight;

            topRow.Height = new GridLength(topProportion, GridUnitType.Star);
            bottomRow.Height = new GridLength(bottomProportion, GridUnitType.Star);
        }

        // ── Task List ↔ Features splitter drag ──
        // Uses window-relative DIP tracking to avoid Thumb coordinate feedback
        // (the Thumb moves as row heights change, skewing element-relative deltas).

        private double _tfDragStartTaskHeight;
        private double _tfDragStartFeaturesHeight;
        private double _tfDragStartMouseY;

        private void TaskFeaturesSplitter_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isSplitterDragging = true;
            _tfDragStartTaskHeight = TaskListRow.ActualHeight;
            _tfDragStartFeaturesHeight = FeaturesPanelRow.ActualHeight;
            _tfDragStartMouseY = Mouse.GetPosition(this).Y;
            TaskListRow.Height = new GridLength(_tfDragStartTaskHeight);
            FeaturesPanelRow.Height = new GridLength(_tfDragStartFeaturesHeight);
        }

        private void TaskFeaturesSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double currentY = Mouse.GetPosition(this).Y;
            double offset = currentY - _tfDragStartMouseY;

            double newTask = _tfDragStartTaskHeight + offset;
            double newFeatures = _tfDragStartFeaturesHeight - offset;

            const double minHeight = 60;
            if (newTask < minHeight) { newTask = minHeight; newFeatures = _tfDragStartTaskHeight + _tfDragStartFeaturesHeight - minHeight; }
            if (newFeatures < minHeight) { newFeatures = minHeight; newTask = _tfDragStartTaskHeight + _tfDragStartFeaturesHeight - minHeight; }

            TaskListRow.Height = new GridLength(newTask);
            FeaturesPanelRow.Height = new GridLength(newFeatures);
        }

        private void TaskFeaturesSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isSplitterDragging = false;
            double totalHeight = TaskListRow.ActualHeight + FeaturesPanelRow.ActualHeight;
            if (totalHeight <= 0) return;
            double taskProportion = TaskListRow.ActualHeight / totalHeight;
            double featuresProportion = FeaturesPanelRow.ActualHeight / totalHeight;
            TaskListRow.Height = new GridLength(taskProportion, GridUnitType.Star);
            FeaturesPanelRow.Height = new GridLength(featuresProportion, GridUnitType.Star);
        }

        // ── Right splitter drag ──

        private void RightSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = RightPanelCol.Width.Value - e.HorizontalChange;
            if (newWidth < RightPanelCol.MinWidth) newWidth = RightPanelCol.MinWidth;
            RightPanelCol.Width = new GridLength(newWidth);
        }

        // ── Chat splitter drag ──

        private void ChatSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newChat = ChatPanelCol.Width.Value - e.HorizontalChange;
            if (newChat < 60) newChat = 60;
            ChatPanelCol.Width = new GridLength(newChat);
        }

        // ── Terminal ───────────────────────────────────────────────

        private void TerminalSend_Click(object sender, RoutedEventArgs e) => _terminalManager?.SendCommand();

        private void TerminalInput_PreviewKeyDown(object sender, KeyEventArgs e) => _terminalManager?.HandleKeyDown(e);

        private void TerminalInterrupt_Click(object sender, RoutedEventArgs e) => _terminalManager?.SendInterrupt();

        // ── Graph Collapse ────────────────────────────────────────────

        private void GraphCollapse_Click(object sender, RoutedEventArgs e) => ToggleGraphCollapse();

        private void GraphHeader_MouseDown(object sender, MouseButtonEventArgs e) => ToggleGraphCollapse();

        private void ToggleGraphCollapse()
        {
            _graphCollapsed = !_graphCollapsed;

            if (_graphCollapsed)
            {
                _graphExpandedHeight = GraphPanelRow.Height;

                GraphPanelRow.MinHeight = 0;
                GraphPanelRow.Height = GridLength.Auto;
                GraphSplitter.Visibility = Visibility.Collapsed;
                NodeGraphPanel.Visibility = Visibility.Collapsed;

                GraphCollapseBtn.Content = "\uE70D";
                GraphCollapseBtn.ToolTip = "Expand graph";
            }
            else
            {
                GraphPanelRow.MinHeight = 60;
                GraphPanelRow.Height = _graphExpandedHeight;
                GraphSplitter.Visibility = Visibility.Visible;
                NodeGraphPanel.Visibility = Visibility.Visible;

                GraphCollapseBtn.Content = "\uE70E";
                GraphCollapseBtn.ToolTip = "Collapse graph";

                NodeGraphPanel.FitToView();
            }
        }

        private void TerminalCollapse_Click(object sender, RoutedEventArgs e) => ToggleTerminalCollapse();

        private void TerminalHeader_MouseDown(object sender, MouseButtonEventArgs e) => ToggleTerminalCollapse();

        private void ToggleTerminalCollapse()
        {
            _terminalCollapsed = !_terminalCollapsed;

            if (_terminalCollapsed)
            {
                _terminalExpandedHeight = TerminalRow.Height;

                TerminalRow.MinHeight = 0;
                TerminalRow.Height = GridLength.Auto;
                TerminalSplitter.Visibility = Visibility.Collapsed;
                TerminalOutput.Visibility = Visibility.Collapsed;
                TerminalInputBar.Visibility = Visibility.Collapsed;
                TerminalRootBar.Visibility = Visibility.Collapsed;
                TerminalTabBar.Visibility = Visibility.Collapsed;

                TerminalCollapseBtn.Content = "\uE70E";
                TerminalCollapseBtn.ToolTip = "Expand terminal";
            }
            else
            {
                TerminalRow.MinHeight = 60;
                TerminalRow.Height = _terminalExpandedHeight;
                TerminalSplitter.Visibility = Visibility.Visible;
                TerminalOutput.Visibility = Visibility.Visible;
                TerminalInputBar.Visibility = Visibility.Visible;
                TerminalRootBar.Visibility = Visibility.Visible;
                TerminalTabBar.Visibility = Visibility.Visible;

                TerminalCollapseBtn.Content = "\uE70D";
                TerminalCollapseBtn.ToolTip = "Collapse terminal";
            }
        }

        // ── Filters ────────────────────────────────────────────────

        private void RefreshFilterCombos()
        {
            if (ActiveFilterCombo == null || HistoryFilterCombo == null) return;

            var allPaths = new HashSet<string>();
            foreach (var p in _projectManager.SavedProjects)
                allPaths.Add(p.Path);
            foreach (var t in _activeTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);
            foreach (var t in _historyTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);
            foreach (var t in _storedTasks)
                if (!string.IsNullOrEmpty(t.ProjectPath)) allPaths.Add(t.ProjectPath);

            var projectNames = allPaths
                .Select(p => new { Path = p, Name = Path.GetFileName(p) })
                .OrderBy(x => x.Name)
                .ToList();

            var activeSelection = ActiveFilterCombo.SelectedItem as ComboBoxItem;
            var activeTag = activeSelection?.Tag as string;
            var historySelection = HistoryFilterCombo.SelectedItem as ComboBoxItem;
            var historyTag = historySelection?.Tag as string;

            ActiveFilterCombo.SelectionChanged -= ActiveFilter_Changed;
            ActiveFilterCombo.Items.Clear();
            ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                ActiveFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            var found = false;
            if (!string.IsNullOrEmpty(activeTag))
            {
                foreach (ComboBoxItem item in ActiveFilterCombo.Items)
                {
                    if (item.Tag as string == activeTag) { ActiveFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) ActiveFilterCombo.SelectedIndex = 0;
            ActiveFilterCombo.SelectionChanged += ActiveFilter_Changed;

            HistoryFilterCombo.SelectionChanged -= HistoryFilter_Changed;
            HistoryFilterCombo.Items.Clear();
            HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
            foreach (var p in projectNames)
                HistoryFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
            found = false;
            if (!string.IsNullOrEmpty(historyTag))
            {
                foreach (ComboBoxItem item in HistoryFilterCombo.Items)
                {
                    if (item.Tag as string == historyTag) { HistoryFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) HistoryFilterCombo.SelectedIndex = 0;
            HistoryFilterCombo.SelectionChanged += HistoryFilter_Changed;

            if (StoredFilterCombo != null)
            {
                var storedSelection = StoredFilterCombo.SelectedItem as ComboBoxItem;
                var storedTag = storedSelection?.Tag as string;

                StoredFilterCombo.SelectionChanged -= StoredFilter_Changed;
                StoredFilterCombo.Items.Clear();
                StoredFilterCombo.Items.Add(new ComboBoxItem { Content = "All Projects", Tag = "" });
                foreach (var p in projectNames)
                    StoredFilterCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Path });
                found = false;
                if (!string.IsNullOrEmpty(storedTag))
                {
                    foreach (ComboBoxItem item in StoredFilterCombo.Items)
                    {
                        if (item.Tag as string == storedTag) { StoredFilterCombo.SelectedItem = item; found = true; break; }
                    }
                }
                if (!found) StoredFilterCombo.SelectedIndex = 0;
                StoredFilterCombo.SelectionChanged += StoredFilter_Changed;
            }


            RefreshStatusFilterCombos();
        }

        private void RefreshStatusFilterCombos()
        {
            if (ActiveStatusFilterCombo == null || HistoryStatusFilterCombo == null) return;

            var statusOptions = new[] { "All Status", "Running", "Queued", "Completed", "Failed", "Cancelled" };

            var activeStatusTag = (ActiveStatusFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            var historyStatusTag = (HistoryStatusFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            ActiveStatusFilterCombo.SelectionChanged -= ActiveStatusFilter_Changed;
            ActiveStatusFilterCombo.Items.Clear();
            foreach (var s in statusOptions)
                ActiveStatusFilterCombo.Items.Add(new ComboBoxItem { Content = s, Tag = s == "All Status" ? "" : s });
            var found = false;
            if (!string.IsNullOrEmpty(activeStatusTag))
            {
                foreach (ComboBoxItem item in ActiveStatusFilterCombo.Items)
                {
                    if (item.Tag as string == activeStatusTag) { ActiveStatusFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) ActiveStatusFilterCombo.SelectedIndex = 0;
            ActiveStatusFilterCombo.SelectionChanged += ActiveStatusFilter_Changed;

            HistoryStatusFilterCombo.SelectionChanged -= HistoryStatusFilter_Changed;
            HistoryStatusFilterCombo.Items.Clear();
            foreach (var s in statusOptions)
                HistoryStatusFilterCombo.Items.Add(new ComboBoxItem { Content = s, Tag = s == "All Status" ? "" : s });
            found = false;
            if (!string.IsNullOrEmpty(historyStatusTag))
            {
                foreach (ComboBoxItem item in HistoryStatusFilterCombo.Items)
                {
                    if (item.Tag as string == historyStatusTag) { HistoryStatusFilterCombo.SelectedItem = item; found = true; break; }
                }
            }
            if (!found) HistoryStatusFilterCombo.SelectedIndex = 0;
            HistoryStatusFilterCombo.SelectionChanged += HistoryStatusFilter_Changed;
        }

        private static bool TaskMatchesSearch(AgentTask t, string search)
        {
            if (string.IsNullOrEmpty(search)) return true;
            if (t.Description != null && t.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Summary != null && t.Summary.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void ApplyActiveFilters()
        {
            if (_activeView == null) return;
            var projectTag = (ActiveFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (ActiveStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);
            var searchText = ActiveSearchBox?.Text?.Trim() ?? "";
            var hasSearch = searchText.Length > 0;

            if (!hasProject && !hasStatus && !hasSearch)
                _activeView.Filter = null;
            else
                _activeView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    if (hasSearch && !TaskMatchesSearch(t, searchText)) return false;
                    return true;
                };
        }

        private void ApplyHistoryFilters()
        {
            if (_historyView == null) return;
            var projectTag = (HistoryFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var statusTag = (HistoryStatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string;
            var hasProject = !string.IsNullOrEmpty(projectTag);
            var hasStatus = !string.IsNullOrEmpty(statusTag);
            var searchText = HistorySearchBox?.Text?.Trim() ?? "";
            var hasSearch = searchText.Length > 0;

            if (!hasProject && !hasStatus && !hasSearch)
                _historyView.Filter = null;
            else
                _historyView.Filter = obj =>
                {
                    if (obj is not AgentTask t) return false;
                    if (hasProject && t.ProjectPath != projectTag) return false;
                    if (hasStatus && t.Status.ToString() != statusTag) return false;
                    if (hasSearch && !TaskMatchesSearch(t, searchText)) return false;
                    return true;
                };
        }

        private void ActiveFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActiveFilters();

        private void HistoryStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyHistoryFilters();

        private void ActiveSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyActiveFilters();

        private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilters();

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != MainTabs) return;
        }

        private void StatisticsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != StatisticsTabs) return;
            if (StatisticsTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent, _projectManager.ProjectPath);
            else if (StatisticsTabs.SelectedItem == GitTabItem)
                _gitPanelManager.RefreshIfNeeded(GitTabContent);
            else if (StatisticsTabs.SelectedItem == TasksTabItem)
                LoadTasksForDisplay();
        }

        // ── Tab Overflow ─────────────────────────────────────────────

        private void SetupMainTabsOverflow()
        {
            MainTabs.ApplyTemplate();
            var btn = MainTabs.Template.FindName("PART_OverflowButton", MainTabs) as Button;
            if (btn != null)
                btn.Click += MainTabsOverflow_Click;
        }

        private void MainTabsOverflow_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var popup = new Popup
            {
                PlacementTarget = btn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = (Brush)FindResource("BgPopup"),
                BorderBrush = (Brush)FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 130,
                MaxHeight = 350
            };

            var stack = new StackPanel();

            foreach (var item in MainTabs.Items)
            {
                if (item is TabItem tab && tab.Visibility == Visibility.Visible)
                {
                    string text = GetTabHeaderText(tab);
                    bool isSelected = tab == MainTabs.SelectedItem;

                    var itemBorder = new Border
                    {
                        Background = isSelected
                            ? (Brush)FindResource("BgHover")
                            : Brushes.Transparent,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 1, 0, 1),
                        Cursor = Cursors.Hand
                    };

                    var textBlock = new TextBlock
                    {
                        Text = text,
                        Foreground = isSelected
                            ? (Brush)FindResource("Accent")
                            : (Brush)FindResource("TextBody"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal
                    };

                    itemBorder.Child = textBlock;

                    var capturedTab = tab;
                    itemBorder.MouseEnter += (_, _) =>
                    {
                        if (capturedTab != MainTabs.SelectedItem)
                            itemBorder.Background = (Brush)FindResource("BgElevated");
                    };
                    itemBorder.MouseLeave += (_, _) =>
                    {
                        if (capturedTab != MainTabs.SelectedItem)
                            itemBorder.Background = Brushes.Transparent;
                    };
                    itemBorder.MouseLeftButtonDown += (_, _) =>
                    {
                        MainTabs.SelectedItem = capturedTab;
                        popup.IsOpen = false;
                    };

                    stack.Children.Add(itemBorder);
                }
            }

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        private static string GetTabHeaderText(TabItem tab)
        {
            if (tab.Header is TextBlock tb) return tb.Text;
            if (tab.Header is StackPanel sp)
            {
                foreach (var child in sp.Children)
                    if (child is TextBlock t) return t.Text;
            }
            if (tab.Header is string s) return s;
            return "Tab";
        }

        // ── Activity Dashboard ───────────────────────────────────────

        private void RefreshActivityDashboard()
        {
            _activityDashboard.MarkDirty();
            _gitPanelManager.MarkDirty();
            if (StatisticsTabs.SelectedItem == ActivityTabItem)
                _activityDashboard.RefreshIfNeeded(ActivityTabContent, _projectManager.ProjectPath);
            else if (StatisticsTabs.SelectedItem == GitTabItem)
                _gitPanelManager.RefreshIfNeeded(GitTabContent);
        }

        private void RefreshInlineProjectStats()
        {
            _projectManager.RefreshProjectList(
                p => _terminalManager?.UpdateWorkingDirectory(p),
                () => _settingsManager.SaveSettings(_projectManager.ProjectPath),
                SyncSettingsForProject);
        }

        // ── Settings Panel Collapse ───────────────────────────────

        private void ToggleSettingsPanel_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = SettingsExpandedPanel.Visibility == Visibility.Visible;
            ApplySettingsPanelCollapsed(collapse);
            _settingsManager.SettingsPanelCollapsed = collapse;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        private void ApplySettingsPanelCollapsed(bool collapsed)
        {
            if (collapsed)
            {
                SettingsExpandedPanel.Visibility = Visibility.Collapsed;
                ProjectsPanelGrid.Visibility = Visibility.Collapsed;
                RightSplitter.Visibility = Visibility.Collapsed;
                SettingsCollapsedStrip.Visibility = Visibility.Visible;
                RightPanelCol.Width = new GridLength(0);
                RightPanelCol.MinWidth = 0;
            }
            else
            {
                SettingsCollapsedStrip.Visibility = Visibility.Collapsed;
                SettingsExpandedPanel.Visibility = Visibility.Visible;
                ProjectsPanelGrid.Visibility = Visibility.Visible;
                RightSplitter.Visibility = Visibility.Visible;
                RightPanelCol.Width = new GridLength(285);
                RightPanelCol.MinWidth = 150;
            }
        }

        // ── Left Panel Collapse ───────────────────────────────

        private void ToggleLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            bool collapse = TaskListPanelBorder.Visibility == Visibility.Visible;
            ApplyLeftPanelCollapsed(collapse);
            _settingsManager.LeftPanelCollapsed = collapse;
            _settingsManager.SaveSettings(_projectManager.ProjectPath);
        }

        private void ApplyLeftPanelCollapsed(bool collapsed)
        {
            if (collapsed)
            {
                TaskListPanelBorder.Visibility = Visibility.Collapsed;
                LeftSplitter.Visibility = Visibility.Collapsed;
                LeftPanelCollapsedStrip.Visibility = Visibility.Visible;
                LeftPanelCol.Width = new GridLength(0);
                LeftPanelCol.MinWidth = 0;
            }
            else
            {
                LeftPanelCollapsedStrip.Visibility = Visibility.Collapsed;
                TaskListPanelBorder.Visibility = Visibility.Visible;
                LeftSplitter.Visibility = Visibility.Visible;
                LeftPanelCol.Width = new GridLength(285);
                LeftPanelCol.MinWidth = 150;
            }
        }

        private static string WrapTooltipText(string text, int maxLineLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength)
                return text;

            var sb = new System.Text.StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (sb.Length > 0) sb.Append('\n');
                var remaining = line;
                while (remaining.Length > maxLineLength)
                {
                    int breakAt = remaining.LastIndexOf(' ', maxLineLength);
                    if (breakAt <= 0) breakAt = maxLineLength;
                    sb.Append(remaining, 0, breakAt);
                    sb.Append('\n');
                    remaining = remaining.Substring(breakAt).TrimStart();
                }
                sb.Append(remaining);
            }
            return sb.ToString();
        }
    }
}
