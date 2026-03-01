using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HappyEngine.Helpers;
using HappyEngine.Managers;

namespace HappyEngine.Controls
{
    public class TaskNodeGraphPanel : Border
    {
        private Canvas _canvas = null!;
        private ScrollViewer _scrollViewer = null!;
        private Border _legendPanel = null!;
        private TextBlock _nodeCountText = null!;

        private ObservableCollection<AgentTask>? _activeTasks;
        private FileLockManager? _fileLockManager;

        // Shared position state
        private readonly Dictionary<string, Point> _nodePositions = new();
        private readonly Dictionary<string, Point> _targetPositions = new();

        // Grid state
        private bool _showGrid = true;
        private Brush _gridBrush = null!;

        // Progress panel
        private Border _progressPanel = null!;
        private StackPanel _progressStack = null!;
        private bool _showProgress;
        private ColumnDefinition _progressColumn = null!;

        // Zoom/pan transforms
        private ScaleTransform _scaleTransform = null!;
        private TranslateTransform _translateTransform = null!;
        private TransformGroup _canvasTransformGroup = null!;

        // Auto-refresh timer
        private DispatcherTimer? _refreshTimer;
        private bool _needsInitialCenter;

        // Dirty-flag: only rebuild graph when something changed
        private bool _graphDirty = true;

        // Debounce timer for collection changes (prevents redundant rebuilds during rapid additions)
        private DispatcherTimer? _collectionDebounce;

        // Decomposed subsystems
        private GraphLayoutEngine _layoutEngine = null!;
        private GraphRenderer _renderer = null!;
        private GraphInteractionHandler _interaction = null!;

        // Events for MainWindow integration
        public event Action<AgentTask>? CancelRequested;
        public event Action<AgentTask>? PauseResumeRequested;
        public event Action<AgentTask>? ShowOutputRequested;
        public event Action<AgentTask>? CopyPromptRequested;
        public event Action<AgentTask>? RevertRequested;
        public event Action<AgentTask>? ForceStartRequested;
        public event Action<AgentTask, AgentTask>? DependencyCreated;
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
            BorderBrush = BrushCache.Theme("BgElevated");
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

            var legendBtn = GraphRenderer.CreateHeaderButton("\uE946", "Toggle legend");
            legendBtn.Click += (_, _) =>
            {
                _legendPanel.Visibility = _legendPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
            };

            var resetBtn = GraphRenderer.CreateHeaderButton("\uE72C", "Reset layout");
            resetBtn.Click += (_, _) =>
            {
                _interaction.ResetLayout(_nodePositions, _targetPositions);
                RebuildGraph();
                Dispatcher.BeginInvoke(() => _interaction.CenterOnGraph(_nodePositions), DispatcherPriority.Loaded);
            };

            var fitBtn = GraphRenderer.CreateHeaderButton("\uE740", "Fit to view");
            fitBtn.Click += (_, _) => _interaction.FitToView(_nodePositions);

            var gridBtn = GraphRenderer.CreateHeaderButton("\uE80A", "Toggle grid");
            gridBtn.Click += (_, _) =>
            {
                _showGrid = !_showGrid;
                _scrollViewer.Background = _showGrid ? _gridBrush : Brushes.Transparent;
            };

            var progressBtn = GraphRenderer.CreateHeaderButton("\uE9D2", "Toggle subtask progress panel");
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

            // Initialize subsystems
            _layoutEngine = new GraphLayoutEngine();

            // Scrollable canvas area
            _scaleTransform = new ScaleTransform(1.0, 1.0);
            _translateTransform = new TranslateTransform(0, 0);
            _canvasTransformGroup = new TransformGroup();
            _canvasTransformGroup.Children.Add(_scaleTransform);
            _canvasTransformGroup.Children.Add(_translateTransform);

            var gridBrush = GraphRenderer.CreateGridBrush();
            gridBrush.Transform = _canvasTransformGroup;
            _gridBrush = gridBrush;

            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = false,
                RenderTransform = _canvasTransformGroup,
                RenderTransformOrigin = new Point(0, 0)
            };

