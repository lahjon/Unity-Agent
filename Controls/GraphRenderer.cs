using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgenticEngine.Helpers;
using AgenticEngine.Managers;

namespace AgenticEngine.Controls
{
    internal class GraphRenderer
    {
        private readonly Canvas _canvas;
        private readonly Dictionary<string, Border> _nodeBorders = new();
        private readonly List<UIElement> _edgeElements = new();

        public IReadOnlyDictionary<string, Border> NodeBorders => _nodeBorders;
        public IReadOnlyList<UIElement> EdgeElements => _edgeElements;

        // Events for context menu actions
        public event Action<AgentTask>? CancelRequested;
        public event Action<AgentTask>? PauseResumeRequested;
        public event Action<AgentTask>? ShowOutputRequested;
        public event Action<AgentTask>? CopyPromptRequested;
        public event Action<AgentTask>? RevertRequested;
        public event Action<AgentTask>? ContinueRequested;
        public event Action<AgentTask>? ForceStartRequested;
        public event Action<AgentTask>? DependenciesRemoved;

        // Delegate for wiring interaction events to nodes
        public Action<Border, AgentTask>? OnNodeCreated;

        public GraphRenderer(Canvas canvas)
        {
            _canvas = canvas;
        }

        public void ClearAll()
        {
            _canvas.Children.Clear();
            _nodeBorders.Clear();
        }

        public void ClearForRedraw()
        {
            _canvas.Children.Clear();
            _nodeBorders.Clear();
        }

        public void RemoveEdgeElements()
        {
            foreach (var elem in _edgeElements)
                _canvas.Children.Remove(elem);
        }

        public void DrawEmptyState()
        {
            var emptyText = new TextBlock
            {
                Text = "No active tasks \u2014 launch a task to see it here",
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                FontStyle = FontStyles.Italic
            };
            Canvas.SetLeft(emptyText, 40);
            Canvas.SetTop(emptyText, 30);
            _canvas.Children.Add(emptyText);
        }

        // ── Edge Drawing ─────────────────────────────────────────────

