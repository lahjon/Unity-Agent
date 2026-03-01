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
using HappyEngine.Helpers;

namespace HappyEngine.Controls
{
    internal class GraphInteractionHandler
    {
        private readonly Canvas _canvas;
        private readonly ScrollViewer _scrollViewer;
        private readonly ScaleTransform _scaleTransform;
        private readonly TranslateTransform _translateTransform;

        // Node drag state
        private string? _draggingNodeId;
        private Point _dragNodeOffset;
        private readonly HashSet<string> _userDraggedNodeIds = new();

        // Drag-to-connect state
        private string? _dragSourceId;
        private Line? _dragLine;
        private Point _rightClickStartPos;
        private bool _rightClickDragging;

        // Selection / highlight state
        private string? _selectedNodeId;
        private readonly HashSet<string> _highlightedNodeIds = new();
        private readonly HashSet<string> _highlightedEdgeKeys = new();

        // Zoom state
        private double _zoom = 1.0;

        // Pan state
        private bool _isPanning;
        private Point _panStart;
        private Point _panOrigin;

        // Animation state
        private DispatcherTimer? _animationTimer;
        private bool _isAnimating;

        // References needed for interaction
        private ObservableCollection<AgentTask>? _activeTasks;

        // Callbacks
        public Action? RequestRebuildGraph;
        public Action? RequestRedrawVisuals;

        // Events
        public event Action<AgentTask>? ShowOutputRequested;
        public event Action<AgentTask, AgentTask>? DependencyCreated;

        public string? DraggingNodeId => _draggingNodeId;
        public string? DragSourceId => _dragSourceId;
        public string? SelectedNodeId => _selectedNodeId;
        public HashSet<string> HighlightedNodeIds => _highlightedNodeIds;
        public HashSet<string> HighlightedEdgeKeys => _highlightedEdgeKeys;
        public HashSet<string> UserDraggedNodeIds => _userDraggedNodeIds;
        public double Zoom => _zoom;

        public GraphInteractionHandler(
            Canvas canvas,
            ScrollViewer scrollViewer,
            ScaleTransform scaleTransform,
            TranslateTransform translateTransform)
        {
            _canvas = canvas;
            _scrollViewer = scrollViewer;
            _scaleTransform = scaleTransform;
            _translateTransform = translateTransform;
        }

        public void SetActiveTasks(ObservableCollection<AgentTask>? activeTasks)
        {
            _activeTasks = activeTasks;
        }

        // ── Wire up scroll viewer events ──────────────────────────────

        public void AttachScrollViewerEvents()
        {
            _scrollViewer.PreviewMouseWheel += OnMouseWheel;
            _scrollViewer.PreviewMouseDown += OnPanStart;
            _scrollViewer.PreviewMouseMove += OnPanMove;
            _scrollViewer.PreviewMouseUp += OnPanEnd;
            _scrollViewer.PreviewKeyDown += OnGraphKeyDown;

            _canvas.MouseLeftButtonDown += (_, e) =>
            {
                _scrollViewer.Focus();
                if (e.OriginalSource == _canvas)
                {
                    _selectedNodeId = null;
                    _highlightedNodeIds.Clear();
                    _highlightedEdgeKeys.Clear();
                    RequestRebuildGraph?.Invoke();
                }
            };
        }

        // ── Wire up node interaction events ────────────────────────────

        public void AttachNodeEvents(
            Border nodeBorder,
            AgentTask task,
            Dictionary<string, Point> nodePositions,
            IReadOnlyDictionary<string, Border> nodeBorders)
        {
            var capturedTaskId = task.Id;
            var capturedTask = task;

            nodeBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    ShowOutputRequested?.Invoke(capturedTask);
                    e.Handled = true;
                    return;
                }

                if (e.ClickCount == 1)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    {
                        ToggleNodeSelection(capturedTaskId);
                        e.Handled = true;
                        return;
                    }

