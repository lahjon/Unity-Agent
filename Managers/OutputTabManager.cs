using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Spritely.Managers
{
    public class OutputTabManager
    {
        /// <summary>Rolling-window cap for task output kept in memory (200 KB). Full output is on disk.</summary>
        internal const int OutputCapChars = 200_000;

        private readonly Dictionary<string, TabItem> _tabs = new();
        private readonly Dictionary<string, RichTextBox> _outputBoxes = new();
        private readonly Dictionary<string, WrapPanel> _geminiGalleries = new();
        private readonly Dictionary<string, TypewriterState> _typewriterStates = new();
        private readonly Dictionary<string, Button> _sendButtons = new();
        private readonly HashSet<string> _userScrolledUp = new();
        private Button? _overflowBtn;
        private readonly TabControl _outputTabs;
        private readonly Dispatcher _dispatcher;

        /// <summary>Streams output chunks to per-task log files on disk.</summary>
        private readonly StreamingOutputWriter _outputWriter = new();

        /// <summary>Tracks per-task typewriter animation and pulsing dots state.</summary>
        private class TypewriterState
        {
            public readonly Queue<(string Text, Brush Foreground)> PendingChunks = new();
            public Run? CurrentRun;
            public string CurrentFullText = "";
            public int CurrentCharIndex;
            public Brush CurrentBrush = Brushes.White;
            public DispatcherTimer? Timer;
            public Run? PulsingDotsRun;
            public DoubleAnimation? PulsingAnimation;
            public bool IsAnimating;
            public bool Stopped;
        }

        public event Action<AgentTask>? TabCloseRequested;
        public event Action<AgentTask>? TabStoreRequested;
        public event Action<AgentTask>? TabResumeRequested;
        public event Action<AgentTask>? TabExportRequested;
        public event Action<AgentTask, TextBox>? InputSent;
        public event Action<AgentTask, TextBox>? InterruptInputSent;

        public Dictionary<string, TabItem> Tabs => _tabs;
        public Dictionary<string, RichTextBox> OutputBoxes => _outputBoxes;

        /// <summary>Provides access to the disk-backed output writer for full output retrieval.</summary>
        public StreamingOutputWriter OutputWriter => _outputWriter;

        public OutputTabManager(TabControl outputTabs, Dispatcher dispatcher)
        {
            _outputTabs = outputTabs;
            _dispatcher = dispatcher;
            _outputTabs.SelectionChanged += OnTabSelectionChanged;
        }

        public void CreateTab(AgentTask task)
        {
            // Guard against duplicate tab creation (e.g. if SubTaskSpawned fires twice)
            if (_tabs.ContainsKey(task.Id)) return;

            var outputBox = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = (Brush)Application.Current.FindResource("BgAbyss"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8)
            };
            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            outputBox.Resources.Add(typeof(Paragraph), paraStyle);
            _outputBoxes[task.Id] = outputBox;
            AttachScrollTracking(task.Id, outputBox);

            var inputBox = new TextBox
            {
                Background = (Brush)Application.Current.FindResource("BgTerminalInput"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                CaretBrush = (Brush)Application.Current.FindResource("TextBody"),
                BorderBrush = (Brush)Application.Current.FindResource("BgCard"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Enter: Send message (queues if task is busy)\nShift+Enter: Send interrupt message (injects immediately)"
            };

            var sendBtn = new Button
            {
                Content = task.IsContinuable ? "Continue" : "Send",
                Style = (Style)Application.Current.FindResource("Btn"),
                Background = (Brush)Application.Current.FindResource("Accent"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(4, 0, 0, 0)
            };
            _sendButtons[task.Id] = sendBtn;

            sendBtn.Click += (_, _) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift && !task.IsFinished)
                    InterruptInputSent?.Invoke(task, inputBox);
                else
                    InputSent?.Invoke(task, inputBox);
            };

            // Update button appearance when Shift is held (only for non-finished tasks)
            inputBox.PreviewKeyDown += (_, ke) =>
            {
                if ((ke.Key == Key.LeftShift || ke.Key == Key.RightShift) && !task.IsFinished)
                {
                    sendBtn.Content = "Interrupt";
                    sendBtn.Background = (Brush)Application.Current.FindResource("Warning");
                }
            };
            inputBox.PreviewKeyUp += (_, ke) =>
            {
                if (ke.Key == Key.LeftShift || ke.Key == Key.RightShift)
                {
                    sendBtn.Content = task.IsContinuable ? "Continue" : "Send";
                    sendBtn.Background = (Brush)Application.Current.FindResource("Accent");
                }
            };
            inputBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter)
                {
                    if (Keyboard.Modifiers == ModifierKeys.Shift && !task.IsFinished)
                    {
                        // Shift+Enter for interrupt send
                        InterruptInputSent?.Invoke(task, inputBox);
                        ke.Handled = true;
                    }
                    else if (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        // Normal send
                        InputSent?.Invoke(task, inputBox);
                        ke.Handled = true;
                    }
                }
            };

            var fullOutputBtn = new Button
            {
                Content = "\uE8A5", // ViewAll icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Style = (Style)Application.Current.FindResource("Btn"),
                Background = (Brush)Application.Current.FindResource("BgCard"),
                Foreground = (Brush)Application.Current.FindResource("TextSubtle"),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "View full output from disk"
            };
            fullOutputBtn.Click += (_, _) =>
            {
                Dialogs.FullOutputViewerDialog.Show(task.Id, task.Description, _outputWriter);
            };

            var inputPanel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(fullOutputBtn, Dock.Right);
            DockPanel.SetDock(sendBtn, Dock.Right);
            inputPanel.Children.Add(fullOutputBtn);
            inputPanel.Children.Add(sendBtn);
            inputPanel.Children.Add(inputBox);

            var content = new DockPanel();
            DockPanel.SetDock(inputPanel, Dock.Bottom);
            content.Children.Add(inputPanel);
            content.Children.Add(outputBox);

            var header = CreateTabHeader(task);

            var tabItem = new TabItem
            {
                Header = header,
                Content = content,
                Tag = task.Id
            };

            tabItem.PreviewMouseDown += (_, me) =>
            {
                if (me.MiddleButton == MouseButtonState.Pressed)
                {
                    TabCloseRequested?.Invoke(task);
                    me.Handled = true;
                }
            };

            _tabs[task.Id] = tabItem;
            _outputTabs.Items.Add(tabItem);
            _outputTabs.SelectedItem = tabItem;
            UpdateOutputTabWidths();
        }

        public void CreateGeminiTab(AgentTask task, string imageFolder)
        {
            var geminiPanel = GeminiImagePanel.Create(task, out var outputBox, out var imageGallery);
            GeminiImagePanel.SetOpenFolderHandler(geminiPanel, imageFolder);

            _outputBoxes[task.Id] = outputBox;
            _geminiGalleries[task.Id] = imageGallery;
            AttachScrollTracking(task.Id, outputBox);

            var header = CreateTabHeader(task, isGemini: true);

            var tabItem = new TabItem
            {
                Header = header,
                Content = geminiPanel,
                Tag = task.Id
            };

            tabItem.PreviewMouseDown += (_, me) =>
            {
                if (me.MiddleButton == MouseButtonState.Pressed)
                {
                    TabCloseRequested?.Invoke(task);
                    me.Handled = true;
                }
            };

            _tabs[task.Id] = tabItem;
            _outputTabs.Items.Add(tabItem);
            _outputTabs.SelectedItem = tabItem;
            UpdateOutputTabWidths();
        }

        public void AddGeminiImage(string taskId, string imagePath)
        {
            if (!_geminiGalleries.TryGetValue(taskId, out var gallery)) return;
            _dispatcher.BeginInvoke(() => GeminiImagePanel.AddImage(gallery, imagePath));
        }

        private StackPanel CreateTabHeader(AgentTask task, bool isGemini = false)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            var dotGrid = new Grid
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var glow = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(ParseHexColor(task.StatusColor)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 0 }
            };

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(ParseHexColor(task.StatusColor))
            };

            dotGrid.Children.Add(glow);
            dotGrid.Children.Add(dot);

            if (task.IsRunning)
                ApplyPulseAnimation(glow, 0.8, 10);
            else if (task.IsPlanning)
                ApplyPulseAnimation(glow, 1.0, 8, 0.4);
            else if (task.IsQueued)
                ApplyPulseAnimation(glow, 1.2, 8, 0.4);
            else if (task.IsPaused)
                ApplyPulseAnimation(glow, 2.0, 0, 0.3);

            var prefix = isGemini ? "\uE91B " : "";
            var labelText = task.ShortDescription.ReplaceLineEndings(" ");
            if (task.IsInitQueued && task.QueuePosition > 0)
                labelText = $"{labelText} - Queued (#{task.QueuePosition})";

            var label = new TextBlock
            {
                Text = (prefix + labelText),
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isGemini
                    ? (Brush)Application.Current.FindResource("Accent")
                    : (Brush)Application.Current.FindResource("TextBody")
            };

            var closeBtn = new Button
            {
                Content = "\uE8BB",
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("TextDisabled"),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };

            var bdFactory = new FrameworkElementFactory(typeof(Border));
            bdFactory.Name = "Bd";
            bdFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            bdFactory.SetValue(FrameworkElement.WidthProperty, 18.0);
            bdFactory.SetValue(FrameworkElement.HeightProperty, 18.0);
            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdFactory.AppendChild(cpFactory);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, (Brush)Application.Current.FindResource("BorderSubtle"), "Bd"));
            hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, (Brush)Application.Current.FindResource("TextLight")));

            var closeBtnTemplate = new ControlTemplate(typeof(Button)) { VisualTree = bdFactory };
            closeBtnTemplate.Triggers.Add(hoverTrigger);
            closeBtn.Template = closeBtnTemplate;
            closeBtn.Click += (_, _) => TabCloseRequested?.Invoke(task);

            var resumeItem = new MenuItem { Header = "Resume Task" };
            resumeItem.Click += (_, _) => TabResumeRequested?.Invoke(task);

            var storeItem = new MenuItem { Header = "Store Task" };
            storeItem.Click += (_, _) => TabStoreRequested?.Invoke(task);

            var exportItem = new MenuItem { Header = "Export Output" };
            exportItem.Click += (_, _) => TabExportRequested?.Invoke(task);

            var closeItem = new MenuItem { Header = "Close Tab" };
            closeItem.Click += (_, _) => TabCloseRequested?.Invoke(task);

            var ctx = new ContextMenu();
            ctx.Items.Add(resumeItem);
            ctx.Items.Add(storeItem);
            ctx.Items.Add(exportItem);
            ctx.Items.Add(new Separator());
            ctx.Items.Add(closeItem);

            ctx.Opened += (_, _) =>
            {
                resumeItem.Visibility = task.IsFinished ? Visibility.Visible : Visibility.Collapsed;
            };

            panel.Children.Add(dotGrid);
            panel.Children.Add(label);
            panel.Children.Add(closeBtn);
            panel.ContextMenu = ctx;
            panel.ToolTip = new ToolTip
            {
                Content = new TextBlock
                {
                    Text = task.Description,
                    MaxWidth = 500,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12
                },
                MaxWidth = 520
            };
            ToolTipService.SetInitialShowDelay(panel, 400);
            return panel;
        }

        public void CloseTab(AgentTask task)
        {
            if (!_tabs.TryGetValue(task.Id, out var tab))
            {
                _outputBoxes.Remove(task.Id);
                _geminiGalleries.Remove(task.Id);
                return;
            }

            // Select an adjacent tab before removing
            if (_outputTabs.SelectedItem == tab)
            {
                int idx = _outputTabs.Items.IndexOf(tab);
                if (idx + 1 < _outputTabs.Items.Count)
                    _outputTabs.SelectedIndex = idx + 1;
                else if (idx > 0)
                    _outputTabs.SelectedIndex = idx - 1;
            }

            RemoveTabImmediate(task, tab);
        }

        private void RemoveTabImmediate(AgentTask task, TabItem tab)
        {
            // Clear animations so the tab can be cleanly removed
            tab.BeginAnimation(UIElement.OpacityProperty, null);
            tab.BeginAnimation(FrameworkElement.WidthProperty, null);

            StopTypewriterAnimation(task.Id);
            _userScrolledUp.Remove(task.Id);
            _outputTabs.Items.Remove(tab);
            _tabs.Remove(task.Id);
            _outputBoxes.Remove(task.Id);
            _sendButtons.Remove(task.Id);
            _geminiGalleries.Remove(task.Id);
            UpdateOutputTabWidths();
        }

        public void UpdateTabHeader(AgentTask task)
        {
            // Stop typewriter and pulsing dots when task reaches a terminal state
            if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Recommendation)
                StopTypewriterAnimation(task.Id);

            if (_sendButtons.TryGetValue(task.Id, out var sendBtn))
            {
                sendBtn.Content = task.IsContinuable ? "Continue" : "Send";
                sendBtn.Background = (Brush)Application.Current.FindResource("Accent");
            }

            if (!_tabs.TryGetValue(task.Id, out var tab)) return;
            if (tab.Header is StackPanel sp && sp.Children[0] is Grid dotGrid
                && dotGrid.Children.Count >= 2
                && dotGrid.Children[0] is System.Windows.Shapes.Ellipse glow
                && dotGrid.Children[1] is System.Windows.Shapes.Ellipse dot)
            {
                var color = new SolidColorBrush(ParseHexColor(task.StatusColor));
                dot.Fill = color;
                glow.Fill = color;

                // Stop any existing animations
                glow.BeginAnimation(UIElement.OpacityProperty, null);
                glow.Opacity = 1.0;

                if (glow.Effect is System.Windows.Media.Effects.BlurEffect blurFx)
                {
                    blurFx.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                    blurFx.Radius = 0;
                }

                if (task.IsRunning)
                    ApplyPulseAnimation(glow, 0.8, 10);
                else if (task.IsPlanning)
                    ApplyPulseAnimation(glow, 1.0, 8, 0.4);
                else if (task.IsQueued)
                    ApplyPulseAnimation(glow, 1.2, 8, 0.4);
                else if (task.IsPaused)
                    ApplyPulseAnimation(glow, 2.0, 0, 0.3);

                if (sp.Children.Count > 1 && sp.Children[1] is TextBlock label)
                {
                    // Show queue position for InitQueued tasks
                    if (task.IsInitQueued)
                        label.Text = $"{task.ShortDescription.ReplaceLineEndings(" ")} - {task.QueueStatusText}";
                    else
                        label.Text = task.ShortDescription.ReplaceLineEndings(" ");
                }

                if (sp.Children.Count > 2 && sp.Children[2] is Button closeBtn)
                {
                    var isDone = task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled;
                    closeBtn.Content = isDone ? "\uE73E" : "\uE8BB";
                    closeBtn.Foreground = isDone
                        ? (Brush)Application.Current.FindResource("Success")
                        : (Brush)Application.Current.FindResource("TextDisabled");
                }
            }
        }

        public void AppendOutput(string taskId, string text,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (!_outputBoxes.TryGetValue(taskId, out var box))
            {
                AppLogger.Warn("FollowUp", $"[{taskId}] AppendOutput: No output box found, attempting to create tab");

                var missingTask = activeTasks.FirstOrDefault(t => t.Id == taskId)
                        ?? historyTasks.FirstOrDefault(t => t.Id == taskId);

                if (missingTask != null)
                {
                    // Only recreate tabs for tasks that are actively running.
                    // Don't recreate tabs that were intentionally closed for finished/cancelled tasks
                    // (e.g. from stale follow-up process exit callbacks firing after removal).
                    if (missingTask.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Planning
                        or AgentTaskStatus.Queued or AgentTaskStatus.InitQueued or AgentTaskStatus.Paused))
                    {
                        AppLogger.Debug("FollowUp", $"[{taskId}] Skipping tab recreation — task status is {missingTask.Status}");
                        return;
                    }

                    AppLogger.Info("FollowUp", $"[{taskId}] Creating tab for task: {missingTask.Description}");
                    CreateTab(missingTask);

                    if (!_outputBoxes.TryGetValue(taskId, out box))
                    {
                        AppLogger.Error("FollowUp", $"[{taskId}] Failed to create output box even after creating tab");
                        return;
                    }
                }
                else
                {
                    AppLogger.Error("FollowUp", $"[{taskId}] Task not found in active or history tasks");
                    return;
                }
            }

            var baseBrush = (Brush)Application.Current.FindResource("TextBody");

            // Stream to disk for full output recovery
            _outputWriter.WriteChunk(taskId, text);

            var task = activeTasks.FirstOrDefault(t => t.Id == taskId)
                    ?? historyTasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                task.OutputBuilder.Append(text);
                TrimOutputIfNeeded(task);
            }

            EnqueueTypewriterText(taskId, text, baseBrush, box);
        }

        public void AppendColoredOutput(string taskId, string text, Brush foreground,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (!_outputBoxes.TryGetValue(taskId, out var box))
            {
                AppLogger.Warn("FollowUp", $"[{taskId}] AppendColoredOutput: No output box found, attempting to create tab");

                var missingTask = activeTasks.FirstOrDefault(t => t.Id == taskId)
                        ?? historyTasks.FirstOrDefault(t => t.Id == taskId);

                if (missingTask != null)
                {
                    // Only recreate tabs for tasks that are actively running.
                    // Don't recreate tabs that were intentionally closed for finished/cancelled tasks.
                    if (missingTask.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Planning
                        or AgentTaskStatus.Queued or AgentTaskStatus.InitQueued or AgentTaskStatus.Paused))
                    {
                        AppLogger.Debug("FollowUp", $"[{taskId}] Skipping tab recreation — task status is {missingTask.Status}");
                        return;
                    }

                    CreateTab(missingTask);

                    if (!_outputBoxes.TryGetValue(taskId, out box))
                    {
                        AppLogger.Error("FollowUp", $"[{taskId}] Failed to create output box even after creating tab");
                        return;
                    }
                }
                else
                {
                    AppLogger.Error("FollowUp", $"[{taskId}] Task not found in active or history tasks");
                    return;
                }
            }

            // Stream to disk for full output recovery
            _outputWriter.WriteChunk(taskId, text);

            var task = activeTasks.FirstOrDefault(t => t.Id == taskId)
                    ?? historyTasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                task.OutputBuilder.Append(text);
                TrimOutputIfNeeded(task);
            }

            EnqueueTypewriterText(taskId, text, foreground, box);
        }

        // ── Typewriter Animation Engine ────────────────────────────────

        private void EnqueueTypewriterText(string taskId, string text, Brush foreground, RichTextBox box)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (!_typewriterStates.TryGetValue(taskId, out var state))
            {
                state = new TypewriterState();
                _typewriterStates[taskId] = state;
            }

            // Remove pulsing dots while we have text to show
            RemovePulsingDots(state, box);

            if (state.IsAnimating)
            {
                // Flush current animation immediately, then queue new text
                FlushCurrentTypewriter(taskId, state, box);
            }

            state.PendingChunks.Enqueue((text, foreground));

            if (!state.IsAnimating)
                StartNextTypewriterChunk(taskId, state, box);
        }

        private void StartNextTypewriterChunk(string taskId, TypewriterState state, RichTextBox box)
        {
            if (state.PendingChunks.Count == 0)
            {
                state.IsAnimating = false;
                // Show pulsing dots to indicate the task is still working
                ShowPulsingDots(taskId, state, box);
                return;
            }

            var (text, brush) = state.PendingChunks.Dequeue();
            state.CurrentFullText = text;
            state.CurrentCharIndex = 0;
            state.CurrentBrush = brush;
            state.IsAnimating = true;

            // Create the Run that will be progressively filled
            state.CurrentRun = new Run("") { Foreground = brush };
            if (box.Document.Blocks.LastBlock is Paragraph lastPara)
                lastPara.Inlines.Add(state.CurrentRun);
            else
                box.Document.Blocks.Add(new Paragraph(state.CurrentRun));

            // Determine speed: longer texts get faster reveal
            int charsPerTick = Math.Max(1, state.CurrentFullText.Length / 40);
            // Cap the interval so it feels snappy
            var interval = TimeSpan.FromMilliseconds(12);

            state.Timer?.Stop();
            state.Timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = interval
            };

            state.Timer.Tick += (_, _) =>
            {
                if (state.CurrentRun == null || state.CurrentCharIndex >= state.CurrentFullText.Length)
                {
                    state.Timer.Stop();
                    // Apply lightning effect on the completed run
                    if (state.CurrentRun != null)
                        ApplyLightningTextAnimation(state.CurrentRun, state.CurrentBrush);
                    ScrollToEndIfNotUserScrolled(taskId, box);
                    StartNextTypewriterChunk(taskId, state, box);
                    return;
                }

                int end = Math.Min(state.CurrentCharIndex + charsPerTick, state.CurrentFullText.Length);
                state.CurrentRun.Text = state.CurrentFullText[..end];
                state.CurrentCharIndex = end;
                ScrollToEndIfNotUserScrolled(taskId, box);
            };
            state.Timer.Start();
        }

        private void FlushCurrentTypewriter(string taskId, TypewriterState state, RichTextBox box)
        {
            state.Timer?.Stop();
            if (state.CurrentRun != null && state.CurrentCharIndex < state.CurrentFullText.Length)
            {
                state.CurrentRun.Text = state.CurrentFullText;
                ApplyLightningTextAnimation(state.CurrentRun, state.CurrentBrush);
            }

            // Also flush any remaining queued chunks immediately
            while (state.PendingChunks.Count > 0)
            {
                var (text, brush) = state.PendingChunks.Dequeue();
                var run = new Run(text) { Foreground = brush };
                if (box.Document.Blocks.LastBlock is Paragraph p)
                    p.Inlines.Add(run);
                else
                    box.Document.Blocks.Add(new Paragraph(run));
            }

            state.CurrentRun = null;
            state.CurrentFullText = "";
            state.CurrentCharIndex = 0;
            state.IsAnimating = false;
            ScrollToEndIfNotUserScrolled(taskId, box);
        }

        private void ShowPulsingDots(string taskId, TypewriterState state, RichTextBox box)
        {
            if (state.Stopped) return;
            if (state.PulsingDotsRun != null) return;

            var dotsBrush = new SolidColorBrush(((SolidColorBrush)Application.Current.FindResource("TextDisabled")).Color);
            state.PulsingDotsRun = new Run("...") { Foreground = dotsBrush };

            // Always add dots on a new line, left-aligned
            var dotsPara = new Paragraph(state.PulsingDotsRun) { Margin = new Thickness(0) };
            box.Document.Blocks.Add(dotsPara);

            // Pulse the opacity of the dots
            var pulseAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            state.PulsingAnimation = pulseAnim;
            dotsBrush.BeginAnimation(Brush.OpacityProperty, pulseAnim);
            ScrollToEndIfNotUserScrolled(taskId, box);
        }

        private static void RemovePulsingDots(TypewriterState state, RichTextBox box)
        {
            if (state.PulsingDotsRun == null) return;

            // Stop animation
            if (state.PulsingDotsRun.Foreground is SolidColorBrush scb)
                scb.BeginAnimation(Brush.OpacityProperty, null);

            // Remove from document
            if (state.PulsingDotsRun.Parent is Paragraph para)
                para.Inlines.Remove(state.PulsingDotsRun);

            state.PulsingDotsRun = null;
            state.PulsingAnimation = null;
        }

        /// <summary>Stops typewriter animation and removes pulsing dots for a task (call on task completion).</summary>
        public void StopTypewriterAnimation(string taskId)
        {
            if (!_typewriterStates.TryGetValue(taskId, out var state)) return;
            state.Stopped = true;
            if (_outputBoxes.TryGetValue(taskId, out var box))
            {
                FlushCurrentTypewriter(taskId, state, box);
                RemovePulsingDots(state, box);
            }
            state.Timer?.Stop();
            _typewriterStates.Remove(taskId);
        }

        /// <summary>
        /// Animates newly appended text with a quick lightning-like fade:
        /// starts bright and semi-transparent, then settles into the final brush.
        /// </summary>
        private static void ApplyLightningTextAnimation(Run run, Brush finalBrush)
        {
            try
            {
                // Clone the final color so we don't animate shared resource brushes
                var finalColor = (finalBrush as SolidColorBrush)?.Color ?? Colors.LightGray;
                var lightningColor = ((SolidColorBrush)Application.Current.FindResource("WarningYellow")).Color;

                var animatedBrush = new SolidColorBrush(Color.FromArgb(0, lightningColor.R, lightningColor.G, lightningColor.B));
                run.Foreground = animatedBrush;

                var duration = TimeSpan.FromSeconds(0.45);

                // Opacity fade-in via brush opacity
                var opacityAnim = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(duration),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                animatedBrush.BeginAnimation(Brush.OpacityProperty, opacityAnim);

                // Color flash from bright yellow back to the final text color
                var colorAnim = new ColorAnimation
                {
                    From = lightningColor,
                    To = finalColor,
                    Duration = new Duration(duration),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
            }
            catch
            {
                // If anything goes wrong with animation, fall back to normal rendering
                run.Foreground = finalBrush;
            }
        }

        /// <summary>Keeps the last <see cref="OutputCapChars"/> characters when a task's buffer grows too large.</summary>
        internal static void TrimOutputIfNeeded(AgentTask task)
        {
            // Feature mode tasks are not trimmed by general cap (they have their own iteration-based trimming)
            if (task.IsFeatureMode)
                return;

            if (task.OutputBuilder.Length <= OutputCapChars)
                return;

            var trimmed = task.OutputBuilder.ToString(
                task.OutputBuilder.Length - OutputCapChars, OutputCapChars);
            task.OutputBuilder.Clear();
            task.OutputBuilder.Append(trimmed);
        }

        private static void ApplyPulseAnimation(System.Windows.Shapes.Ellipse glow, double durationSec, double blurTo, double opacityFrom = 0.3)
        {
            var duration = TimeSpan.FromSeconds(durationSec);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

            if (blurTo > 0 && glow.Effect is System.Windows.Media.Effects.BlurEffect blur)
            {
                var blurAnim = new DoubleAnimation(0, blurTo, duration)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease
                };
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnim);
            }

            var opacityAnim = new DoubleAnimation(opacityFrom, 1.0, duration)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            glow.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private static Color ParseHexColor(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return c;
            }
            catch
            {
                return Color.FromRgb(0x55, 0x55, 0x55);
            }
        }

        private void AttachScrollTracking(string taskId, RichTextBox box)
        {
            // Track user scrolling via mouse wheel — avoids false resets from content-change scroll events
            box.PreviewMouseWheel += (_, _) =>
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                    UpdateScrollTrackingFromPosition(taskId, box));
            };

            // Track scrollbar thumb/button interactions
            box.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, _) =>
            {
                UpdateScrollTrackingFromPosition(taskId, box);
            }));
        }

        private void UpdateScrollTrackingFromPosition(string taskId, RichTextBox box)
        {
            var sv = FindScrollViewer(box);
            if (sv == null) return;
            bool atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 20;
            if (atBottom)
                _userScrolledUp.Remove(taskId);
            else
                _userScrolledUp.Add(taskId);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer sv) return sv;
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void ScrollToEndIfNotUserScrolled(string taskId, RichTextBox box)
        {
            if (!_userScrolledUp.Contains(taskId))
                box.ScrollToEnd();
        }

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != _outputTabs) return;
            if (_outputTabs.SelectedItem is TabItem tab && tab.Tag is string taskId)
            {
                _userScrolledUp.Remove(taskId);
                if (_outputBoxes.TryGetValue(taskId, out var box))
                    box.ScrollToEnd();
            }
        }

        public bool HasTab(string taskId) => _tabs.ContainsKey(taskId);

        public TabItem? GetTab(string taskId) => _tabs.GetValueOrDefault(taskId);

        public RichTextBox? GetOutputBox(string taskId) => _outputBoxes.GetValueOrDefault(taskId);

        public void EnsureOverflowButton()
        {
            if (_overflowBtn != null) return;
            _outputTabs.ApplyTemplate();
            _overflowBtn = _outputTabs.Template.FindName("PART_OverflowButton", _outputTabs) as Button;
            if (_overflowBtn != null)
                _overflowBtn.Click += TabOverflow_Click;
        }

        public void UpdateOutputTabWidths()
        {
            EnsureOverflowButton();

            const double maxWidth = 200.0;
            const double minWidth = 60.0;
            const double headerOverhead = 55.0;
            const double overflowBtnWidth = 28.0;

            int count = _outputTabs.Items.Count;
            if (count == 0)
            {
                if (_overflowBtn != null) _overflowBtn.Visibility = Visibility.Collapsed;
                return;
            }

            double available = _outputTabs.ActualWidth;
            if (available <= 0) available = 500;

            bool overflow = count * minWidth > available;
            if (_overflowBtn != null)
                _overflowBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            double tabAvailable = overflow ? available - overflowBtnWidth : available;
            double tabWidth = Math.Max(minWidth, Math.Min(maxWidth, tabAvailable / count));
            double labelMax = Math.Max(20, tabWidth - headerOverhead);

            foreach (var item in _outputTabs.Items)
            {
                if (item is TabItem tab)
                {
                    tab.Width = tabWidth;
                    if (tab.Header is StackPanel sp)
                    {
                        foreach (var child in sp.Children)
                        {
                            if (child is TextBlock tb)
                            {
                                tb.MaxWidth = labelMax;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void TabOverflow_Click(object sender, RoutedEventArgs e)
        {
            var popup = new Popup
            {
                PlacementTarget = _overflowBtn,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgPopup"),
                BorderBrush = (Brush)Application.Current.FindResource("BorderMedium"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 150,
                MaxHeight = 300
            };

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var stack = new StackPanel();

            foreach (var item in _outputTabs.Items)
            {
                if (item is TabItem tab)
                {
                    string text = "Tab";
                    if (tab.Header is StackPanel sp)
                    {
                        foreach (var child in sp.Children)
                        {
                            if (child is TextBlock tb)
                            {
                                text = tb.Text;
                                break;
                            }
                        }
                    }

                    bool isSelected = tab == _outputTabs.SelectedItem;
                    var itemBorder = new Border
                    {
                        Background = isSelected
                            ? (Brush)Application.Current.FindResource("BgHover")
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
                            ? (Brush)Application.Current.FindResource("Accent")
                            : (Brush)Application.Current.FindResource("TextBody"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 12,
                        FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.NoWrap,
                        MaxWidth = 200
                    };

                    itemBorder.Child = textBlock;

                    var capturedTab = tab;
                    var capturedBorder = itemBorder;
                    var capturedSelected = isSelected;

                    itemBorder.MouseEnter += (_, _) =>
                    {
                        if (!capturedSelected)
                            capturedBorder.Background = (Brush)Application.Current.FindResource("BgCardHover");
                    };
                    itemBorder.MouseLeave += (_, _) =>
                    {
                        if (!capturedSelected)
                            capturedBorder.Background = Brushes.Transparent;
                    };
                    itemBorder.MouseLeftButtonDown += (_, _) =>
                    {
                        _outputTabs.SelectedItem = capturedTab;
                        capturedTab.BringIntoView();
                        popup.IsOpen = false;
                    };

                    stack.Children.Add(itemBorder);
                }
            }

            scroll.Content = stack;
            border.Child = scroll;
            popup.Child = border;
            popup.IsOpen = true;
        }
    }
}