        public void DrawAllEdges(
            List<AgentTask> tasks,
            FileLockManager fileLockManager,
            Dictionary<string, Point> nodePositions,
            HashSet<string> highlightedEdgeKeys)
        {
            _edgeElements.Clear();
            bool hasHighlight = highlightedEdgeKeys.Count > 0;

            // 1) Dependency edges (solid blue)
            foreach (var task in tasks)
            {
                if (task.DependencyTaskIds != null)
                {
                    foreach (var depId in task.DependencyTaskIds)
                    {
                        if (nodePositions.ContainsKey(depId) && nodePositions.ContainsKey(task.Id))
                        {
                            var from = nodePositions[depId];
                            var to = nodePositions[task.Id];
                            string edgeKey = $"{depId}->{task.Id}";
                            bool highlighted = highlightedEdgeKeys.Contains(edgeKey);
                            string color = highlighted ? "#90CAF9" : "#64B5F6";
                            double thickness = highlighted ? 3 : 2;
                            double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                            DrawBezierEdge(from, to, color, false, thickness, opacity);
                            DrawArrowHead(from, to, color, opacity);
                        }
                    }
                }

                // BlockedByTaskId edge (orange)
                if (!string.IsNullOrEmpty(task.BlockedByTaskId) &&
                    nodePositions.ContainsKey(task.BlockedByTaskId) &&
                    nodePositions.ContainsKey(task.Id))
                {
                    var from = nodePositions[task.BlockedByTaskId];
                    var to = nodePositions[task.Id];
                    string edgeKey = $"{task.BlockedByTaskId}->{task.Id}";
                    bool highlighted = highlightedEdgeKeys.Contains(edgeKey);
                    string color = highlighted ? "#FFB74D" : "#FFA726";
                    double thickness = highlighted ? 3 : 2;
                    double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                    DrawBezierEdge(from, to, color, false, thickness, opacity);
                    DrawArrowHead(from, to, color, opacity);
                }
            }

            // 2) File-lock edges (dashed red)
            var queuedInfos = fileLockManager.QueuedTaskInfos;
            foreach (var kvp in queuedInfos)
            {
                var info = kvp.Value;
                if (info.BlockedByTaskIds != null)
                {
                    foreach (var blockerId in info.BlockedByTaskIds)
                    {
                        if (nodePositions.ContainsKey(blockerId) && nodePositions.ContainsKey(kvp.Key))
                        {
                            var from = nodePositions[blockerId];
                            var to = nodePositions[kvp.Key];
                            string edgeKey = $"{blockerId}->{kvp.Key}";
                            bool highlighted = highlightedEdgeKeys.Contains(edgeKey);
                            string color = highlighted ? "#EF9A9A" : "#E05555";
                            double thickness = highlighted ? 3 : 2;
                            double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                            DrawBezierEdge(from, to, color, true, thickness, opacity);
                            DrawArrowHead(from, to, color, opacity);
                        }
                    }
                }
            }

            // 3) Parent-child edges (green L-shaped connectors)
            foreach (var task in tasks)
            {
                if (task.ChildTaskIds == null || task.ChildTaskIds.Count == 0) continue;

                foreach (var childId in task.ChildTaskIds)
                {
                    if (!nodePositions.ContainsKey(task.Id) || !nodePositions.ContainsKey(childId))
                        continue;

                    var parentPos = nodePositions[task.Id];
                    var childPos = nodePositions[childId];

                    string edgeKey = $"{task.Id}=>{childId}";
                    bool highlighted = highlightedEdgeKeys.Contains(edgeKey);
                    double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                    var greenBrush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(highlighted ? "#81C784" : "#66BB6A"));
                    greenBrush.Opacity = opacity;

                    double startX = parentPos.X + GraphLayoutEngine.NodeWidth / 2;
                    double startY = parentPos.Y + GraphLayoutEngine.NodeHeight;
                    double midY = childPos.Y + GraphLayoutEngine.NodeHeight / 2;

                    var vLine = new Line
                    {
                        X1 = startX, Y1 = startY,
                        X2 = startX, Y2 = midY,
                        Stroke = greenBrush,
                        StrokeThickness = highlighted ? 3 : 2,
                        IsHitTestVisible = false
                    };
                    _canvas.Children.Add(vLine);
                    _edgeElements.Add(vLine);

                    var hLine = new Line
                    {
                        X1 = startX, Y1 = midY,
                        X2 = childPos.X, Y2 = midY,
                        Stroke = greenBrush,
                        StrokeThickness = highlighted ? 3 : 2,
                        IsHitTestVisible = false
                    };
                    _canvas.Children.Add(hLine);
                    _edgeElements.Add(hLine);

                    double arrowSize = 8;
                    var arrowTip = new Point(childPos.X, midY);
                    var arrowP2 = new Point(childPos.X - arrowSize, midY - arrowSize * 0.4);
                    var arrowP3 = new Point(childPos.X - arrowSize, midY + arrowSize * 0.4);
                    var arrowBrush = BrushCache.Get(highlighted ? "#81C784" : "#66BB6A");
                    var arrowPolygon = new Polygon
                    {
                        Points = new PointCollection { arrowTip, arrowP2, arrowP3 },
                        Fill = arrowBrush,
                        IsHitTestVisible = false,
                        Opacity = opacity
                    };
                    _canvas.Children.Add(arrowPolygon);
                    _edgeElements.Add(arrowPolygon);
                }
            }
        }

        // ── Node Drawing ─────────────────────────────────────────────

        public void DrawNode(
            AgentTask task,
            List<AgentTask> allTasks,
            Dictionary<string, Point> nodePositions,
            string? selectedNodeId,
            HashSet<string> highlightedNodeIds,
            ScrollViewer scrollViewer,
            ScaleTransform scaleTransform,
            TranslateTransform translateTransform,
            ObservableCollection<AgentTask>? activeTasks)
        {
            if (!nodePositions.TryGetValue(task.Id, out var pos))
                return;

            // Viewport culling
            if (IsNodeOutsideViewport(pos, scrollViewer, scaleTransform, translateTransform))
                return;

            bool isSelected = selectedNodeId == task.Id;
            bool isHighlighted = highlightedNodeIds.Contains(task.Id);
            bool hasFocusedSelection = highlightedNodeIds.Count > 0;
            double nodeOpacity = hasFocusedSelection && !isHighlighted && !isSelected ? 0.3 : 1.0;

            var statusColor = (Color)ColorConverter.ConvertFromString(task.StatusColor);
            var statusBrush = new SolidColorBrush(statusColor);
            bool isParent = task.HasChildren;
            bool isSubtask = task.IsSubTask;

            var nodeBorder = new Border
            {
                Width = GraphLayoutEngine.NodeWidth,
                Height = GraphLayoutEngine.NodeHeight,
                Background = new SolidColorBrush(isSelected
                    ? Color.FromArgb(255, 50, 50, 60)
                    : Color.FromArgb(230, 40, 40, 48)),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9))
                    : statusBrush,
                BorderThickness = new Thickness(isParent ? 3 : isSelected ? 2.5 : 2),
                CornerRadius = new CornerRadius(8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = task.Id,
                Opacity = nodeOpacity
            };