            _scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = _gridBrush,
                Content = _canvas,
                Focusable = true
            };

            _renderer = new GraphRenderer(_canvas);
            _interaction = new GraphInteractionHandler(_canvas, _scrollViewer, _scaleTransform, _translateTransform);

            // Wire renderer events -> panel events
            _renderer.CancelRequested += t => CancelRequested?.Invoke(t);
            _renderer.PauseResumeRequested += t => PauseResumeRequested?.Invoke(t);
            _renderer.ShowOutputRequested += t => ShowOutputRequested?.Invoke(t);
            _renderer.CopyPromptRequested += t => CopyPromptRequested?.Invoke(t);
            _renderer.RevertRequested += t => RevertRequested?.Invoke(t);
            _renderer.ForceStartRequested += t => ForceStartRequested?.Invoke(t);
            _renderer.DependenciesRemoved += t => DependenciesRemoved?.Invoke(t);
            _renderer.NodeHighlightRequested += id => _interaction.ToggleNodeSelection(id);

            // Wire interaction events -> panel events
            _interaction.ShowOutputRequested += t => ShowOutputRequested?.Invoke(t);
            _interaction.DependencyCreated += (s, t) => DependencyCreated?.Invoke(s, t);
            _interaction.RequestRebuildGraph = () => RebuildGraph();
            _interaction.RequestRedrawVisuals = () => RedrawEdgesOnly();
            _interaction.RequestFocusOnNode = id => _interaction.FocusOnNode(id, _nodePositions);

            // Wire renderer's node-created callback to interaction handler
            _renderer.OnNodeCreated = (border, task) =>
            {
                _interaction.AttachNodeEvents(border, task, _nodePositions,
                    _renderer.NodeBorders);
            };

            // Attach scroll viewer events
            _interaction.AttachScrollViewerEvents();

            // Legend panel
            _legendPanel = _renderer.CreateLegendPanel();
            _legendPanel.Visibility = Visibility.Visible;
            DockPanel.SetDock(_legendPanel, Dock.Bottom);
            root.Children.Add(_legendPanel);

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
                Background = BrushCache.Theme("GraphPanelOverlay"),
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
            {
                _activeTasks.CollectionChanged -= OnTasksChanged;
                foreach (var task in _activeTasks)
                    task.PropertyChanged -= OnTaskPropertyChanged;
            }

            _activeTasks = activeTasks;
            _fileLockManager = fileLockManager;
            _activeTasks.CollectionChanged += OnTasksChanged;

            foreach (var task in _activeTasks)
                task.PropertyChanged += OnTaskPropertyChanged;

            _interaction.SetActiveTasks(_activeTasks);

            _refreshTimer?.Stop();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) =>
            {
                if (_interaction.DraggingNodeId == null && _interaction.DragSourceId == null && _graphDirty)
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
            if (e.OldItems != null)
                foreach (AgentTask task in e.OldItems)
                    task.PropertyChanged -= OnTaskPropertyChanged;

            if (e.NewItems != null)
                foreach (AgentTask task in e.NewItems)
                    task.PropertyChanged += OnTaskPropertyChanged;

            _graphDirty = true;

            // Debounce rapid additions: coalesce multiple CollectionChanged events
            // into a single rebuild after a short delay. This prevents redundant
            // rebuilds when many tasks are added in quick succession (e.g. subtask
            // spawning, workflow composition) and ensures the layout sees the full
            // batch at once, producing correct positions.
            if (_collectionDebounce == null)
            {
                _collectionDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(50)
                };
                _collectionDebounce.Tick += (_, _) =>
                {
                    _collectionDebounce.Stop();
                    if (_interaction.DraggingNodeId == null && _interaction.DragSourceId == null)
                        RebuildGraph();
                };
            }

            // Restart the timer on each change so rapid additions are batched
            _collectionDebounce.Stop();
            _collectionDebounce.Start();
        }

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Only mark dirty for properties that affect node appearance
            if (e.PropertyName is "Status" or "StatusText" or "StatusColor" or "IsRunning"
                or "IsQueued" or "IsInitQueued" or "IsFinished" or "IsPaused"
                or "Summary" or "CurrentIteration" or "Priority" or "HasPriorityBadge"
                or "ToolActivityText" or "HasToolActivity")
            {
                _graphDirty = true;
            }
        }

        public void MarkDirty() => _graphDirty = true;

        // ── Graph Rendering (orchestration) ──────────────────────────

        public void RebuildGraph()
        {
            if (_activeTasks == null || _fileLockManager == null) return;

            // Never rebuild while a node is being dragged — it recreates all borders
            // and can cause other nodes to visually shift
            if (_interaction.DraggingNodeId != null || _interaction.DragSourceId != null)
            {
                _graphDirty = true; // ensure rebuild happens after drag ends
                return;
            }

            _renderer.ClearAll();

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
                _renderer.DrawEmptyState();
                return;
            }

            // Clean up removed tasks
            var taskIds = new HashSet<string>(tasks.Select(t => t.Id));
            _layoutEngine.CleanupRemovedTasks(taskIds, _nodePositions, _targetPositions,
                _interaction.UserDraggedNodeIds);

            // Compute layout
            _layoutEngine.ComputeLayout(tasks, _nodePositions, _targetPositions,
                _interaction.UserDraggedNodeIds, _interaction.DraggingNodeId);

            // Draw edges first (behind nodes)
            _renderer.DrawAllEdges(tasks, _fileLockManager, _nodePositions,
                _interaction.HighlightedEdgeKeys);

            // Draw nodes
            foreach (var task in tasks)
                _renderer.DrawNode(task, tasks, _nodePositions,
                    _interaction.SelectedNodeId, _interaction.HighlightedNodeIds, _activeTasks);

            // Resize canvas to fit content
            _renderer.ResizeCanvas(_nodePositions);

            // Center on graph on first build only
            if (_needsInitialCenter && _nodePositions.Count > 0)
            {
                _needsInitialCenter = false;
                Dispatcher.BeginInvoke(() => _interaction.CenterOnGraph(_nodePositions), DispatcherPriority.Loaded);
            }

            // Update progress panel if visible
            _renderer.RebuildProgressPanel(_showProgress, _activeTasks, _progressStack);
        }

        private void RedrawEdgesOnly()
        {
            if (_activeTasks == null || _fileLockManager == null) return;

            _renderer.RemoveEdgeElements();
            _renderer.DrawAllEdges(_activeTasks.ToList(), _fileLockManager, _nodePositions,
                _interaction.HighlightedEdgeKeys);

            // Skip canvas resize during drag to avoid WPF layout invalidation
            // that can shift the canvas position and make other nodes appear to move
            if (_interaction.DraggingNodeId == null)
                _renderer.ResizeCanvas(_nodePositions);
        }

        // ── Progress Panel ──────────────────────────────────────────

        private void ToggleProgressPanel()
        {
            _showProgress = !_showProgress;
            if (_showProgress)
            {
                _progressPanel.Visibility = Visibility.Visible;
                _progressColumn.Width = new GridLength(240);
                _renderer.RebuildProgressPanel(_showProgress, _activeTasks, _progressStack);
            }
            else
            {
                _progressPanel.Visibility = Visibility.Collapsed;
                _progressColumn.Width = new GridLength(0);
            }
        }

        // ── Public API ──────────────────────────────────────────────

        public void FocusOnTask(string taskId)
        {
            _interaction.FocusOnTask(taskId, _nodePositions);
        }

        public void Dispose()
        {
            _interaction.Dispose();
            _refreshTimer?.Stop();
            _refreshTimer = null;
            _collectionDebounce?.Stop();
            _collectionDebounce = null;
            if (_activeTasks != null)
            {
                _activeTasks.CollectionChanged -= OnTasksChanged;
                foreach (var task in _activeTasks)
                    task.PropertyChanged -= OnTaskPropertyChanged;
            }
        }
    }
}
