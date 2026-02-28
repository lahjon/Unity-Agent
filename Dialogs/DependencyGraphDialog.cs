using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgenticEngine.Managers;

namespace AgenticEngine.Dialogs
{
    public static class DependencyGraphDialog
    {
        private const double NodeWidth = 160;
        private const double NodeHeight = 56;
        private const double Padding = 40;

        public static void Show(
            ObservableCollection<AgentTask> activeTasks,
            FileLockManager fileLockManager)
        {
            Window? owner = null;
            try { owner = Application.Current.MainWindow; } catch { }

            var dlg = new Window
            {
                Title = "Task Dependency Graph",
                Width = 1000,
                Height = 700,
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
            outerBorder.MouseLeftButtonDown += (_, me) =>
            {
                if (me.ClickCount == 1 && me.OriginalSource == outerBorder)
                    dlg.DragMove();
            };

            var root = new DockPanel();

            // Title bar
            var titleBar = new DockPanel { Margin = new Thickness(18, 14, 18, 0) };

            var titleBlock = new TextBlock
            {
                Text = "Task Dependency Graph",
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

            // Canvas for graph
            var canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Margin = new Thickness(12, 12, 12, 12)
            };

            root.Children.Add(canvas);
            outerBorder.Child = root;
            dlg.Content = outerBorder;

            if (owner != null) dlg.Owner = owner;

            // State for tracking nodes
            var nodePositions = new Dictionary<string, Point>();
            var nodeBorders = new Dictionary<string, Border>();

            // Drag-to-connect state
            string? dragSourceId = null;
            Line? dragLine = null;

            // Node drag state
            string? draggingNodeId = null;
            Point dragNodeOffset = default;

            void RebuildGraph()
            {
                canvas.Children.Clear();
                nodeBorders.Clear();

                var tasks = activeTasks.ToList();
                if (tasks.Count == 0)
                {
                    var emptyText = new TextBlock
                    {
                        Text = "No active tasks",
                        Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                        FontSize = 14,
                        FontFamily = new FontFamily("Segoe UI")
                    };
                    Canvas.SetLeft(emptyText, 40);
                    Canvas.SetTop(emptyText, 40);
                    canvas.Children.Add(emptyText);
                    return;
                }

                // Assign positions: lay out in a grid if no existing position
                int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(tasks.Count)));
                for (int i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    if (!nodePositions.ContainsKey(t.Id))
                    {
                        int row = i / cols;
                        int col = i % cols;
                        nodePositions[t.Id] = new Point(
                            Padding + col * (NodeWidth + 60),
                            Padding + row * (NodeHeight + 70));
                    }
                }

                // Draw edges first (behind nodes)
                // 1) Dependency edges (solid)
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
                                DrawBezierEdge(canvas, from, to, "#64B5F6", false);
                                DrawArrowHead(canvas, from, to, "#64B5F6");
                            }
                        }
                    }

                    // BlockedByTaskId edge
                    if (!string.IsNullOrEmpty(task.BlockedByTaskId) &&
                        nodePositions.ContainsKey(task.BlockedByTaskId) &&
                        nodePositions.ContainsKey(task.Id))
                    {
                        var from = nodePositions[task.BlockedByTaskId];
                        var to = nodePositions[task.Id];
                        DrawBezierEdge(canvas, from, to, "#FFA726", false);
                        DrawArrowHead(canvas, from, to, "#FFA726");
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
                                DrawBezierEdge(canvas, from, to, "#E05555", true);
                                DrawArrowHead(canvas, from, to, "#E05555");
                            }
                        }
                    }
                }

                // Draw nodes
                foreach (var task in tasks)
                {
                    if (!nodePositions.TryGetValue(task.Id, out var pos))
                        continue;

                    var statusBrush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(task.StatusColor));

                    var nodeBorder = new Border
                    {
                        Width = NodeWidth,
                        Height = NodeHeight,
                        Background = new SolidColorBrush(Color.FromArgb(230, 40, 40, 48)),
                        BorderBrush = statusBrush,
                        BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(8),
                        Cursor = Cursors.Hand,
                        Tag = task.Id
                    };

                    var stack = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 6, 8, 6)
                    };

                    var taskLabel = new TextBlock
                    {
                        Text = $"#{task.TaskNumber}",
                        Foreground = statusBrush,
                        FontWeight = FontWeights.Bold,
                        FontSize = 13,
                        FontFamily = new FontFamily("Segoe UI")
                    };

                    var descLabel = new TextBlock
                    {
                        Text = task.Description?.Length > 22
                            ? task.Description.Substring(0, 22) + "..."
                            : task.Description ?? "",
                        Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    descLabel.ToolTip = task.Description;

                    var statusLabel = new TextBlock
                    {
                        Text = task.Status.ToString(),
                        Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                        FontSize = 9,
                        FontFamily = new FontFamily("Segoe UI")
                    };

                    stack.Children.Add(taskLabel);
                    stack.Children.Add(descLabel);
                    stack.Children.Add(statusLabel);
                    nodeBorder.Child = stack;

                    Canvas.SetLeft(nodeBorder, pos.X);
                    Canvas.SetTop(nodeBorder, pos.Y);
                    canvas.Children.Add(nodeBorder);
                    nodeBorders[task.Id] = nodeBorder;

                    // ── Node dragging ──
                    var capturedTaskId = task.Id;
                    nodeBorder.MouseLeftButtonDown += (s, e) =>
                    {
                        if (e.ClickCount == 1)
                        {
                            draggingNodeId = capturedTaskId;
                            var currentPos = nodePositions[capturedTaskId];
                            var mousePos = e.GetPosition(canvas);
                            dragNodeOffset = new Point(mousePos.X - currentPos.X, mousePos.Y - currentPos.Y);
                            nodeBorder.CaptureMouse();
                            e.Handled = true;
                        }
                    };

                    nodeBorder.MouseMove += (s, e) =>
                    {
                        if (draggingNodeId == capturedTaskId && e.LeftButton == MouseButtonState.Pressed)
                        {
                            var mousePos = e.GetPosition(canvas);
                            nodePositions[capturedTaskId] = new Point(
                                mousePos.X - dragNodeOffset.X,
                                mousePos.Y - dragNodeOffset.Y);
                            RebuildGraph();
                            e.Handled = true;
                        }
                    };

                    nodeBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        if (draggingNodeId == capturedTaskId)
                        {
                            draggingNodeId = null;
                            nodeBorder.ReleaseMouseCapture();
                            e.Handled = true;
                        }
                    };

                    // ── Right-click drag to create dependency edge ──
                    nodeBorder.MouseRightButtonDown += (s, e) =>
                    {
                        dragSourceId = capturedTaskId;
                        var mousePos = e.GetPosition(canvas);
                        dragLine = new Line
                        {
                            X1 = pos.X + NodeWidth / 2,
                            Y1 = pos.Y + NodeHeight / 2,
                            X2 = mousePos.X,
                            Y2 = mousePos.Y,
                            Stroke = new SolidColorBrush(Color.FromRgb(100, 181, 246)),
                            StrokeThickness = 2,
                            StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                            IsHitTestVisible = false
                        };
                        canvas.Children.Add(dragLine);
                        nodeBorder.CaptureMouse();
                        e.Handled = true;
                    };

                    nodeBorder.MouseRightButtonUp += (s, e) =>
                    {
                        if (dragSourceId == null || dragLine == null)
                            return;

                        nodeBorder.ReleaseMouseCapture();

                        // Find target node under mouse
                        var mousePos = e.GetPosition(canvas);
                        canvas.Children.Remove(dragLine);
                        dragLine = null;

                        string? targetId = null;
                        foreach (var kvp in nodeBorders)
                        {
                            if (kvp.Key == dragSourceId) continue;
                            var nodePos = nodePositions[kvp.Key];
                            if (mousePos.X >= nodePos.X && mousePos.X <= nodePos.X + NodeWidth &&
                                mousePos.Y >= nodePos.Y && mousePos.Y <= nodePos.Y + NodeHeight)
                            {
                                targetId = kvp.Key;
                                break;
                            }
                        }

                        if (targetId != null)
                        {
                            // Add dependency: target depends on source
                            var targetTask = activeTasks.FirstOrDefault(t => t.Id == targetId);
                            if (targetTask != null)
                            {
                                targetTask.DependencyTaskIds ??= new List<string>();
                                if (!targetTask.DependencyTaskIds.Contains(dragSourceId))
                                {
                                    targetTask.DependencyTaskIds.Add(dragSourceId);
                                    RebuildGraph();
                                }
                            }
                        }

                        dragSourceId = null;
                        e.Handled = true;
                    };
                }
            }

            // Mouse move on canvas for drag-line tracking
            canvas.MouseMove += (s, e) =>
            {
                if (dragSourceId != null && dragLine != null && e.RightButton == MouseButtonState.Pressed)
                {
                    var mousePos = e.GetPosition(canvas);
                    dragLine.X2 = mousePos.X;
                    dragLine.Y2 = mousePos.Y;
                }
            };

            // Initial build
            RebuildGraph();

            // Auto-refresh timer
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                // Preserve positions but rebuild edges and node visuals
                if (draggingNodeId == null && dragSourceId == null)
                    RebuildGraph();
            };
            timer.Start();

            dlg.Closed += (_, _) => timer.Stop();

            // Escape to close
            dlg.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Escape) dlg.Close();
            };

            dlg.ShowDialog();
        }

        private static void DrawBezierEdge(Canvas canvas, Point from, Point to, string colorHex, bool dashed)
        {
            var fromCenter = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);
            var toCenter = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);

            double midY = (fromCenter.Y + toCenter.Y) / 2;
            var cp1 = new Point(fromCenter.X, midY);
            var cp2 = new Point(toCenter.X, midY);

            var pathFig = new PathFigure { StartPoint = fromCenter };
            pathFig.Segments.Add(new BezierSegment(cp1, cp2, toCenter, true));

            var pathGeo = new PathGeometry();
            pathGeo.Figures.Add(pathFig);

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));

            var path = new Path
            {
                Data = pathGeo,
                Stroke = brush,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };

            if (dashed)
            {
                path.StrokeDashArray = new DoubleCollection(new[] { 6.0, 3.0 });
            }

            canvas.Children.Add(path);
        }

        private static void DrawArrowHead(Canvas canvas, Point from, Point to, string colorHex)
        {
            var toCenter = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);
            var fromCenter = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);

            double dx = toCenter.X - fromCenter.X;
            double dy = toCenter.Y - fromCenter.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;

            dx /= len;
            dy /= len;

            // Place arrowhead at edge of target node
            double arrowTip_X = toCenter.X - dx * (NodeWidth / 2 + 2);
            double arrowTip_Y = toCenter.Y - dy * (NodeHeight / 2 + 2);

            double arrowSize = 10;
            double perpX = -dy;
            double perpY = dx;

            var p1 = new Point(arrowTip_X, arrowTip_Y);
            var p2 = new Point(arrowTip_X - dx * arrowSize + perpX * arrowSize * 0.4,
                               arrowTip_Y - dy * arrowSize + perpY * arrowSize * 0.4);
            var p3 = new Point(arrowTip_X - dx * arrowSize - perpX * arrowSize * 0.4,
                               arrowTip_Y - dy * arrowSize - perpY * arrowSize * 0.4);

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));

            var polygon = new Polygon
            {
                Points = new PointCollection { p1, p2, p3 },
                Fill = brush,
                IsHitTestVisible = false
            };

            canvas.Children.Add(polygon);
        }
    }
}
