using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgenticEngine
{
    public class TerminalTabManager
    {
        private readonly List<ConPtyTerminal> _terminals = new();
        private int _activeIndex = -1;
        private int _nextTabNumber = 1;

        private readonly StackPanel _tabBar;
        private readonly TextBox _output;
        private readonly TextBox _input;
        private readonly Button _sendBtn;
        private readonly Button _interruptBtn;
        private readonly System.Windows.Controls.TextBlock _rootPathDisplay;
        private readonly Dispatcher _dispatcher;
        private string _defaultWorkingDirectory;

        public TerminalTabManager(
            StackPanel tabBar,
            TextBox output,
            TextBox input,
            Button sendBtn,
            Button interruptBtn,
            System.Windows.Controls.TextBlock rootPathDisplay,
            Dispatcher dispatcher,
            string defaultWorkingDirectory)
        {
            _tabBar = tabBar;
            _output = output;
            _input = input;
            _sendBtn = sendBtn;
            _interruptBtn = interruptBtn;
            _rootPathDisplay = rootPathDisplay;
            _dispatcher = dispatcher;
            _defaultWorkingDirectory = defaultWorkingDirectory;
            UpdateInputState();
        }

        public int TerminalCount => _terminals.Count;

        public void UpdateWorkingDirectory(string directory)
        {
            _defaultWorkingDirectory = directory;

            var wd = Directory.Exists(directory)
                ? directory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Collect indices of unused terminals to replace
            var toReplace = new List<(int index, bool wasActive)>();
            for (int i = _terminals.Count - 1; i >= 0; i--)
            {
                if (!_terminals[i].HasBeenUsed)
                    toReplace.Add((i, i == _activeIndex));
            }

            foreach (var (i, wasActive) in toReplace)
            {
                try
                {
                    var terminal = _terminals[i];
                    _terminals.RemoveAt(i);
                    // Dispose on background thread to avoid Thread.Join(2000) blocking the UI
                    _ = System.Threading.Tasks.Task.Run(() => { try { terminal.Dispose(); } catch (Exception ex) { Managers.AppLogger.Debug("TerminalTab", $"Background terminal dispose failed: {ex.Message}"); } });

                    if (_activeIndex > i) _activeIndex--;
                    else if (_activeIndex == i) _activeIndex = -1;

                    ConPtyTerminal fresh;
                    try
                    {
                        fresh = new ConPtyTerminal(wd);
                    }
                    catch (Exception ex)
                    {
                        // If terminal creation fails, don't insert anything
                        Managers.AppLogger.Warn("TerminalTab", $"Failed to create replacement terminal for {wd}", ex);
                        continue;
                    }

                    WireTerminalEvents(fresh);
                    _terminals.Insert(i, fresh);

                    if (wasActive)
                    {
                        _activeIndex = i;
                        LoadActiveOutput();
                    }
                    else if (_activeIndex >= i)
                    {
                        _activeIndex++;
                    }
                }
                catch (Exception ex)
                {
                    // Don't let a single terminal failure break the whole swap
                    Managers.AppLogger.Warn("TerminalTab", "Terminal swap failed for one tab", ex);
                }
            }

            RebuildTabBar();
            UpdateRootPathDisplay();
            UpdateInputState();
        }

        // ── Add / Close / Switch ────────────────────────────────────

        public void AddTerminal(string? workingDirectory = null)
        {
            var wd = workingDirectory ?? _defaultWorkingDirectory;

            // Validate working directory exists, fall back to a safe default
            if (!Directory.Exists(wd))
                wd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ConPtyTerminal terminal;
            try
            {
                terminal = new ConPtyTerminal(wd);
            }
            catch (Exception ex)
            {
                _dispatcher.BeginInvoke(() =>
                {
                    _output.AppendText($"[Failed to create terminal: {ex.Message}]\n");
                    _output.CaretIndex = _output.Text.Length;
                    _output.ScrollToEnd();
                });
                return;
            }

            _nextTabNumber++;
            WireTerminalEvents(terminal);

            _terminals.Add(terminal);
            SwitchTo(_terminals.Count - 1);
            RebuildTabBar();
            UpdateInputState();
        }

        private void WireTerminalEvents(ConPtyTerminal terminal)
        {
            // Time-throttled rendering: at most once per 50ms to prevent UI flood
            var renderPending = 0; // 0 = idle, 1 = queued
            DispatcherTimer? renderTimer = null;

            terminal.OutputReceived += () =>
            {
                if (Interlocked.CompareExchange(ref renderPending, 1, 0) != 0) return;
                _dispatcher.BeginInvoke(() =>
                {
                    if (renderTimer == null)
                    {
                        renderTimer = new DispatcherTimer(DispatcherPriority.Background)
                        {
                            Interval = TimeSpan.FromMilliseconds(50)
                        };
                        renderTimer.Tick += (_, _) =>
                        {
                            renderTimer.Stop();
                            Interlocked.Exchange(ref renderPending, 0);
                            var idx = _terminals.IndexOf(terminal);
                            if (idx >= 0 && idx == _activeIndex)
                                RenderActiveTerminal();
                        };
                    }
                    if (!renderTimer.IsEnabled)
                        renderTimer.Start();
                });
            };

            terminal.Exited += () =>
            {
                _dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref renderPending, 0);
                    var idx = _terminals.IndexOf(terminal);
                    if (idx >= 0)
                    {
                        if (idx == _activeIndex)
                            RenderActiveTerminal();
                        RebuildTabBar();
                    }
                });
            };
        }

        private void RenderActiveTerminal()
        {
            if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
            {
                App.TraceUi("TerminalRender.GetOutputText");
                var text = _terminals[_activeIndex].GetOutputText();
                // Only update if content actually changed
                if (_output.Text != text)
                {
                    App.TraceUi("TerminalRender.SetText");
                    _output.Text = text;
                    _output.CaretIndex = text.Length;
                    _dispatcher.BeginInvoke(DispatcherPriority.Background,
                        () => _output.ScrollToEnd());
                }
            }
        }

        public void CloseTerminal(int index)
        {
            if (index < 0 || index >= _terminals.Count) return;

            var terminal = _terminals[index];
            _terminals.RemoveAt(index);
            terminal.Dispose();

            if (_terminals.Count == 0)
            {
                _activeIndex = -1;
                _output.Clear();
                RebuildTabBar();
                UpdateInputState();
                return;
            }

            if (_activeIndex > index)
                _activeIndex--;
            else if (_activeIndex >= _terminals.Count)
                _activeIndex = _terminals.Count - 1;

            LoadActiveOutput();
            RebuildTabBar();
            UpdateInputState();
            UpdateRootPathDisplay();
        }

        public void SwitchTo(int index)
        {
            if (index < 0 || index >= _terminals.Count) return;
            _activeIndex = index;
            LoadActiveOutput();
            RebuildTabBar();
            UpdateRootPathDisplay();
            _input.Focus();
        }

        private void LoadActiveOutput()
        {
            if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                RenderActiveTerminal();
            else
                _output.Clear();
        }

        private void UpdateInputState()
        {
            var hasTerminal = _terminals.Count > 0;
            _input.IsEnabled = hasTerminal;
            _sendBtn.IsEnabled = hasTerminal;
            _interruptBtn.IsEnabled = hasTerminal;

            if (!hasTerminal)
            {
                _input.Clear();
                _output.Text = "[No terminal — click + to open one]\n";
                _rootPathDisplay.Text = "";
            }
        }

        // ── Commands ────────────────────────────────────────────────

        public void SendCommand()
        {
            if (_activeIndex < 0 || _activeIndex >= _terminals.Count)
                return;

            var cmd = _input.Text;
            if (cmd == null) return;
            _input.Clear();

            var terminal = _terminals[_activeIndex];
            terminal.HistoryIndex = -1;
            terminal.HasBeenUsed = true;

            if (!string.IsNullOrWhiteSpace(cmd))
                terminal.CommandHistory.Add(cmd);

            if (terminal.HasExited)
            {
                _output.Text += "\n[Terminal has exited — close this tab and open a new one]\n";
                _output.CaretIndex = _output.Text.Length;
                _output.ScrollToEnd();
                return;
            }

            terminal.SendLine(cmd);
        }

        public void SendInterrupt()
        {
            if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                _terminals[_activeIndex].SendInterrupt();
        }

        public void HandleKeyDown(KeyEventArgs e)
        {
            if (_terminals.Count == 0) return;

            if (e.Key == Key.Tab)
            {
                if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                {
                    var terminal = _terminals[_activeIndex];
                    var text = _input.Text ?? "";
                    terminal.SendRaw(text + "\t");
                    _input.Clear();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                SendCommand();
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                {
                    var t = _terminals[_activeIndex];
                    if (t.CommandHistory.Count > 0)
                    {
                        if (t.HistoryIndex < t.CommandHistory.Count - 1)
                            t.HistoryIndex++;
                        _input.Text = t.CommandHistory[t.CommandHistory.Count - 1 - t.HistoryIndex];
                        _input.CaretIndex = _input.Text.Length;
                    }
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                {
                    var t = _terminals[_activeIndex];
                    if (t.HistoryIndex > 0)
                    {
                        t.HistoryIndex--;
                        _input.Text = t.CommandHistory[t.CommandHistory.Count - 1 - t.HistoryIndex];
                        _input.CaretIndex = _input.Text.Length;
                    }
                    else
                    {
                        t.HistoryIndex = -1;
                        _input.Clear();
                    }
                }
                e.Handled = true;
            }
        }

        // ── Root Path Display ──────────────────────────────────────

        private void UpdateRootPathDisplay()
        {
            if (_activeIndex >= 0 && _activeIndex < _terminals.Count)
                _rootPathDisplay.Text = _terminals[_activeIndex].WorkingDirectory;
            else
                _rootPathDisplay.Text = "";
        }

        // ── Tab Bar ─────────────────────────────────────────────────

        private void RebuildTabBar()
        {
            _tabBar.Children.Clear();

            for (int i = 0; i < _terminals.Count; i++)
            {
                var idx = i;
                var isActive = i == _activeIndex;
                var hasExited = _terminals[i].HasExited;

                var tabBtn = new Button
                {
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 2, 0),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0)
                };

                var panel = new StackPanel { Orientation = Orientation.Horizontal };

                var label = new TextBlock
                {
                    Text = $"  Terminal {i + 1}{(hasExited ? " (exited)" : "")}  ",
                    Foreground = new SolidColorBrush(
                        hasExited ? Color.FromRgb(0x66, 0x55, 0x55) :
                        isActive ? Color.FromRgb(0xE8, 0xE8, 0xE8) :
                        Color.FromRgb(0x88, 0x88, 0x88)),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(label);

                // Close "x" button
                var closeBtn = new Button
                {
                    Content = "\u00D7",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(2, 0, 4, 0),
                    Margin = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                // Transparent template for close button
                var closeBtnTemplate = new ControlTemplate(typeof(Button));
                var closeBorder = new FrameworkElementFactory(typeof(Border));
                closeBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                closeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                closeBorder.SetValue(Border.PaddingProperty, new Thickness(2, 0, 2, 0));
                var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
                closeContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                closeContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                closeBorder.AppendChild(closeContent);
                closeBtnTemplate.VisualTree = closeBorder;
                closeBtn.Template = closeBtnTemplate;

                var closeIdx = idx;
                closeBtn.Click += (_, _) => CloseTerminal(closeIdx);
                closeBtn.MouseEnter += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                closeBtn.MouseLeave += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                panel.Children.Add(closeBtn);

                // Tab button template
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty,
                    isActive
                        ? new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C))
                        : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4, 4, 0, 0));
                border.SetValue(Border.PaddingProperty, new Thickness(4, 3, 4, 3));
                if (isActive)
                {
                    border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56)));
                    border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
                }
                var content = new FrameworkElementFactory(typeof(ContentPresenter));
                content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                border.AppendChild(content);
                template.VisualTree = border;
                tabBtn.Template = template;

                tabBtn.Content = panel;
                tabBtn.Click += (_, _) => SwitchTo(idx);

                // Middle-click to close/terminate terminal
                tabBtn.PreviewMouseDown += (_, me) =>
                {
                    if (me.MiddleButton == MouseButtonState.Pressed)
                    {
                        CloseTerminal(idx);
                        me.Handled = true;
                    }
                };

                _tabBar.Children.Add(tabBtn);
            }

            // "+" button to add new tab
            var addBtn = new Button
            {
                Content = "+",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                Cursor = Cursors.Hand,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2, 0, 0, 0),
                BorderThickness = new Thickness(0)
            };

            var addTemplate = new ControlTemplate(typeof(Button));
            var addBorder = new FrameworkElementFactory(typeof(Border));
            addBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
            addBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4, 4, 0, 0));
            addBorder.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
            var addContent = new FrameworkElementFactory(typeof(ContentPresenter));
            addContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            addContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            addBorder.AppendChild(addContent);
            addTemplate.VisualTree = addBorder;
            addBtn.Template = addTemplate;

            addBtn.Click += (_, _) => AddTerminal();
            addBtn.MouseEnter += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0xDA, 0x77, 0x56));
            addBtn.MouseLeave += (s, _) => ((Button)s).Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            _tabBar.Children.Add(addBtn);
        }

        // ── Cleanup ─────────────────────────────────────────────────

        public void DisposeAll()
        {
            foreach (var t in _terminals)
            {
                try { t.Dispose(); } catch (Exception ex) { Managers.AppLogger.Debug("TerminalTab", $"Terminal dispose failed: {ex.Message}"); }
            }
            _terminals.Clear();
            _activeIndex = -1;
            _output.Clear();
            RebuildTabBar();
            UpdateInputState();
        }
    }
}
