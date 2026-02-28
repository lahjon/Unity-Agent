using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgenticEngine.Helpers;
using AgenticEngine.Managers;

namespace AgenticEngine.Controls
{
    public class TaskNodeGraphPanel : Border
    {
        private const double NodeWidth = 180;
        private const double NodeHeight = 72;
        private const double NodePadding = 30;
        private const double HierarchyIndent = 200;
        private const double HierarchyRowGap = 80;

        private Canvas _canvas = null!;
        private ScrollViewer _scrollViewer = null!;
        private Border _legendPanel = null!;
        private TextBlock _nodeCountText = null!;

        private ObservableCollection<AgentTask>? _activeTasks;
        private FileLockManager? _fileLockManager;

        private readonly Dictionary<string, Point> _nodePositions = new();
        private readonly Dictionary<string, Point> _targetPositions = new();
        private readonly Dictionary<string, Border> _nodeBorders = new();
        private readonly List<UIElement> _edgeElements = new();

        // Animation state
        private DispatcherTimer? _animationTimer;
        private bool _isAnimating;

        // Node drag state
        private string? _draggingNodeId;
        private Point _dragNodeOffset;
        private readonly HashSet<string> _userDraggedNodeIds = new();

        // Drag-to-connect state
        private string? _dragSourceId = null;
        private Line? _dragLine = null;
        private Point _rightClickStartPos;
        private bool _rightClickDragging;

        // Grid state
        private bool _showGrid = true;
        private Brush _gridBrush = null!;

        // Progress panel
        private Border _progressPanel = null!;
        private StackPanel _progressStack = null!;
        private bool _showProgress;
        private ColumnDefinition _progressColumn = null!;

        // Selection / highlight state
        private string? _selectedNodeId;
        private HashSet<string> _highlightedNodeIds = new();
        private HashSet<string> _highlightedEdgeKeys = new();

        // Zoom state
        private double _zoom = 1.0;
        private ScaleTransform _scaleTransform = null!;
        private TranslateTransform _translateTransform = null!;
        private TransformGroup _canvasTransformGroup = null!;

        // Pan state
        private bool _isPanning;
        private Point _panStart;
        private Point _panOrigin;

        // Auto-refresh timer
        private DispatcherTimer? _refreshTimer;
        private bool _needsInitialCenter;

        // Dirty-flag: only rebuild graph when something changed
        private bool _graphDirty = true;

        // Events for MainWindow integration
        public event Action<AgentTask>? CancelRequested;
        public event Action<AgentTask>? PauseResumeRequested;
        public event Action<AgentTask>? ShowOutputRequested;
        public event Action<AgentTask>? CopyPromptRequested;
        public event Action<AgentTask>? RevertRequested;
        public event Action<AgentTask>? ContinueRequested;
        public event Action<AgentTask>? ForceStartRequested;
        public event Action<AgentTask, AgentTask>? DependencyCreated; // (source, target) — target depends on source
        public event Action<AgentTask>? DependenciesRemoved;

