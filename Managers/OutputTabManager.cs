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

namespace HappyEngine.Managers
{
    public class OutputTabManager
    {
        /// <summary>Rolling-window cap for non-overnight task output (500 KB of text).
        /// Overnight tasks already trim at iteration boundaries via OvernightOutputCapChars.</summary>
        internal const int OutputCapChars = 500_000;

        private readonly Dictionary<string, TabItem> _tabs = new();
        private readonly Dictionary<string, RichTextBox> _outputBoxes = new();
        private readonly Dictionary<string, WrapPanel> _geminiGalleries = new();
        private Button? _overflowBtn;
        private readonly TabControl _outputTabs;
        private readonly Dispatcher _dispatcher;

        public event Action<AgentTask>? TabCloseRequested;
        public event Action<AgentTask>? TabStoreRequested;
        public event Action<AgentTask>? TabResumeRequested;
        public event Action<AgentTask, TextBox>? InputSent;

        public Dictionary<string, TabItem> Tabs => _tabs;
        public Dictionary<string, RichTextBox> OutputBoxes => _outputBoxes;

        public OutputTabManager(TabControl outputTabs, Dispatcher dispatcher)
        {
            _outputTabs = outputTabs;
            _dispatcher = dispatcher;
        }

        public void CreateTab(AgentTask task)
        {
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

            var inputBox = new TextBox
            {
                Background = (Brush)Application.Current.FindResource("BgTerminalInput"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                CaretBrush = (Brush)Application.Current.FindResource("TextBody"),
                BorderBrush = (Brush)Application.Current.FindResource("BgCard"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var sendBtn = new Button
            {
                Content = "Send",
                Style = (Style)Application.Current.FindResource("Btn"),
                Background = (Brush)Application.Current.FindResource("Accent"),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(4, 0, 0, 0)
            };

            sendBtn.Click += (_, _) => InputSent?.Invoke(task, inputBox);
            inputBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Control))
                {
                    InputSent?.Invoke(task, inputBox);
                    ke.Handled = true;
                }
            };

            var inputPanel = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(sendBtn, Dock.Right);
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
            var label = new TextBlock
            {
                Text = (prefix + task.ShortDescription).ReplaceLineEndings(" "),
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

            var closeItem = new MenuItem { Header = "Close Tab" };
            closeItem.Click += (_, _) => TabCloseRequested?.Invoke(task);

            var ctx = new ContextMenu();
            ctx.Items.Add(resumeItem);
            ctx.Items.Add(storeItem);
            ctx.Items.Add(closeItem);

            ctx.Opened += (_, _) =>
            {
                resumeItem.Visibility = task.IsFinished ? Visibility.Visible : Visibility.Collapsed;
            };

            panel.Children.Add(dotGrid);
            panel.Children.Add(label);
            panel.Children.Add(closeBtn);
            panel.ContextMenu = ctx;
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

            // If tab isn't loaded or has no width yet, skip animation
            if (tab.ActualWidth <= 0)
            {
                RemoveTabImmediate(task, tab);
                return;
            }

            // Prevent interaction during animation
            tab.IsHitTestVisible = false;

            // Select an adjacent tab before animating
            if (_outputTabs.SelectedItem == tab)
            {
                int idx = _outputTabs.Items.IndexOf(tab);
                if (idx + 1 < _outputTabs.Items.Count)
                    _outputTabs.SelectedIndex = idx + 1;
                else if (idx > 0)
                    _outputTabs.SelectedIndex = idx - 1;
            }

            double originalWidth = tab.ActualWidth;

            // Phase 1: Fade out (200ms)
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            // Phase 2: Shrink width to 0 (180ms, starts 80ms after fade begins)
            var shrink = new DoubleAnimation(originalWidth, 0, TimeSpan.FromMilliseconds(180))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            shrink.Completed += (_, _) => RemoveTabImmediate(task, tab);

            tab.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            tab.BeginAnimation(FrameworkElement.WidthProperty, shrink);
        }

        private void RemoveTabImmediate(AgentTask task, TabItem tab)
        {
            // Clear animations so the tab can be cleanly removed
            tab.BeginAnimation(UIElement.OpacityProperty, null);
            tab.BeginAnimation(FrameworkElement.WidthProperty, null);

            _outputTabs.Items.Remove(tab);
            _tabs.Remove(task.Id);
            _outputBoxes.Remove(task.Id);
            _geminiGalleries.Remove(task.Id);
            UpdateOutputTabWidths();
        }

        public void UpdateTabHeader(AgentTask task)
        {
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
                    label.Text = task.ShortDescription.ReplaceLineEndings(" ");

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
            if (!_outputBoxes.TryGetValue(taskId, out var box)) return;
            var run = new Run(text) { Foreground = (Brush)Application.Current.FindResource("TextBody") };
            if (box.Document.Blocks.LastBlock is Paragraph lastPara)
                lastPara.Inlines.Add(run);
            else
                box.Document.Blocks.Add(new Paragraph(run));
            box.ScrollToEnd();

            var task = activeTasks.FirstOrDefault(t => t.Id == taskId)
                    ?? historyTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            task.OutputBuilder.Append(text);
            TrimOutputIfNeeded(task);
        }

        public void AppendColoredOutput(string taskId, string text, Brush foreground,
            ObservableCollection<AgentTask> activeTasks, ObservableCollection<AgentTask> historyTasks)
        {
            if (!_outputBoxes.TryGetValue(taskId, out var box)) return;
            var run = new Run(text) { Foreground = foreground };
            if (box.Document.Blocks.LastBlock is Paragraph lastPara)
                lastPara.Inlines.Add(run);
            else
                box.Document.Blocks.Add(new Paragraph(run));
            box.ScrollToEnd();

            var task = activeTasks.FirstOrDefault(t => t.Id == taskId)
                    ?? historyTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            task.OutputBuilder.Append(text);
            TrimOutputIfNeeded(task);
        }

        /// <summary>Keeps the last <see cref="OutputCapChars"/> characters when a non-overnight task's buffer grows too large.</summary>
        internal static void TrimOutputIfNeeded(AgentTask task)
        {
            if (task.IsOvernight || task.OutputBuilder.Length <= OutputCapChars)
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

        public bool HasTab(string taskId) => _tabs.ContainsKey(taskId);

        public TabItem? GetTab(string taskId) => _tabs.GetValueOrDefault(taskId);

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
