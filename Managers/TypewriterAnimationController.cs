using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages typewriter text animation, pulsing dots, and scroll-lock detection for a single output tab.
    /// </summary>
    public class TypewriterAnimationController
    {
        private readonly Queue<(string Text, Brush Foreground)> _pendingChunks = new();
        private Run? _currentRun;
        private string _currentFullText = "";
        private int _currentCharIndex;
        private Brush _currentBrush = Brushes.White;
        private DispatcherTimer? _timer;
        private Run? _pulsingDotsRun;
        private DoubleAnimation? _pulsingAnimation;
        private bool _isAnimating;
        private bool _stopped;
        private bool _userScrolledUp;

        private readonly RichTextBox _box;
        private readonly Dispatcher _dispatcher;

        public TypewriterAnimationController(RichTextBox box, Dispatcher dispatcher)
        {
            _box = box;
            _dispatcher = dispatcher;
            AttachScrollTracking();
        }

        public bool UserScrolledUp => _userScrolledUp;

        public void ClearScrollLock() => _userScrolledUp = false;

        public void Enqueue(string text, Brush foreground)
        {
            if (string.IsNullOrEmpty(text)) return;

            RemovePulsingDots();

            if (_isAnimating)
                FlushCurrent();

            _pendingChunks.Enqueue((text, foreground));

            if (!_isAnimating)
                StartNextChunk();
        }

        public void Stop()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(Stop);
                return;
            }

            _stopped = true;
            FlushCurrent();
            RemovePulsingDots();
            _timer?.Stop();
        }

        private void StartNextChunk()
        {
            if (_pendingChunks.Count == 0)
            {
                _isAnimating = false;
                ShowPulsingDots();
                return;
            }

            var (text, brush) = _pendingChunks.Dequeue();
            _currentFullText = text;
            _currentCharIndex = 0;
            _currentBrush = brush;
            _isAnimating = true;

            _currentRun = new Run("") { Foreground = brush };
            if (_box.Document.Blocks.LastBlock is Paragraph lastPara)
                lastPara.Inlines.Add(_currentRun);
            else
                _box.Document.Blocks.Add(new Paragraph(_currentRun));

            int charsPerTick = Math.Max(1, _currentFullText.Length / 40);
            var interval = TimeSpan.FromMilliseconds(12);

            _timer?.Stop();
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = interval };

            _timer.Tick += (_, _) =>
            {
                if (_currentRun == null || _currentCharIndex >= _currentFullText.Length)
                {
                    _timer.Stop();
                    if (_currentRun != null)
                        ApplyLightningTextAnimation(_currentRun, _currentBrush);
                    ScrollToEndIfNotUserScrolled();
                    StartNextChunk();
                    return;
                }

                int end = Math.Min(_currentCharIndex + charsPerTick, _currentFullText.Length);
                _currentRun.Text = _currentFullText[..end];
                _currentCharIndex = end;
                ScrollToEndIfNotUserScrolled();
            };
            _timer.Start();
        }

        private void FlushCurrent()
        {
            _timer?.Stop();
            if (_currentRun != null && _currentCharIndex < _currentFullText.Length)
            {
                _currentRun.Text = _currentFullText;
                ApplyLightningTextAnimation(_currentRun, _currentBrush);
            }

            while (_pendingChunks.Count > 0)
            {
                var (text, brush) = _pendingChunks.Dequeue();
                var run = new Run(text) { Foreground = brush };
                if (_box.Document.Blocks.LastBlock is Paragraph p)
                    p.Inlines.Add(run);
                else
                    _box.Document.Blocks.Add(new Paragraph(run));
            }

            _currentRun = null;
            _currentFullText = "";
            _currentCharIndex = 0;
            _isAnimating = false;
            ScrollToEndIfNotUserScrolled();
        }

        private void ShowPulsingDots()
        {
            if (_stopped) return;
            if (_pulsingDotsRun != null) return;

            var dotsBrush = new SolidColorBrush(((SolidColorBrush)Application.Current.FindResource("TextDisabled")).Color);
            _pulsingDotsRun = new Run("...") { Foreground = dotsBrush };

            var dotsPara = new Paragraph(_pulsingDotsRun) { Margin = new Thickness(0) };
            _box.Document.Blocks.Add(dotsPara);

            var pulseAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            _pulsingAnimation = pulseAnim;
            dotsBrush.BeginAnimation(Brush.OpacityProperty, pulseAnim);
            ScrollToEndIfNotUserScrolled();
        }

        private void RemovePulsingDots()
        {
            if (_pulsingDotsRun == null) return;

            if (_pulsingDotsRun.Foreground is SolidColorBrush scb)
                scb.BeginAnimation(Brush.OpacityProperty, null);

            if (_pulsingDotsRun.Parent is Paragraph para)
                para.Inlines.Remove(_pulsingDotsRun);

            _pulsingDotsRun = null;
            _pulsingAnimation = null;
        }

        private static void ApplyLightningTextAnimation(Run run, Brush finalBrush)
        {
            try
            {
                var finalColor = (finalBrush as SolidColorBrush)?.Color ?? Colors.LightGray;
                var lightningColor = ((SolidColorBrush)Application.Current.FindResource("WarningYellow")).Color;

                var animatedBrush = new SolidColorBrush(Color.FromArgb(0, lightningColor.R, lightningColor.G, lightningColor.B));
                run.Foreground = animatedBrush;

                var duration = TimeSpan.FromSeconds(0.45);

                var opacityAnim = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(duration),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                animatedBrush.BeginAnimation(Brush.OpacityProperty, opacityAnim);

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
                run.Foreground = finalBrush;
            }
        }

        // ── Scroll tracking ─────────────────────────────────────────────

        private void AttachScrollTracking()
        {
            _box.PreviewMouseWheel += (_, _) =>
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Input, UpdateScrollTrackingFromPosition);
            };

            _box.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, _) =>
            {
                UpdateScrollTrackingFromPosition();
            }));
        }

        private void UpdateScrollTrackingFromPosition()
        {
            var sv = FindScrollViewer(_box);
            if (sv == null) return;
            bool atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 20;
            _userScrolledUp = !atBottom;
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

        private void ScrollToEndIfNotUserScrolled()
        {
            if (!_userScrolledUp)
                _box.ScrollToEnd();
        }
    }
}
