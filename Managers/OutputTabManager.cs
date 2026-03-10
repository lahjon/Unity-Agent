using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
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

        private readonly ConcurrentDictionary<string, TabItem> _tabs = new();
        private readonly ConcurrentDictionary<string, RichTextBox> _outputBoxes = new();
        private readonly ConcurrentDictionary<string, WrapPanel> _geminiGalleries = new();
        private readonly ConcurrentDictionary<string, TypewriterAnimationController> _animationControllers = new();
        private readonly ConcurrentDictionary<string, Button> _sendButtons = new();
        private Button? _overflowBtn;
        private readonly TabControl _outputTabs;
        private readonly Dispatcher _dispatcher;

        /// <summary>Streams output chunks to per-task log files on disk.</summary>
        private readonly StreamingOutputWriter _outputWriter = new();


        public event Action<AgentTask>? TabCloseRequested;
        public event Action<AgentTask>? TabStoreRequested;
        public event Action<AgentTask>? TabResumeRequested;
        public event Action<AgentTask>? TabExportRequested;
        public event Action<AgentTask, TextBox>? InputSent;
        public event Action<AgentTask, TextBox>? InterruptInputSent;

        public ConcurrentDictionary<string, TabItem> Tabs => _tabs;
        public ConcurrentDictionary<string, RichTextBox> OutputBoxes => _outputBoxes;

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
            _animationControllers[task.Id] = new TypewriterAnimationController(outputBox, _dispatcher);

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
            _animationControllers[task.Id] = new TypewriterAnimationController(outputBox, _dispatcher);

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
                _outputBoxes.TryRemove(task.Id, out _);
                _geminiGalleries.TryRemove(task.Id, out _);
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
            _outputTabs.Items.Remove(tab);
            _tabs.TryRemove(task.Id, out _);
            _outputBoxes.TryRemove(task.Id, out _);
            _sendButtons.TryRemove(task.Id, out _);
            _geminiGalleries.TryRemove(task.Id, out _);
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
                    if (missingTask.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning
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

            if (!_animationControllers.TryGetValue(taskId, out var controller))
            {
                // Re-create animation controller if it was removed on task completion
                // but the tab/output box still exists (e.g. follow-up messages).
                controller = new TypewriterAnimationController(box, _dispatcher);
                _animationControllers[taskId] = controller;
            }

            if (_dispatcher.CheckAccess())
                controller.Enqueue(text, baseBrush);
            else
                _dispatcher.BeginInvoke(() => controller.Enqueue(text, baseBrush));
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
                    if (missingTask.Status is not (AgentTaskStatus.Running or AgentTaskStatus.Stored or AgentTaskStatus.Planning
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

            if (!_animationControllers.TryGetValue(taskId, out var controller))
            {
                controller = new TypewriterAnimationController(box, _dispatcher);
                _animationControllers[taskId] = controller;
            }

            if (_dispatcher.CheckAccess())
                controller.Enqueue(text, foreground);
            else
                _dispatcher.BeginInvoke(() => controller.Enqueue(text, foreground));
        }

        /// <summary>Stops typewriter animation and removes pulsing dots for a task (call on task completion).</summary>
        public void StopTypewriterAnimation(string taskId)
        {
            if (!_animationControllers.TryGetValue(taskId, out var controller)) return;
            controller.Stop();
            _animationControllers.TryRemove(taskId, out _);
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

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != _outputTabs) return;
            if (_outputTabs.SelectedItem is TabItem tab && tab.Tag is string taskId)
            {
                if (_animationControllers.TryGetValue(taskId, out var controller))
                    controller.ClearScrollLock();
                if (_outputBoxes.TryGetValue(taskId, out var box))
                    box.ScrollToEnd();
            }
        }

        public void ClearFinishedTabs(ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            var finishedTaskIds = _tabs.Keys
                .Where(id =>
                {
                    var task = activeTasks.FirstOrDefault(t => t.Id == id)
                            ?? historyTasks.FirstOrDefault(t => t.Id == id);
                    return task == null || task.IsFinished;
                })
                .ToList();

            foreach (var taskId in finishedTaskIds)
            {
                if (_tabs.TryGetValue(taskId, out var tab))
                {
                    var task = activeTasks.FirstOrDefault(t => t.Id == taskId)
                            ?? historyTasks.FirstOrDefault(t => t.Id == taskId);
                    if (task != null)
                        RemoveTabImmediate(task, tab);
                }
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