        public TaskNodeGraphPanel()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Background = (Brush)Application.Current.FindResource("BgSurface");
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C));
            Padding = new Thickness(0);

            var root = new DockPanel();

            // Header bar
            var header = new DockPanel { Margin = new Thickness(8, 6, 8, 4) };

            var titleBlock = new TextBlock
            {
                Text = "GRAPH VIEW",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontWeight = FontWeights.Bold,
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };

            _nodeCountText = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            // Right-side buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttonPanel, Dock.Right);

            var legendBtn = CreateHeaderButton("\uE946", "Toggle legend");
            legendBtn.Click += (_, _) =>
            {
                _legendPanel.Visibility = _legendPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
            };

            var resetBtn = CreateHeaderButton("\uE72C", "Reset layout");
            resetBtn.Click += (_, _) =>
            {
                StopLayoutAnimation();
                _nodePositions.Clear();
                _targetPositions.Clear();
                _userDraggedNodeIds.Clear();
                _zoom = 1.0;
                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
                _translateTransform.X = 0;
                _translateTransform.Y = 0;
                RebuildGraph();
                Dispatcher.BeginInvoke(CenterOnGraph, DispatcherPriority.Loaded);
            };

            var fitBtn = CreateHeaderButton("\uE740", "Fit to view");
            fitBtn.Click += (_, _) => FitToView();

            var gridBtn = CreateHeaderButton("\uE80A", "Toggle grid");
            gridBtn.Click += (_, _) =>
            {
                _showGrid = !_showGrid;
                _canvas.Background = _showGrid ? _gridBrush : Brushes.Transparent;
            };

            var progressBtn = CreateHeaderButton("\uE9D2", "Toggle subtask progress panel");
            progressBtn.Click += (_, _) => ToggleProgressPanel();

            buttonPanel.Children.Add(fitBtn);
            buttonPanel.Children.Add(resetBtn);
            buttonPanel.Children.Add(gridBtn);
            buttonPanel.Children.Add(progressBtn);
            buttonPanel.Children.Add(legendBtn);

            header.Children.Add(buttonPanel);
            header.Children.Add(titleBlock);
            header.Children.Add(_nodeCountText);

            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Legend panel (visible by default)
            _legendPanel = CreateLegendPanel();
            _legendPanel.Visibility = Visibility.Visible;
            DockPanel.SetDock(_legendPanel, Dock.Bottom);
            root.Children.Add(_legendPanel);

            // Scrollable canvas area
            _scaleTransform = new ScaleTransform(1.0, 1.0);
            _translateTransform = new TranslateTransform(0, 0);
            _canvasTransformGroup = new TransformGroup();
            _canvasTransformGroup.Children.Add(_scaleTransform);
            _canvasTransformGroup.Children.Add(_translateTransform);

            _gridBrush = CreateGridBrush();

            _canvas = new Canvas
            {
                Background = _gridBrush,
                ClipToBounds = true,
                RenderTransform = _canvasTransformGroup,
                RenderTransformOrigin = new Point(0, 0),
                Width = 2000,
                Height = 1200
            };

            _scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                Content = _canvas,
                Focusable = true
            };

            // Mouse wheel zoom
            _scrollViewer.PreviewMouseWheel += OnMouseWheel;

            // Pan with middle mouse
            _scrollViewer.PreviewMouseDown += OnPanStart;
            _scrollViewer.PreviewMouseMove += OnPanMove;
            _scrollViewer.PreviewMouseUp += OnPanEnd;

            // Focus on node with F key
            _scrollViewer.PreviewKeyDown += OnGraphKeyDown;

            // Deselect on canvas click & grab focus for keyboard shortcuts
            _canvas.MouseLeftButtonDown += (_, e) =>
            {
                _scrollViewer.Focus();
                if (e.OriginalSource == _canvas)
                {
                    _selectedNodeId = null;
                    _highlightedNodeIds.Clear();
                    _highlightedEdgeKeys.Clear();
                    RebuildGraph();
                }
            };

            // Progress panel (right side, collapsed by default)
            _progressStack = new StackPanel();

            var progressHeader = new TextBlock
            {
                Text = "Subtask Progress",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var progressScrollContent = new StackPanel { Margin = new Thickness(8) };
            progressScrollContent.Children.Add(progressHeader);
            progressScrollContent.Children.Add(_progressStack);

            var progressScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = progressScrollContent
            };

            _progressPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 36)),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Child = progressScroll,
                Visibility = Visibility.Collapsed
            };

            // Content grid: graph canvas (left) + progress panel (right)
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _progressColumn = new ColumnDefinition { Width = new GridLength(0) };
            contentGrid.ColumnDefinitions.Add(_progressColumn);

            Grid.SetColumn(_scrollViewer, 0);
            Grid.SetColumn(_progressPanel, 1);
            contentGrid.Children.Add(_scrollViewer);
            contentGrid.Children.Add(_progressPanel);

            root.Children.Add(contentGrid);
            Child = root;
        }

        public void Initialize(ObservableCollection<AgentTask> activeTasks, FileLockManager fileLockManager)
        {
            if (_activeTasks != null)
                _activeTasks.CollectionChanged -= OnTasksChanged;

            _activeTasks = activeTasks;
            _fileLockManager = fileLockManager;
            _activeTasks.CollectionChanged += OnTasksChanged;

            _refreshTimer?.Stop();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) =>
            {
                if (_draggingNodeId == null && _dragSourceId == null && _graphDirty)
                {
                    RebuildGraph();
                    _graphDirty = false;
                }
            };
            _refreshTimer.Start();

            _needsInitialCenter = true;
            _graphDirty = true;
            RebuildGraph();
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            _graphDirty = true;
            Dispatcher.BeginInvoke(RebuildGraph);
        }

        /// <summary>Marks the graph as needing a rebuild on the next timer tick or explicit call.</summary>
        public void MarkDirty() => _graphDirty = true;

        // ── Graph Rendering ──────────────────────────────────────────

        public void RebuildGraph()
        {
            if (_activeTasks == null || _fileLockManager == null) return;

            _canvas.Children.Clear();
            _nodeBorders.Clear();

            var tasks = _activeTasks.ToList();

            // Update node count
            int running = tasks.Count(t => t.IsRunning);
            int queued = tasks.Count(t => t.IsQueued || t.IsInitQueued);
            int subtasks = tasks.Count(t => t.IsSubTask);
            _nodeCountText.Text = tasks.Count > 0
                ? $" ({tasks.Count} tasks, {running} running, {queued} queued" +
                  (subtasks > 0 ? $", {subtasks} subtasks)" : ")")
                : " (no tasks)";

            if (tasks.Count == 0)
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
                return;
            }

            // Remove positions for tasks that no longer exist
            var taskIds = new HashSet<string>(tasks.Select(t => t.Id));
            foreach (var key in _nodePositions.Keys.ToList())
            {
                if (!taskIds.Contains(key))
                    _nodePositions.Remove(key);
            }
            foreach (var key in _targetPositions.Keys.ToList())
            {
                if (!taskIds.Contains(key))
                    _targetPositions.Remove(key);
            }
            _userDraggedNodeIds.IntersectWith(taskIds);

            // Assign positions using hierarchy-aware layout
            LayoutNodes(tasks);

            // Draw edges first (behind nodes)
            DrawAllEdges(tasks);

            // Draw nodes
            foreach (var task in tasks)
                DrawNode(task, tasks);

            // Resize canvas to fit content
            ResizeCanvas();

            // Center on graph on first build (deferred so viewport has a size)
            if (_needsInitialCenter && _nodePositions.Count > 0)
            {
                _needsInitialCenter = false;
                Dispatcher.BeginInvoke(CenterOnGraph, DispatcherPriority.Loaded);
            }

            // Update progress panel if visible
            RebuildProgressPanel();
        }

        // ── Layout ───────────────────────────────────────────────────

        private void LayoutNodes(List<AgentTask> tasks)
        {
            var taskMap = tasks.ToDictionary(t => t.Id);
            var computed = new Dictionary<string, Point>();
            double currentY = NodePadding;

            // Phase 1: Hierarchy layout (parent tasks with their children)
            var parents = tasks
                .Where(t => t.ChildTaskIds != null && t.ChildTaskIds.Count > 0
                            && t.ChildTaskIds.Any(cid => taskMap.ContainsKey(cid)))
                .ToList();

            var hierarchyIds = new HashSet<string>();

            foreach (var parent in parents)
            {
                hierarchyIds.Add(parent.Id);
                computed[parent.Id] = new Point(NodePadding, currentY);

                double childY = currentY + HierarchyRowGap;
                foreach (var childId in parent.ChildTaskIds)
                {
                    if (!taskMap.ContainsKey(childId)) continue;
                    hierarchyIds.Add(childId);

                    if (!computed.ContainsKey(childId))
                        computed[childId] = new Point(NodePadding + HierarchyIndent, childY);

                    childY += NodeHeight + 24;
                }

                currentY = childY + 20;
            }

            // Place orphan subtasks (ParentTaskId set but parent didn't list them)
            foreach (var task in tasks)
            {
                if (!string.IsNullOrEmpty(task.ParentTaskId) && !hierarchyIds.Contains(task.Id))
                {
                    hierarchyIds.Add(task.Id);
                    if (!computed.ContainsKey(task.Id))
                    {
                        computed[task.Id] = new Point(NodePadding + HierarchyIndent, currentY);
                        currentY += NodeHeight + 24;
                    }
                }
            }

            // Phase 2: Topological layout for remaining (non-hierarchy) tasks
            var orphans = tasks.Where(t => !hierarchyIds.Contains(t.Id)).ToList();

            if (orphans.Count > 0)
            {
                var orphanPlaced = new HashSet<string>();
                var layers = new List<List<AgentTask>>();
                var remaining = new List<AgentTask>(orphans);

                while (remaining.Count > 0)
                {
                    var layer = remaining.Where(t =>
                    {
                        if (orphanPlaced.Contains(t.Id)) return false;
                        if (t.DependencyTaskIds == null || t.DependencyTaskIds.Count == 0)
                            return true;
                        return t.DependencyTaskIds.All(d =>
                            orphanPlaced.Contains(d) || !remaining.Any(r => r.Id == d));
                    }).ToList();

                    if (layer.Count == 0)
                        layer = remaining.Where(t => !orphanPlaced.Contains(t.Id)).ToList();

                    layers.Add(layer);
                    foreach (var t in layer)
                        orphanPlaced.Add(t.Id);
                    remaining = remaining.Where(t => !orphanPlaced.Contains(t.Id)).ToList();
                }

                double orphanStartY = currentY > NodePadding ? currentY + 20 : NodePadding;
                double x = NodePadding;
                for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
                {
                    var layer = layers[layerIdx];
                    double y = orphanStartY;
                    for (int i = 0; i < layer.Count; i++)
                    {
                        var t = layer[i];
                        if (!computed.ContainsKey(t.Id))
                            computed[t.Id] = new Point(x, y);
                        y += NodeHeight + 30;
                    }
                    x += NodeWidth + 80;
                }
            }

            // Apply computed positions — snap all nodes to prevent overlap.
            // Preserve positions for user-dragged nodes; all others get computed positions.
            foreach (var kvp in computed)
            {
                if (_userDraggedNodeIds.Contains(kvp.Key) && _nodePositions.ContainsKey(kvp.Key))
                {
                    // Keep user-dragged position
                    _targetPositions[kvp.Key] = _nodePositions[kvp.Key];
                }
                else
                {
                    _targetPositions[kvp.Key] = kvp.Value;
                    _nodePositions[kvp.Key] = kvp.Value;
                }
            }

            // During active drag, keep dragged node at its drag position
            if (_draggingNodeId != null && _nodePositions.ContainsKey(_draggingNodeId))
            {
                _targetPositions[_draggingNodeId] = _nodePositions[_draggingNodeId];
            }
        }

        // ── Animation ────────────────────────────────────────────────

        private void StartLayoutAnimation()
        {
            if (_isAnimating) return;
            _isAnimating = true;

            if (_animationTimer == null)
            {
                _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _animationTimer.Tick += OnAnimationTick;
            }
            _animationTimer.Start();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            if (_activeTasks == null || _fileLockManager == null || _draggingNodeId != null)
            {
                StopLayoutAnimation();
                return;
            }

            bool allSettled = true;
            foreach (var id in _targetPositions.Keys.ToList())
            {
                if (!_nodePositions.ContainsKey(id)) continue;

                var current = _nodePositions[id];
                var target = _targetPositions[id];
                double dx = target.X - current.X;
                double dy = target.Y - current.Y;

                if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                {
                    _nodePositions[id] = target;
                    continue;
                }

                allSettled = false;
                _nodePositions[id] = new Point(
                    current.X + dx * 0.2,
                    current.Y + dy * 0.2);
            }

            // Redraw at current interpolated positions
            RedrawGraphVisuals();

            if (allSettled)
                StopLayoutAnimation();
        }

        private void RedrawGraphVisuals()
        {
            if (_activeTasks == null || _fileLockManager == null) return;

            _canvas.Children.Clear();
            _nodeBorders.Clear();

            var tasks = _activeTasks.ToList();
            if (tasks.Count == 0) return;

            DrawAllEdges(tasks);
            foreach (var task in tasks)
                DrawNode(task, tasks);
            ResizeCanvas();
        }

        private void StopLayoutAnimation()
        {
            _animationTimer?.Stop();
            _isAnimating = false;
        }

        // ── Edge Drawing ─────────────────────────────────────────────

        private void DrawAllEdges(List<AgentTask> tasks)
        {
            _edgeElements.Clear();
            bool hasHighlight = _highlightedEdgeKeys.Count > 0;

            // 1) Dependency edges (solid blue)
            foreach (var task in tasks)
            {
                if (task.DependencyTaskIds != null)
                {
                    foreach (var depId in task.DependencyTaskIds)
                    {
                        if (_nodePositions.ContainsKey(depId) && _nodePositions.ContainsKey(task.Id))
                        {
                            var from = _nodePositions[depId];
                            var to = _nodePositions[task.Id];
                            string edgeKey = $"{depId}->{task.Id}";
                            bool highlighted = _highlightedEdgeKeys.Contains(edgeKey);
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
                    _nodePositions.ContainsKey(task.BlockedByTaskId) &&
                    _nodePositions.ContainsKey(task.Id))
                {
                    var from = _nodePositions[task.BlockedByTaskId];
                    var to = _nodePositions[task.Id];
                    string edgeKey = $"{task.BlockedByTaskId}->{task.Id}";
                    bool highlighted = _highlightedEdgeKeys.Contains(edgeKey);
                    string color = highlighted ? "#FFB74D" : "#FFA726";
                    double thickness = highlighted ? 3 : 2;
                    double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                    DrawBezierEdge(from, to, color, false, thickness, opacity);
                    DrawArrowHead(from, to, color, opacity);
                }
            }

            // 2) File-lock edges (dashed red)
            var queuedInfos = _fileLockManager!.QueuedTaskInfos;
            foreach (var kvp in queuedInfos)
            {
                var info = kvp.Value;
                if (info.BlockedByTaskIds != null)
                {
                    foreach (var blockerId in info.BlockedByTaskIds)
                    {
                        if (_nodePositions.ContainsKey(blockerId) && _nodePositions.ContainsKey(kvp.Key))
                        {
                            var from = _nodePositions[blockerId];
                            var to = _nodePositions[kvp.Key];
                            string edgeKey = $"{blockerId}->{kvp.Key}";
                            bool highlighted = _highlightedEdgeKeys.Contains(edgeKey);
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
                    if (!_nodePositions.ContainsKey(task.Id) || !_nodePositions.ContainsKey(childId))
                        continue;

                    var parentPos = _nodePositions[task.Id];
                    var childPos = _nodePositions[childId];

                    string edgeKey = $"{task.Id}=>{childId}";
                    bool highlighted = _highlightedEdgeKeys.Contains(edgeKey);
                    double opacity = (hasHighlight && !highlighted) ? 0.25 : 1.0;
                    var greenBrush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(highlighted ? "#81C784" : "#66BB6A"));
                    greenBrush.Opacity = opacity;

                    // Vertical line from parent bottom to child row
                    double startX = parentPos.X + NodeWidth / 2;
                    double startY = parentPos.Y + NodeHeight;
                    double midY = childPos.Y + NodeHeight / 2;

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

                    // Horizontal line from vertical to child node
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

                    // Arrow at child end
                    DrawArrowHead(
                        new Point(startX - NodeWidth / 2, midY - NodeHeight / 2),
                        childPos, highlighted ? "#81C784" : "#66BB6A", opacity);
                }
            }
        }

        // ── Viewport Culling ──────────────────────────────────────────

        /// <summary>
        /// Returns true if a node at the given canvas position is entirely outside the visible
        /// ScrollViewer viewport, accounting for the current pan and zoom transforms.
        /// </summary>
        private bool IsNodeOutsideViewport(Point nodePos)
        {
            // Transform the node's canvas-space rectangle to screen-space
            double scale = _scaleTransform.ScaleX;
            double tx = _translateTransform.X;
            double ty = _translateTransform.Y;

            // Node screen-space bounds (top-left corner in canvas coords is nodePos)
            double screenLeft = nodePos.X * scale + tx;
            double screenTop = nodePos.Y * scale + ty;
            double screenRight = (nodePos.X + NodeWidth) * scale + tx;
            double screenBottom = (nodePos.Y + NodeHeight) * scale + ty;

            // Visible viewport from the ScrollViewer
            double vpWidth = _scrollViewer.ActualWidth;
            double vpHeight = _scrollViewer.ActualHeight;

            // If the viewport hasn't been measured yet, don't cull anything
            if (vpWidth <= 0 || vpHeight <= 0)
                return false;

            // Check if the node is entirely outside the viewport
            return screenRight < 0 || screenLeft > vpWidth ||
                   screenBottom < 0 || screenTop > vpHeight;
        }

        // ── Node Drawing ─────────────────────────────────────────────

        private void DrawNode(AgentTask task, List<AgentTask> allTasks)
        {
            if (!_nodePositions.TryGetValue(task.Id, out var pos))
                return;

            // Viewport culling: skip rendering nodes entirely outside the visible area
            if (IsNodeOutsideViewport(pos))
                return;

            bool isSelected = _selectedNodeId == task.Id;
            bool isHighlighted = _highlightedNodeIds.Contains(task.Id);
            bool hasFocusedSelection = _highlightedNodeIds.Count > 0;
            double nodeOpacity = hasFocusedSelection && !isHighlighted && !isSelected ? 0.3 : 1.0;

            var statusColor = (Color)ColorConverter.ConvertFromString(task.StatusColor);
            var statusBrush = new SolidColorBrush(statusColor);
            bool isParent = task.HasChildren;
            bool isSubtask = task.IsSubTask;

            var nodeBorder = new Border
            {
                Width = NodeWidth,
                Height = NodeHeight,
                Background = new SolidColorBrush(isSelected
                    ? Color.FromArgb(255, 50, 50, 60)
                    : Color.FromArgb(230, 40, 40, 48)),
                BorderBrush = isSelected
                    ? new SolidColorBrush(Color.FromRgb(0x90, 0xCA, 0xF9))
                    : statusBrush,
                BorderThickness = new Thickness(isParent ? 3 : isSelected ? 2.5 : 2),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Tag = task.Id,
                Opacity = nodeOpacity
            };

            // Add glow effect for running tasks
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

            // Right-side badges
            var badgePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(badgePanel, Dock.Right);

            // Project badge
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

            // Subtask indicator
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

            // Parent children count
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

            // Task label (hierarchy-aware)
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

            // Dep/group indicators on the right
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

            // Tooltip with full details
            nodeBorder.ToolTip = CreateNodeTooltip(task, allTasks);

            Canvas.SetLeft(nodeBorder, pos.X);
            Canvas.SetTop(nodeBorder, pos.Y);
            _canvas.Children.Add(nodeBorder);
            _nodeBorders[task.Id] = nodeBorder;

            // Context Menu
            nodeBorder.ContextMenu = CreateNodeContextMenu(task);

            // ── Mouse interactions ──
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
                    var currentPos = _nodePositions[capturedTaskId];
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
                        mousePos.Y - _dragNodeOffset.Y));
                    e.Handled = true;
                }
                else if (_dragSourceId == capturedTaskId && e.RightButton == MouseButtonState.Pressed)
                {
                    var mousePos2 = e.GetPosition(_canvas);

                    // Start drag line only after moving beyond threshold
                    if (!_rightClickDragging)
                    {
                        var dx = mousePos2.X - _rightClickStartPos.X;
                        var dy = mousePos2.Y - _rightClickStartPos.Y;
                        if (dx * dx + dy * dy < 25) return; // 5px threshold

                        _rightClickDragging = true;
                        var nodePos = _nodePositions[capturedTaskId];
                        _dragLine = new Line
                        {
                            X1 = nodePos.X + NodeWidth / 2,
                            Y1 = nodePos.Y + NodeHeight / 2,
                            X2 = mousePos2.X,
                            Y2 = mousePos2.Y,
                            Stroke = new SolidColorBrush(Color.FromRgb(100, 181, 246)),
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

            // ── Right-click: drag to create dependency edge, or click for context menu ──
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

                // No drag happened — show context menu
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

                // Drag happened — try to connect dependency
                var mousePos = e.GetPosition(_canvas);
                if (_dragLine != null)
                {
                    _canvas.Children.Remove(_dragLine);
                    _dragLine = null;
                }

                // Find target node under mouse
                string? targetId = null;
                foreach (var kvp in _nodeBorders)
                {
                    if (kvp.Key == _dragSourceId) continue;
                    var nodePos = _nodePositions[kvp.Key];
                    if (mousePos.X >= nodePos.X && mousePos.X <= nodePos.X + NodeWidth &&
                        mousePos.Y >= nodePos.Y && mousePos.Y <= nodePos.Y + NodeHeight)
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
                            if (WouldCreateCycle(_activeTasks, _dragSourceId, targetId))
                            {
                                // Flash target node red briefly
                                if (_nodeBorders.TryGetValue(targetId, out var flashBorder))
                                {
                                    var origBg = flashBorder.Background;
                                    flashBorder.Background = new SolidColorBrush(Color.FromArgb(180, 220, 50, 50));
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
                                    RebuildGraph();
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
            nodeBorder.MouseEnter += (s, _) =>
            {
                if (_draggingNodeId == null && _highlightedNodeIds.Count == 0)
                    nodeBorder.Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 58));
            };
            nodeBorder.MouseLeave += (s, _) =>
            {
                if (_draggingNodeId == null && _highlightedNodeIds.Count == 0)
                    nodeBorder.Background = new SolidColorBrush(
                        isSelected ? Color.FromArgb(255, 50, 50, 60) : Color.FromArgb(230, 40, 40, 48));
            };
        }

        // ── Context Menu ─────────────────────────────────────────────

        private ContextMenu CreateNodeContextMenu(AgentTask task)
        {
            var menu = new ContextMenu();

            // Show Output
            var showOutputItem = new MenuItem { Header = "Show Output" };
            showOutputItem.Click += (_, _) => ShowOutputRequested?.Invoke(task);
            menu.Items.Add(showOutputItem);

            menu.Items.Add(new Separator());

            // Highlight Dependencies
            var highlightItem = new MenuItem { Header = "Highlight Dependency Chain" };
            highlightItem.Click += (_, _) => ToggleNodeSelection(task.Id);
            menu.Items.Add(highlightItem);

            // Remove All Dependencies
            bool hasOwnDeps = task.DependencyTaskIdCount > 0;
            bool hasIncoming = _activeTasks?.Any(t => t.Id != task.Id && t.ContainsDependencyTaskId(task.Id)) == true;
            if (hasOwnDeps || hasIncoming)
            {
                var removeDepsItem = new MenuItem { Header = "Remove All Dependencies" };
                removeDepsItem.Click += (_, _) =>
                {
                    DependenciesRemoved?.Invoke(task);
                    RebuildGraph();
                };
                menu.Items.Add(removeDepsItem);
            }

            menu.Items.Add(new Separator());

            // Pause/Resume (only for running/paused)
            if (task.IsRunning || task.IsPaused)
            {
                var pauseItem = new MenuItem
                {
                    Header = task.IsPaused ? "Resume" : "Pause"
                };
                pauseItem.Click += (_, _) => PauseResumeRequested?.Invoke(task);
                menu.Items.Add(pauseItem);
            }

            // Force Start (only for queued tasks)
            if (task.IsQueued || task.IsInitQueued)
            {
                var forceItem = new MenuItem { Header = "Force Start" };
                forceItem.Click += (_, _) => ForceStartRequested?.Invoke(task);
                menu.Items.Add(forceItem);
            }

            // Continue (only for finished with recommendations)
            if (task.IsFinished && task.HasRecommendations)
            {
                var continueItem = new MenuItem { Header = "Continue (Recommendations)" };
                continueItem.Click += (_, _) => ContinueRequested?.Invoke(task);
                menu.Items.Add(continueItem);
            }

            // Copy Prompt
            var copyItem = new MenuItem { Header = "Copy Prompt" };
            copyItem.Click += (_, _) => CopyPromptRequested?.Invoke(task);
            menu.Items.Add(copyItem);

            menu.Items.Add(new Separator());

            // Cancel / Remove
            var cancelItem = new MenuItem
            {
                Header = task.IsFinished ? "Remove" : "Cancel"
            };
            cancelItem.Click += (_, _) => CancelRequested?.Invoke(task);
            menu.Items.Add(cancelItem);

            // Revert
            if (!string.IsNullOrEmpty(task.GitStartHash))
            {
                var revertItem = new MenuItem { Header = "Revert Changes" };
                revertItem.Click += (_, _) => RevertRequested?.Invoke(task);
                menu.Items.Add(revertItem);
            }

            return menu;
        }

        // ── Dependency Chain Highlighting ─────────────────────────────

        private void ToggleNodeSelection(string taskId)
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
            RebuildGraph();
        }

        private void BuildHighlightChain(string taskId)
        {
            _highlightedNodeIds.Clear();
            _highlightedEdgeKeys.Clear();

            if (_activeTasks == null) return;

            var tasks = _activeTasks.ToList();
            var taskMap = tasks.ToDictionary(t => t.Id);

            _highlightedNodeIds.Add(taskId);

            // Walk upstream (dependencies + parent)
            WalkUpstream(taskId, taskMap);

            // Walk downstream (dependents + children)
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

            // Walk to parent
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

                // Walk to children
                if (task.ParentTaskId == taskId)
                {
                    _highlightedEdgeKeys.Add($"{taskId}=>{task.Id}");
                    if (_highlightedNodeIds.Add(task.Id))
                        WalkDownstream(task.Id, tasks);
                }
            }
        }

        // ── Node Tooltip ─────────────────────────────────────────────

        private ToolTip CreateNodeTooltip(AgentTask task, List<AgentTask> allTasks)
        {
            var panel = new StackPanel { MaxWidth = 400 };

            // Title
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

            // Description
            panel.Children.Add(new TextBlock
            {
                Text = task.Description ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Project
            panel.Children.Add(new TextBlock
            {
                Text = $"Project: {task.ProjectName}",
                Foreground = BrushCache.Get(task.ProjectColor),
                FontSize = 10
            });

            // Time info
            panel.Children.Add(new TextBlock
            {
                Text = task.TimeInfo,
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0)
            });

            // Tokens
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

            // Group
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

            // Dependencies
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

            // Blocked by
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

            // Parent task
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

            // Child tasks progress
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

            // Tool activity
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

            // Completion summary
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

            // Hints
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

        private Border CreateLegendPanel()
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

            // Edge legend
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

            // Status legend
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

        // ── Drawing Helpers ──────────────────────────────────────────

        private void DrawBezierEdge(Point from, Point to, string colorHex, bool dashed,
            double thickness = 2, double opacity = 1.0)
        {
            var fromCenter = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);
            var toCenter = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);

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
            var toCenter = new Point(to.X + NodeWidth / 2, to.Y + NodeHeight / 2);
            var fromCenter = new Point(from.X + NodeWidth / 2, from.Y + NodeHeight / 2);

            double dx = toCenter.X - fromCenter.X;
            double dy = toCenter.Y - fromCenter.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;

            dx /= len;
            dy /= len;

            double arrowTipX = toCenter.X - dx * (NodeWidth / 2 + 2);
            double arrowTipY = toCenter.Y - dy * (NodeHeight / 2 + 2);

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

        // ── Zoom & Pan ───────────────────────────────────────────────

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

            e.Handled = true;

            double delta = e.Delta > 0 ? 1.1 : 0.9;
            _zoom = Math.Clamp(_zoom * delta, 0.3, 3.0);

            _scaleTransform.ScaleX = _zoom;
            _scaleTransform.ScaleY = _zoom;
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

        private void FitToView()
        {
            if (_nodePositions.Count == 0) return;

            double minX = _nodePositions.Values.Min(p => p.X);
            double minY = _nodePositions.Values.Min(p => p.Y);
            double maxX = _nodePositions.Values.Max(p => p.X) + NodeWidth;
            double maxY = _nodePositions.Values.Max(p => p.Y) + NodeHeight;

            double contentWidth = maxX - minX + NodePadding * 2;
            double contentHeight = maxY - minY + NodePadding * 2;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;

            if (viewWidth <= 0 || viewHeight <= 0) return;

            double scaleX = viewWidth / contentWidth;
            double scaleY = viewHeight / contentHeight;
            _zoom = Math.Min(scaleX, scaleY) * 0.9;
            _zoom = Math.Clamp(_zoom, 0.3, 2.0);

            _scaleTransform.ScaleX = _zoom;
            _scaleTransform.ScaleY = _zoom;

            _translateTransform.X = -minX * _zoom + NodePadding;
            _translateTransform.Y = -minY * _zoom + NodePadding;
        }

        private void OnGraphKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F)
            {
                if (_selectedNodeId != null && _nodePositions.ContainsKey(_selectedNodeId))
                {
                    FocusOnNode(_selectedNodeId);
                }
                else if (_nodePositions.Count > 0)
                {
                    // Focus the first node (topmost task in the active list)
                    var firstId = _activeTasks?.FirstOrDefault()?.Id;
                    if (firstId != null && _nodePositions.ContainsKey(firstId))
                    {
                        _selectedNodeId = firstId;
                        BuildHighlightChain(firstId);
                        RebuildGraph();
                        FocusOnNode(firstId);
                    }
                    else
                    {
                        // Fallback: use first available positioned node
                        var fallbackId = _nodePositions.Keys.First();
                        _selectedNodeId = fallbackId;
                        BuildHighlightChain(fallbackId);
                        RebuildGraph();
                        FocusOnNode(fallbackId);
                    }
                }
                e.Handled = true;
            }
        }

        private void FocusOnNode(string nodeId)
        {
            if (!_nodePositions.TryGetValue(nodeId, out var pos)) return;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0) return;

            double nodeCenterX = pos.X + NodeWidth / 2;
            double nodeCenterY = pos.Y + NodeHeight / 2;

            _translateTransform.X = viewWidth / 2 - nodeCenterX * _zoom;
            _translateTransform.Y = viewHeight / 2 - nodeCenterY * _zoom;
        }

        private void CenterOnGraph()
        {
            if (_nodePositions.Count == 0) return;

            double minX = _nodePositions.Values.Min(p => p.X);
            double minY = _nodePositions.Values.Min(p => p.Y);
            double maxX = _nodePositions.Values.Max(p => p.X) + NodeWidth;
            double maxY = _nodePositions.Values.Max(p => p.Y) + NodeHeight;

            double centerX = (minX + maxX) / 2;
            double centerY = (minY + maxY) / 2;

            double viewWidth = _scrollViewer.ActualWidth;
            double viewHeight = _scrollViewer.ActualHeight;

            if (viewWidth <= 0 || viewHeight <= 0) return;

            _translateTransform.X = viewWidth / 2 - centerX * _zoom;
            _translateTransform.Y = viewHeight / 2 - centerY * _zoom;
        }

        private void ResizeCanvas()
        {
            if (_nodePositions.Count == 0) return;

            double maxX = _nodePositions.Values.Max(p => p.X) + NodeWidth + NodePadding * 2;
            double maxY = _nodePositions.Values.Max(p => p.Y) + NodeHeight + NodePadding * 2;

            _canvas.Width = Math.Max(2000, maxX);
            _canvas.Height = Math.Max(1200, maxY);
        }

        // ── Progress Panel ──────────────────────────────────────────

        private void ToggleProgressPanel()
        {
            _showProgress = !_showProgress;
            if (_showProgress)
            {
                _progressPanel.Visibility = Visibility.Visible;
                _progressColumn.Width = new GridLength(240);
                RebuildProgressPanel();
            }
            else
            {
                _progressPanel.Visibility = Visibility.Collapsed;
                _progressColumn.Width = new GridLength(0);
            }
        }

        private void RebuildProgressPanel()
        {
            if (!_showProgress || _activeTasks == null) return;

            _progressStack.Children.Clear();
            var tasks = _activeTasks.ToList();
            var taskMap = tasks.ToDictionary(t => t.Id);

            var parents = tasks.Where(t => t.ChildTaskIds != null && t.ChildTaskIds.Count > 0).ToList();

            if (parents.Count == 0)
            {
                _progressStack.Children.Add(new TextBlock
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

                // Progress bar
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

                // Child task list
                foreach (var child in children)
                {
                    if (child == null) continue;
                    var statusColor = (Color)ColorConverter.ConvertFromString(child.StatusColor);
                    var childRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(4, 3, 0, 0)
                    };

                    childRow.Children.Add(new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = new SolidColorBrush(statusColor),
                        Margin = new Thickness(0, 2, 6, 0)
                    });

                    childRow.Children.Add(new TextBlock
                    {
                        Text = $"#{child.TaskNumber} {child.Status}",
                        Foreground = new SolidColorBrush(statusColor),
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
                _progressStack.Children.Add(card);
            }
        }

        /// <summary>
        /// Moves a single node without rebuilding the entire graph.
        /// Updates position, repositions the Border, and redraws only edges.
        /// </summary>
        private void MoveNode(string taskId, Point newPos)
        {
            _nodePositions[taskId] = newPos;

            if (_nodeBorders.TryGetValue(taskId, out var border))
            {
                Canvas.SetLeft(border, newPos.X);
                Canvas.SetTop(border, newPos.Y);
            }

            // Remove only edge elements from canvas
            foreach (var elem in _edgeElements)
                _canvas.Children.Remove(elem);

            // Redraw edges using current positions
            if (_activeTasks != null)
                DrawAllEdges(_activeTasks.ToList());

            ResizeCanvas();
        }

        // ── Helpers ──────────────────────────────────────────────────

        private string GetCompactTimeInfo(AgentTask task)
        {
            if (task.EndTime.HasValue)
            {
                var duration = task.EndTime.Value - task.StartTime;
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
            var running = DateTime.Now - task.StartTime;
            return $"{(int)running.TotalMinutes}m {running.Seconds}s";
        }

        private Brush CreateGridBrush()
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

        private Button CreateHeaderButton(string icon, string tooltip)
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
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public void Dispose()
        {
            StopLayoutAnimation();
            _animationTimer = null;
            _refreshTimer?.Stop();
            _refreshTimer = null;
            if (_activeTasks != null)
                _activeTasks.CollectionChanged -= OnTasksChanged;
        }

        /// <summary>
        /// Checks if adding a dependency where targetId depends on sourceId would create a cycle.
        /// BFS from sourceId following DependencyTaskIds — if we can reach targetId, a cycle exists.
        /// </summary>
        private static bool WouldCreateCycle(IEnumerable<AgentTask> tasks, string sourceId, string targetId)
        {
            if (sourceId == targetId) return true;

            var taskMap = tasks.ToDictionary(t => t.Id);
            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(sourceId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == targetId) return true;
                if (!visited.Add(current)) continue;

                if (taskMap.TryGetValue(current, out var task) && task.DependencyTaskIds != null)
                {
                    foreach (var depId in task.DependencyTaskIds)
                        queue.Enqueue(depId);
                }
            }

            return false;
        }
    }
}