            if (task.IsRunning)
            {
                nodeBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = statusColor,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.5
                };
            }

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.Margin = new Thickness(8, 5, 8, 5);

            // Row 0: Task number + status indicator + badges
            var topRow = new DockPanel();

            var badgePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(badgePanel, Dock.Right);

            var projectLabel = new TextBlock
            {
                Text = task.ProjectName,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(task.ProjectColor)),
                FontSize = 8,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 50
            };
            badgePanel.Children.Add(projectLabel);

            if (isSubtask)
            {
                badgePanel.Children.Add(new TextBlock
                {
                    Text = " \u2502sub",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                    FontSize = 8,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            if (isParent)
            {
                int childCount = task.ChildTaskIds.Count;
                var childTasks = allTasks.Where(t => task.ChildTaskIds.Contains(t.Id)).ToList();
                int completedChildren = childTasks.Count(t => t.IsFinished);
                badgePanel.Children.Add(new TextBlock
                {
                    Text = $" [{completedChildren}/{childCount}]",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                    FontSize = 8,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            topRow.Children.Add(badgePanel);

            var taskLabel = new TextBlock
            {
                Text = task.HierarchyLabel,
                Foreground = statusBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI")
            };
            topRow.Children.Add(taskLabel);

            var statusDot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = statusBrush,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            topRow.Children.Add(statusDot);

            var statusText = new TextBlock
            {
                Text = task.StatusText,
                Foreground = statusBrush,
                FontSize = 9,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            topRow.Children.Add(statusText);

            Grid.SetRow(topRow, 0);
            mainGrid.Children.Add(topRow);

            // Row 1: Description
            var descLabel = new TextBlock
            {
                Text = task.Description?.Length > 28
                    ? task.Description.Substring(0, 28) + "..."
                    : task.Description ?? "",
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(descLabel, 1);
            mainGrid.Children.Add(descLabel);

            // Row 2: Time info + tool activity
            var timeRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };

            if (task.HasToolActivity && task.IsRunning)
            {
                var activityLabel = new TextBlock
                {
                    Text = task.ToolActivityText,
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontSize = 7,
                    FontFamily = new FontFamily("Consolas"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 100,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(activityLabel, Dock.Right);
                timeRow.Children.Add(activityLabel);
            }

            var timeLabel = new TextBlock
            {
                Text = GetCompactTimeInfo(task),
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 8,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            timeRow.Children.Add(timeLabel);

            Grid.SetRow(timeRow, 2);
            mainGrid.Children.Add(timeRow);

            // Row 3: Token info + dependency/group info
            var bottomRow = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };

            if (task.HasTokenData)
            {
                var tokenLabel = new TextBlock
                {
                    Text = task.TokenDisplayText,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)),
                    FontSize = 8,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                bottomRow.Children.Add(tokenLabel);
            }

            var infoPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(infoPanel, Dock.Right);

            if (!string.IsNullOrEmpty(task.GroupName))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = task.GroupName.Length > 8 ? task.GroupName[..8] : task.GroupName,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)),
                    FontSize = 7,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 4, 0)
                });
            }

            int depCount = task.DependencyTaskIdCount;
            if (depCount > 0)
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = $"{depCount} dep{(depCount > 1 ? "s" : "")}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)),
                    FontSize = 8,
                    FontFamily = new FontFamily("Segoe UI")
                });
            }

            bottomRow.Children.Add(infoPanel);

            Grid.SetRow(bottomRow, 3);
            mainGrid.Children.Add(bottomRow);

            nodeBorder.Child = mainGrid;
            nodeBorder.ToolTip = CreateNodeTooltip(task, allTasks);

            Canvas.SetLeft(nodeBorder, pos.X);
            Canvas.SetTop(nodeBorder, pos.Y);
            _canvas.Children.Add(nodeBorder);
            _nodeBorders[task.Id] = nodeBorder;

            // Context Menu
            nodeBorder.ContextMenu = CreateNodeContextMenu(task, activeTasks);

            // Let the interaction handler wire up mouse events
            OnNodeCreated?.Invoke(nodeBorder, task);
        }

        // ── Context Menu ─────────────────────────────────────────────

        public ContextMenu CreateNodeContextMenu(AgentTask task, ObservableCollection<AgentTask>? activeTasks)
        {
            var menu = new ContextMenu();

            var showOutputItem = new MenuItem { Header = "Show Output" };
            showOutputItem.Click += (_, _) => ShowOutputRequested?.Invoke(task);
            menu.Items.Add(showOutputItem);

            menu.Items.Add(new Separator());

            var highlightItem = new MenuItem { Header = "Highlight Dependency Chain" };
            highlightItem.Click += (_, _) => NodeHighlightRequested?.Invoke(task.Id);
            menu.Items.Add(highlightItem);

            bool hasOwnDeps = task.DependencyTaskIdCount > 0;
            bool hasIncoming = activeTasks?.Any(t => t.Id != task.Id && t.ContainsDependencyTaskId(task.Id)) == true;
            if (hasOwnDeps || hasIncoming)
            {
                var removeDepsItem = new MenuItem { Header = "Remove All Dependencies" };
                removeDepsItem.Click += (_, _) => DependenciesRemoved?.Invoke(task);
                menu.Items.Add(removeDepsItem);
            }

            menu.Items.Add(new Separator());

            if (task.IsRunning || task.IsPaused)
            {
                var pauseItem = new MenuItem
                {
                    Header = task.IsPaused ? "Resume" : "Pause"
                };
                pauseItem.Click += (_, _) => PauseResumeRequested?.Invoke(task);
                menu.Items.Add(pauseItem);
            }

            if (task.IsQueued || task.IsInitQueued)
            {
                var forceItem = new MenuItem { Header = "Force Start" };
                forceItem.Click += (_, _) => ForceStartRequested?.Invoke(task);
                menu.Items.Add(forceItem);
            }

            if (task.IsFinished && task.HasRecommendations)
            {
                var continueItem = new MenuItem { Header = "Continue (Recommendations)" };
                continueItem.Click += (_, _) => ContinueRequested?.Invoke(task);
                menu.Items.Add(continueItem);
            }

            var copyItem = new MenuItem { Header = "Copy Prompt" };
            copyItem.Click += (_, _) => CopyPromptRequested?.Invoke(task);
            menu.Items.Add(copyItem);

            menu.Items.Add(new Separator());

            var cancelItem = new MenuItem
            {
                Header = task.IsFinished ? "Remove" : "Cancel"
            };
            cancelItem.Click += (_, _) => CancelRequested?.Invoke(task);
            menu.Items.Add(cancelItem);

            if (!string.IsNullOrEmpty(task.GitStartHash))
            {
                var revertItem = new MenuItem { Header = "Revert Changes" };
                revertItem.Click += (_, _) => RevertRequested?.Invoke(task);
                menu.Items.Add(revertItem);
            }

            return menu;
        }

        // Event for highlight from context menu
        public event Action<string>? NodeHighlightRequested;

        // ── Node Tooltip ─────────────────────────────────────────────

        public ToolTip CreateNodeTooltip(AgentTask task, List<AgentTask> allTasks)
        {
            var panel = new StackPanel { MaxWidth = 400 };

            var titleText = $"Task {task.HierarchyLabel} \u2014 {task.StatusText}";
            if (task.IsSubTask)
                titleText += " (subtask)";
            else if (task.HasChildren)
                titleText += " (parent)";

            panel.Children.Add(new TextBlock
            {
                Text = titleText,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = BrushCache.Get(task.StatusColor),
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = task.Description ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = $"Project: {task.ProjectName}",
                Foreground = BrushCache.Get(task.ProjectColor),
                FontSize = 10
            });

            panel.Children.Add(new TextBlock
            {
                Text = task.TimeInfo,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0)
            });

            if (task.HasTokenData)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Tokens: {task.TokenDisplayText}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (!string.IsNullOrEmpty(task.GroupName))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Group: {task.GroupName}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var depIds = task.DependencyTaskIds;
            if (depIds.Count > 0)
            {
                var depNums = task.DependencyTaskNumbers?.Count > 0
                    ? string.Join(", ", task.DependencyTaskNumbers.Select(n => $"#{n}"))
                    : string.Join(", ", depIds.Select(id => id[..Math.Min(8, id.Length)]));
                panel.Children.Add(new TextBlock
                {
                    Text = $"Depends on: {depNums}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (!string.IsNullOrEmpty(task.BlockedByTaskId))
            {
                string blockerText = task.BlockedByTaskNumber.HasValue
                    ? $"#{task.BlockedByTaskNumber.Value}"
                    : task.BlockedByTaskId[..Math.Min(8, task.BlockedByTaskId.Length)];
                panel.Children.Add(new TextBlock
                {
                    Text = $"Blocked by: {blockerText} (file lock)",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (task.IsSubTask)
            {
                var parent = allTasks.FirstOrDefault(t => t.Id == task.ParentTaskId);
                string parentInfo = parent != null ? $"#{parent.TaskNumber}" : (task.ParentTaskId ?? "?")[..Math.Min(8, (task.ParentTaskId ?? "?").Length)];
                panel.Children.Add(new TextBlock
                {
                    Text = $"Parent task: {parentInfo}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (task.HasChildren)
            {
                var childTasks = allTasks.Where(t => task.ChildTaskIds.Contains(t.Id)).ToList();
                int completed = childTasks.Count(t => t.IsFinished);
                int running = childTasks.Count(t => t.IsRunning);
                panel.Children.Add(new TextBlock
                {
                    Text = $"Subtasks: {completed}/{childTasks.Count} done, {running} running",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            if (task.HasToolActivity && task.IsRunning)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Activity: {task.ToolActivityText}",
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(task.CompletionSummary))
            {
                panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
                panel.Children.Add(new TextBlock
                {
                    Text = task.CompletionSummary.Length > 200
                        ? task.CompletionSummary.Substring(0, 200) + "..."
                        : task.CompletionSummary,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                    FontSize = 10
                });
            }

            panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });
            panel.Children.Add(new TextBlock
            {
                Text = "Click: drag | Ctrl+Click: highlight deps | Double-click: output | Right-drag: connect dep | Right-click: menu",
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 9,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            });

            return new ToolTip
            {
                Content = panel,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse
            };
        }

        // ── Legend ────────────────────────────────────────────────────

        public Border CreateLegendPanel()
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 35)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(8, 0, 8, 4),
                Visibility = Visibility.Visible
            };

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

            AddLegendItem(wrap, "#64B5F6", "Dependency", false);
            AddLegendItem(wrap, "#FFA726", "Blocked By", false);
            AddLegendItem(wrap, "#E05555", "File Lock", true);
            AddLegendItem(wrap, "#66BB6A", "Parent-Child", false);

            wrap.Children.Add(new TextBlock
            {
                Text = "  |  ",
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });

            AddStatusLegend(wrap, "#64B5F6", "Running");
            AddStatusLegend(wrap, "#00E676", "Completed");
            AddStatusLegend(wrap, "#FFD600", "Queued");
            AddStatusLegend(wrap, "#CE93D8", "Paused");
            AddStatusLegend(wrap, "#E05555", "Failed");
            AddStatusLegend(wrap, "#B39DDB", "Planning");

            border.Child = wrap;
            return border;
        }

        private void AddLegendItem(WrapPanel panel, string color, string label, bool dashed)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var line = new Line
            {
                X1 = 0, Y1 = 5, X2 = 20, Y2 = 5,
                Stroke = BrushCache.Get(color),
                StrokeThickness = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (dashed)
                line.StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 });

            var text = new TextBlock
            {
                Text = label,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(line);
            stack.Children.Add(text);
            panel.Children.Add(stack);
        }

        private void AddStatusLegend(WrapPanel panel, string color, string label)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = BrushCache.Get(color),
                VerticalAlignment = VerticalAlignment.Center
            };

            var text = new TextBlock
            {
                Text = label,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(dot);
            stack.Children.Add(text);
            panel.Children.Add(stack);
        }

        // ── Progress Panel ──────────────────────────────────────────

        public void RebuildProgressPanel(
            bool showProgress,
            ObservableCollection<AgentTask>? activeTasks,
            StackPanel progressStack)
        {
            if (!showProgress || activeTasks == null) return;

            progressStack.Children.Clear();
            var tasks = activeTasks.ToList();
            var taskMap = tasks.ToDictionary(t => t.Id);

            var parents = tasks.Where(t => t.ChildTaskIds != null && t.ChildTaskIds.Count > 0).ToList();

            if (parents.Count == 0)
            {
                progressStack.Children.Add(new TextBlock
                {
                    Text = "No parent-child tasks",
                    Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            foreach (var parent in parents)
            {
                var childIds = parent.ChildTaskIds;
                var children = childIds
                    .Select(id => taskMap.TryGetValue(id, out var c) ? c : null)
                    .Where(c => c != null)
                    .ToList();

                int total = children.Count;
                int completed = children.Count(c => c!.IsFinished);
                double ratio = total > 0 ? (double)completed / total : 0;

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 48)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(100, 100, 100, 120)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(10, 8, 10, 8)
                };

                var cardStack = new StackPanel();

                cardStack.Children.Add(new TextBlock
                {
                    Text = $"#{parent.TaskNumber}  {(parent.Description?.Length > 24 ? parent.Description[..24] + "..." : parent.Description ?? "")}",
                    Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = parent.Description
                });

                cardStack.Children.Add(new TextBlock
                {
                    Text = $"{completed} / {total} completed",
                    Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 2, 0, 4)
                });

                var progressBarBg = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.FromArgb(80, 80, 80, 90))
                };

                var progressBarGrid = new Grid { Height = 6 };
                progressBarGrid.Children.Add(progressBarBg);

                var progressBarFill = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = BrushCache.Get("#66BB6A"),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                progressBarFill.Loaded += (s, _) =>
                {
                    var parentElement = ((FrameworkElement)s).Parent as FrameworkElement;
                    if (parentElement != null)
                        ((Border)s).Width = parentElement.ActualWidth * ratio;
                };
                progressBarGrid.Children.Add(progressBarFill);
                cardStack.Children.Add(progressBarGrid);

                foreach (var child in children)
                {
                    if (child == null) continue;
                    var childStatusColor = (Color)ColorConverter.ConvertFromString(child.StatusColor);
                    var childRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(4, 3, 0, 0)
                    };

                    childRow.Children.Add(new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = new SolidColorBrush(childStatusColor),
                        Margin = new Thickness(0, 2, 6, 0)
                    });

                    childRow.Children.Add(new TextBlock
                    {
                        Text = $"#{child.TaskNumber} {child.Status}",
                        Foreground = new SolidColorBrush(childStatusColor),
                        FontSize = 10,
                        FontFamily = new FontFamily("Segoe UI")
                    });

                    var childDesc = child.Description?.Length > 16
                        ? child.Description[..16] + "..."
                        : child.Description ?? "";
                    if (!string.IsNullOrEmpty(childDesc))
                    {
                        childRow.Children.Add(new TextBlock
                        {
                            Text = $"  {childDesc}",
                            Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                            FontSize = 10,
                            FontFamily = new FontFamily("Segoe UI"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    }

                    cardStack.Children.Add(childRow);
                }

                card.Child = cardStack;
                progressStack.Children.Add(card);
            }
        }

        // ── Drawing Helpers ──────────────────────────────────────────

        private void DrawBezierEdge(Point from, Point to, string colorHex, bool dashed,
            double thickness = 2, double opacity = 1.0)
        {
            var fromCenter = new Point(from.X + GraphLayoutEngine.NodeWidth / 2, from.Y + GraphLayoutEngine.NodeHeight / 2);
            var toCenter = new Point(to.X + GraphLayoutEngine.NodeWidth / 2, to.Y + GraphLayoutEngine.NodeHeight / 2);

            double midX = (fromCenter.X + toCenter.X) / 2;
            var cp1 = new Point(midX, fromCenter.Y);
            var cp2 = new Point(midX, toCenter.Y);

            var pathFig = new PathFigure { StartPoint = fromCenter };
            pathFig.Segments.Add(new BezierSegment(cp1, cp2, toCenter, true));

            var pathGeo = new PathGeometry();
            pathGeo.Figures.Add(pathFig);

            var brush = BrushCache.Get(colorHex);

            var path = new Path
            {
                Data = pathGeo,
                Stroke = brush,
                StrokeThickness = thickness,
                IsHitTestVisible = false,
                Opacity = opacity
            };

            if (dashed)
                path.StrokeDashArray = new DoubleCollection(new[] { 6.0, 3.0 });

            _canvas.Children.Add(path);
            _edgeElements.Add(path);
        }

        private void DrawArrowHead(Point from, Point to, string colorHex, double opacity = 1.0)
        {
            var toCenter = new Point(to.X + GraphLayoutEngine.NodeWidth / 2, to.Y + GraphLayoutEngine.NodeHeight / 2);
            var fromCenter = new Point(from.X + GraphLayoutEngine.NodeWidth / 2, from.Y + GraphLayoutEngine.NodeHeight / 2);

            double dx = toCenter.X - fromCenter.X;
            double dy = toCenter.Y - fromCenter.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;

            dx /= len;
            dy /= len;

            double arrowTipX = toCenter.X - dx * (GraphLayoutEngine.NodeWidth / 2 + 2);
            double arrowTipY = toCenter.Y - dy * (GraphLayoutEngine.NodeHeight / 2 + 2);

            double arrowSize = 10;
            double perpX = -dy;
            double perpY = dx;

            var p1 = new Point(arrowTipX, arrowTipY);
            var p2 = new Point(arrowTipX - dx * arrowSize + perpX * arrowSize * 0.4,
                               arrowTipY - dy * arrowSize + perpY * arrowSize * 0.4);
            var p3 = new Point(arrowTipX - dx * arrowSize - perpX * arrowSize * 0.4,
                               arrowTipY - dy * arrowSize - perpY * arrowSize * 0.4);

            var brush = BrushCache.Get(colorHex);

            var polygon = new Polygon
            {
                Points = new PointCollection { p1, p2, p3 },
                Fill = brush,
                IsHitTestVisible = false,
                Opacity = opacity
            };

            _canvas.Children.Add(polygon);
            _edgeElements.Add(polygon);
        }

        // ── Viewport Culling ──────────────────────────────────────────

        private bool IsNodeOutsideViewport(
            Point nodePos,
            ScrollViewer scrollViewer,
            ScaleTransform scaleTransform,
            TranslateTransform translateTransform)
        {
            double scale = scaleTransform.ScaleX;
            double tx = translateTransform.X;
            double ty = translateTransform.Y;

            double screenLeft = nodePos.X * scale + tx;
            double screenTop = nodePos.Y * scale + ty;
            double screenRight = (nodePos.X + GraphLayoutEngine.NodeWidth) * scale + tx;
            double screenBottom = (nodePos.Y + GraphLayoutEngine.NodeHeight) * scale + ty;

            double vpWidth = scrollViewer.ActualWidth;
            double vpHeight = scrollViewer.ActualHeight;

            if (vpWidth <= 0 || vpHeight <= 0)
                return false;

            return screenRight < 0 || screenLeft > vpWidth ||
                   screenBottom < 0 || screenTop > vpHeight;
        }

        // ── Canvas Helpers ──────────────────────────────────────────

        public void ResizeCanvas(Dictionary<string, Point> nodePositions)
        {
            if (nodePositions.Count == 0) return;

            double maxX = nodePositions.Values.Max(p => p.X) + GraphLayoutEngine.NodeWidth + GraphLayoutEngine.NodePadding * 2;
            double maxY = nodePositions.Values.Max(p => p.Y) + GraphLayoutEngine.NodeHeight + GraphLayoutEngine.NodePadding * 2;

            _canvas.Width = maxX;
            _canvas.Height = maxY;
        }

        public static string GetCompactTimeInfo(AgentTask task)
        {
            if (task.EndTime.HasValue)
            {
                var duration = task.EndTime.Value - task.StartTime;
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
            var running = DateTime.Now - task.StartTime;
            return $"{(int)running.TotalMinutes}m {running.Seconds}s";
        }

        public static Brush CreateGridBrush()
        {
            const double cellSize = 40.0;
            const int majorEvery = 5;
            double tileSize = cellSize * majorEvery;

            var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 0.5);
            minorPen.Freeze();
            var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.5);
            majorPen.Freeze();

            var drawing = new DrawingGroup();

            for (int i = 0; i < majorEvery; i++)
            {
                var pen = i == 0 ? majorPen : minorPen;
                double offset = i * cellSize;

                drawing.Children.Add(new GeometryDrawing(null, pen,
                    new LineGeometry(new Point(0, offset), new Point(tileSize, offset))));
                drawing.Children.Add(new GeometryDrawing(null, pen,
                    new LineGeometry(new Point(offset, 0), new Point(offset, tileSize))));
            }

            var brush = new DrawingBrush(drawing)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileSize, tileSize),
                ViewportUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            return brush;
        }

        public static Button CreateHeaderButton(string icon, string tooltip)
        {
            return new Button
            {
                Content = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