                    _draggingNodeId = capturedTaskId;
                    var currentPos = nodePositions[capturedTaskId];
                    var mousePos = e.GetPosition(_canvas);
                    _dragNodeOffset = new Point(mousePos.X - currentPos.X, mousePos.Y - currentPos.Y);
                    nodeBorder.CaptureMouse();
                    e.Handled = true;
                }
            };

            nodeBorder.MouseMove += (s, e) =>
            {
                if (_draggingNodeId == capturedTaskId && e.LeftButton == MouseButtonState.Pressed)
                {
                    var mousePos = e.GetPosition(_canvas);
                    MoveNode(capturedTaskId, new Point(
                        mousePos.X - _dragNodeOffset.X,
                        mousePos.Y - _dragNodeOffset.Y), nodePositions, nodeBorders);
                    e.Handled = true;
                }
                else if (_dragSourceId == capturedTaskId && e.RightButton == MouseButtonState.Pressed)
                {
                    var mousePos2 = e.GetPosition(_canvas);

                    if (!_rightClickDragging)
                    {
                        var dx = mousePos2.X - _rightClickStartPos.X;
                        var dy = mousePos2.Y - _rightClickStartPos.Y;
                        if (dx * dx + dy * dy < 25) return;

                        _rightClickDragging = true;
                        var nodePos = nodePositions[capturedTaskId];
                        _dragLine = new Line
                        {
                            X1 = nodePos.X + GraphLayoutEngine.NodeWidth / 2,
                            Y1 = nodePos.Y + GraphLayoutEngine.NodeHeight / 2,
                            X2 = mousePos2.X,
                            Y2 = mousePos2.Y,
                            Stroke = BrushCache.Theme("PausedBlue"),
                            StrokeThickness = 2,
                            StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                            IsHitTestVisible = false
                        };
                        _canvas.Children.Add(_dragLine);
                    }
                    else if (_dragLine != null)
                    {
                        _dragLine.X2 = mousePos2.X;
                        _dragLine.Y2 = mousePos2.Y;
                    }
                    e.Handled = true;
                }
            };

            nodeBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (_draggingNodeId == capturedTaskId)
                {
                    _userDraggedNodeIds.Add(capturedTaskId);
                    _draggingNodeId = null;
                    nodeBorder.ReleaseMouseCapture();
                    e.Handled = true;
                }
            };

            // Right-click: drag to create dependency edge, or click for context menu
            nodeBorder.MouseRightButtonDown += (s, e) =>
            {
                _dragSourceId = capturedTaskId;
                _rightClickStartPos = e.GetPosition(_canvas);
                _rightClickDragging = false;
                _dragLine = null;
                nodeBorder.CaptureMouse();
                e.Handled = true;
            };

            nodeBorder.MouseRightButtonUp += (s, e) =>
            {
                nodeBorder.ReleaseMouseCapture();

                if (_dragSourceId == null)
                    return;

                if (!_rightClickDragging)
                {
                    _dragSourceId = null;
                    if (nodeBorder.ContextMenu != null)
                    {
                        nodeBorder.ContextMenu.IsOpen = true;
                    }
                    e.Handled = true;
                    return;
                }

                var mousePos = e.GetPosition(_canvas);
                if (_dragLine != null)
                {
                    _canvas.Children.Remove(_dragLine);
                    _dragLine = null;
                }

                string? targetId = null;
                foreach (var kvp in nodeBorders)
                {
                    if (kvp.Key == _dragSourceId) continue;
                    var nodePos = nodePositions[kvp.Key];
                    if (mousePos.X >= nodePos.X && mousePos.X <= nodePos.X + GraphLayoutEngine.NodeWidth &&
                        mousePos.Y >= nodePos.Y && mousePos.Y <= nodePos.Y + GraphLayoutEngine.NodeHeight)
                    {
                        targetId = kvp.Key;
                        break;
                    }
                }

                if (targetId != null && _activeTasks != null)
                {
                    var targetTask = _activeTasks.FirstOrDefault(t => t.Id == targetId);
                    if (targetTask != null)
                    {
                        if (!targetTask.ContainsDependencyTaskId(_dragSourceId))
                        {
                            if (GraphLayoutEngine.WouldCreateCycle(_activeTasks, _dragSourceId, targetId))
                            {
                                if (nodeBorders.TryGetValue(targetId, out var flashBorder))
                                {
                                    var origBg = flashBorder.Background;
                                    flashBorder.Background = BrushCache.Theme("DangerFlash");
                                    var flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                                    flashTimer.Tick += (_, _) =>
                                    {
                                        flashBorder.Background = origBg;
                                        flashTimer.Stop();
                                    };
                                    flashTimer.Start();
                                }
                            }
                            else
                            {
                                var sourceTask = _activeTasks.FirstOrDefault(t => t.Id == _dragSourceId);
                                if (sourceTask != null)
                                {
                                    DependencyCreated?.Invoke(sourceTask, targetTask);
                                    RequestRebuildGraph?.Invoke();
                                }
                            }
                        }
                    }
                }

                _dragSourceId = null;
                _rightClickDragging = false;
                e.Handled = true;
            };

            // Hover highlight
            bool isSelected = _selectedNodeId == task.Id;
            nodeBorder.MouseEnter += (s, _) =>
            {
                if (_draggingNodeId == null && _highlightedNodeIds.Count == 0)
                    nodeBorder.Background = BrushCache.Theme("GraphNodeHighlightBg");
            };
            nodeBorder.MouseLeave += (s, _) =>
            {
                if (_draggingNodeId == null && _highlightedNodeIds.Count == 0)
                    nodeBorder.Background = isSelected
                        ? BrushCache.Theme("GraphNodeBg")
                        : BrushCache.Theme("GraphNodeBgDim");
            };
        }

        // ── Dependency Chain Highlighting ─────────────────────────────

        public void ToggleNodeSelection(string taskId)
        {
            if (_selectedNodeId == taskId)
            {
                _selectedNodeId = null;
                _highlightedNodeIds.Clear();
                _highlightedEdgeKeys.Clear();
            }
            else
            {
                _selectedNodeId = taskId;
                BuildHighlightChain(taskId);
            }
            RequestRebuildGraph?.Invoke();
        }

        private void BuildHighlightChain(string taskId)
        {
            _highlightedNodeIds.Clear();
            _highlightedEdgeKeys.Clear();

            if (_activeTasks == null) return;

            var tasks = _activeTasks.ToList();
            var taskMap = tasks.ToDictionary(t => t.Id);

            _highlightedNodeIds.Add(taskId);

            WalkUpstream(taskId, taskMap);
            WalkDownstream(taskId, tasks);
        }

        private void WalkUpstream(string taskId, Dictionary<string, AgentTask> taskMap)
        {
            if (!taskMap.TryGetValue(taskId, out var task)) return;

            if (task.DependencyTaskIds != null)
            {
                foreach (var depId in task.DependencyTaskIds)
                {
                    _highlightedEdgeKeys.Add($"{depId}->{taskId}");
                    if (_highlightedNodeIds.Add(depId))
                        WalkUpstream(depId, taskMap);
                }
            }

            if (!string.IsNullOrEmpty(task.BlockedByTaskId))
            {
                _highlightedEdgeKeys.Add($"{task.BlockedByTaskId}->{taskId}");
                if (_highlightedNodeIds.Add(task.BlockedByTaskId))
                    WalkUpstream(task.BlockedByTaskId, taskMap);
            }

            if (!string.IsNullOrEmpty(task.ParentTaskId))
            {
                _highlightedEdgeKeys.Add($"{task.ParentTaskId}=>{taskId}");
                if (_highlightedNodeIds.Add(task.ParentTaskId))
                    WalkUpstream(task.ParentTaskId, taskMap);
            }
        }

        private void WalkDownstream(string taskId, List<AgentTask> tasks)
        {
            foreach (var task in tasks)
            {
                if (task.ContainsDependencyTaskId(taskId))
                {
                    _highlightedEdgeKeys.Add($"{taskId}->{task.Id}");
                    if (_highlightedNodeIds.Add(task.Id))
                        WalkDownstream(task.Id, tasks);
                }

                if (task.BlockedByTaskId == taskId)
                {
                    _highlightedEdgeKeys.Add($"{taskId}->{task.Id}");
                    if (_highlightedNodeIds.Add(task.Id))
                        WalkDownstream(task.Id, tasks);
                }

                if (task.ParentTaskId == taskId)
                {
                    _highlightedEdgeKeys.Add($"{taskId}=>{task.Id}");
                    if (_highlightedNodeIds.Add(task.Id))
                        WalkDownstream(task.Id, tasks);
                }
            }
        }

        // ── Node Movement ─────────────────────────────────────────────

        private void MoveNode(
            string taskId,
            Point newPos,
            Dictionary<string, Point> nodePositions,
            IReadOnlyDictionary<string, Border> nodeBorders)
        {
            nodePositions[taskId] = newPos;

            if (nodeBorders.TryGetValue(taskId, out var border))
            {
                Canvas.SetLeft(border, newPos.X);
                Canvas.SetTop(border, newPos.Y);
            }

            // Redraw only edges (nodes stay in place)
            RequestRedrawVisuals?.Invoke();
        }

        // ── Zoom & Pan ────────────────────────────────────────────────

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Only handle zoom when the graph context is initialized
            if (_activeTasks == null) return;

            // Only handle zoom when the mouse is within the graph view bounds
            var mousePos = e.GetPosition(_scrollViewer);
            if (mousePos.X < 0 || mousePos.Y < 0 ||
                mousePos.X > _scrollViewer.ActualWidth || mousePos.Y > _scrollViewer.ActualHeight)
                return;

            e.Handled = true;

            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double newZoom = Math.Clamp(_zoom * factor, 0.3, 3.0);

            // Zoom centered on the mouse pointer position
            double canvasX = (mousePos.X - _translateTransform.X) / _zoom;
            double canvasY = (mousePos.Y - _translateTransform.Y) / _zoom;

            _zoom = newZoom;
            _scaleTransform.ScaleX = _zoom;
            _scaleTransform.ScaleY = _zoom;

            _translateTransform.X = mousePos.X - canvasX * _zoom;
            _translateTransform.Y = mousePos.Y - canvasY * _zoom;
        }

        private void OnPanStart(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPanning = true;
                _panStart = e.GetPosition(_scrollViewer);
                _panOrigin = new Point(_translateTransform.X, _translateTransform.Y);
                _scrollViewer.CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnPanMove(object sender, MouseEventArgs e)
        {
            if (_isPanning && e.MiddleButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(_scrollViewer);
                _translateTransform.X = _panOrigin.X + (pos.X - _panStart.X);
                _translateTransform.Y = _panOrigin.Y + (pos.Y - _panStart.Y);
                e.Handled = true;
            }
        }

        private void OnPanEnd(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                _scrollViewer.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        public void FitToView(Dictionary<string, Point> nodePositions)
        {
            if (nodePositions.Count == 0) return;

            double minX = nodePositions.Values.Min(p => p.X);
            double minY = nodePositions.Values.Min(p => p.Y);
            double maxX = nodePositions.Values.Max(p => p.X) + GraphLayoutEngine.NodeWidth;
            double maxY = nodePositions.Values.Max(p => p.Y) + GraphLayoutEngine.NodeHeight;

            double contentWidth = maxX - minX + GraphLayoutEngine.NodePadding * 2;
            double contentHeight = maxY - minY + GraphLayoutEngine.NodePadding * 2;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;

            if (viewWidth <= 0 || viewHeight <= 0) return;

            double scaleX = viewWidth / contentWidth;
            double scaleY = viewHeight / contentHeight;
            _zoom = Math.Min(scaleX, scaleY) * 0.9;
            _zoom = Math.Clamp(_zoom, 0.3, 2.0);

            _scaleTransform.ScaleX = _zoom;
            _scaleTransform.ScaleY = _zoom;

            double contentCenterX = (minX + maxX) / 2;
            double contentCenterY = (minY + maxY) / 2;
            _translateTransform.X = viewWidth / 2 - contentCenterX * _zoom;
            _translateTransform.Y = viewHeight / 2 - contentCenterY * _zoom;
        }

        private void OnGraphKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F)
            {
                if (_selectedNodeId != null)
                {
                    FocusOnNodeById(_selectedNodeId);
                }
                else if (_activeTasks != null && _activeTasks.Count > 0)
                {
                    var firstId = _activeTasks.FirstOrDefault()?.Id;
                    if (firstId != null)
                    {
                        _selectedNodeId = firstId;
                        BuildHighlightChain(firstId);
                        RequestRebuildGraph?.Invoke();
                        FocusOnNodeById(firstId);
                    }
                }
                e.Handled = true;
            }
        }

        public void FocusOnTask(string taskId, Dictionary<string, Point> nodePositions)
        {
            if (!nodePositions.ContainsKey(taskId)) return;
            _selectedNodeId = taskId;
            BuildHighlightChain(taskId);
            RequestRebuildGraph?.Invoke();
            FocusOnNode(taskId, nodePositions);
        }

        private void FocusOnNodeById(string nodeId)
        {
            // This is called from keyboard handler - we need access to positions
            // which will be provided via the RequestFocusOnNode callback
            RequestFocusOnNode?.Invoke(nodeId);
        }

        public Action<string>? RequestFocusOnNode;

        public void FocusOnNode(string nodeId, Dictionary<string, Point> nodePositions)
        {
            if (!nodePositions.TryGetValue(nodeId, out var pos)) return;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0) return;

            double nodeCenterX = pos.X + GraphLayoutEngine.NodeWidth / 2;
            double nodeCenterY = pos.Y + GraphLayoutEngine.NodeHeight / 2;

            _translateTransform.X = viewWidth / 2 - nodeCenterX * _zoom;
            _translateTransform.Y = viewHeight / 2 - nodeCenterY * _zoom;
        }

        public void CenterOnGraph(Dictionary<string, Point> nodePositions)
        {
            if (nodePositions.Count == 0) return;

            double minX = nodePositions.Values.Min(p => p.X);
            double minY = nodePositions.Values.Min(p => p.Y);
            double maxX = nodePositions.Values.Max(p => p.X) + GraphLayoutEngine.NodeWidth;
            double maxY = nodePositions.Values.Max(p => p.Y) + GraphLayoutEngine.NodeHeight;

            double centerX = (minX + maxX) / 2;
            double centerY = (minY + maxY) / 2;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;

            if (viewWidth <= 0 || viewHeight <= 0) return;

            _translateTransform.X = viewWidth / 2 - centerX * _zoom;
            _translateTransform.Y = viewHeight / 2 - centerY * _zoom;
        }

        // ── Animation ─────────────────────────────────────────────────

        public void StartLayoutAnimation(Dictionary<string, Point> nodePositions, Dictionary<string, Point> targetPositions)
        {
            if (_isAnimating) return;
            _isAnimating = true;

            if (_animationTimer == null)
            {
                _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _animationTimer.Tick += (_, _) => OnAnimationTick(nodePositions, targetPositions);
            }
            _animationTimer.Start();
        }

        private void OnAnimationTick(Dictionary<string, Point> nodePositions, Dictionary<string, Point> targetPositions)
        {
            if (_activeTasks == null || _draggingNodeId != null)
            {
                StopLayoutAnimation();
                return;
            }

            bool allSettled = true;
            foreach (var id in targetPositions.Keys.ToList())
            {
                if (!nodePositions.ContainsKey(id)) continue;

                var current = nodePositions[id];
                var target = targetPositions[id];
                double dx = target.X - current.X;
                double dy = target.Y - current.Y;

                if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                {
                    nodePositions[id] = target;
                    continue;
                }

                allSettled = false;
                nodePositions[id] = new Point(
                    current.X + dx * 0.2,
                    current.Y + dy * 0.2);
            }

            RequestRedrawVisuals?.Invoke();

            if (allSettled)
                StopLayoutAnimation();
        }

        public void StopLayoutAnimation()
        {
            _animationTimer?.Stop();
            _isAnimating = false;
        }

        // ── Reset ─────────────────────────────────────────────────────

        public void ResetLayout(
            Dictionary<string, Point> nodePositions,
            Dictionary<string, Point> targetPositions)
        {
            StopLayoutAnimation();
            nodePositions.Clear();
            targetPositions.Clear();
            _userDraggedNodeIds.Clear();
            _zoom = 1.0;
            _scaleTransform.ScaleX = 1.0;
            _scaleTransform.ScaleY = 1.0;
            _translateTransform.X = 0;
            _translateTransform.Y = 0;
        }

        public void Dispose()
        {
            StopLayoutAnimation();
            _animationTimer = null;
        }
    }
}
