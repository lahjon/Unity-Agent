using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgenticEngine
{
    public partial class App : Application
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgenticEngine", "logs");
        private static readonly string LogFile = Path.Combine(LogDir, "crash.log");
        private static readonly string HangLogFile = Path.Combine(LogDir, "hang.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            Directory.CreateDirectory(LogDir);
            Managers.AppLogger.Initialize();
            Managers.AppLogger.Info("App", "Application starting");

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // Start UI thread hang watchdog
            StartUiWatchdog();

            // Make tooltips appear 2x quicker (default 400ms → 200ms)
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(200));

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandledException", e.Exception);

            // Show the error so the user can see what's going wrong
            var msg = e.Exception.InnerException?.Message ?? e.Exception.Message;
            var detail = e.Exception.InnerException?.GetType().Name ?? e.Exception.GetType().Name;
            ShowDarkError($"[{detail}] {msg}\n\nFull details logged to:\n{LogFile}", "AgenticEngine Error");

            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash("AppDomainUnhandledException", e.ExceptionObject as Exception);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        private static void ShowDarkError(string message, string title)
        {
            var dlg = new Window
            {
                Title = title,
                Width = 480,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true
            };

            var outerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var stack = new StackPanel { Margin = new Thickness(24) };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okBtn = new Button
            {
                Content = "OK",
                Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x52, 0x52)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Padding = new Thickness(24, 8, 24, 8),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            okBtn.Click += (_, _) => dlg.Close();

            btnPanel.Children.Add(okBtn);
            stack.Children.Add(btnPanel);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;

            try { dlg.Owner = Current.MainWindow; } catch { /* MainWindow may not be available */ }
            dlg.ShowDialog();
        }

        private static string? _lastUiAction;
        private static long _opStartTicks;
        private static string? _currentOpName;

        /// <summary>Call from UI-thread code to mark what's currently executing (for hang diagnosis).</summary>
        public static void TraceUi(string action) => _lastUiAction = action;

        private void StartUiWatchdog()
        {
            var heartbeat = new ManualResetEventSlim(false);
            var hangStart = DateTime.MinValue;

            // Track every dispatcher operation start/end with timing
            Dispatcher.Hooks.OperationStarted += (_, e) =>
            {
                _opStartTicks = Stopwatch.GetTimestamp();
                _currentOpName = _lastUiAction ?? "unknown";
            };
            Dispatcher.Hooks.OperationCompleted += (_, e) =>
            {
                var elapsed = Stopwatch.GetElapsedTime(_opStartTicks);
                if (elapsed.TotalMilliseconds > 500)
                {
                    var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SLOW OP: {_currentOpName} took {elapsed.TotalMilliseconds:F0}ms (priority={e.Operation.Priority})\n";
                    try { File.AppendAllText(HangLogFile, entry); }
                    catch { }
                }
                _currentOpName = null;
            };
            Dispatcher.Hooks.OperationAborted += (_, e) =>
            {
                _currentOpName = null;
            };

            // UI thread pings the heartbeat every 500ms
            var heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            heartbeatTimer.Tick += (_, _) =>
            {
                _lastUiAction = "idle";
                heartbeat.Set();
            };
            heartbeatTimer.Start();

            // Background thread monitors the heartbeat
            var watchdog = new Thread(() =>
            {
                while (true)
                {
                    heartbeat.Reset();
                    // Wait up to 3 seconds for the UI thread to respond
                    if (!heartbeat.Wait(3000))
                    {
                        hangStart = DateTime.Now;
                        var lastAction = _lastUiAction ?? "unknown";

                        var currentOp = _currentOpName ?? "none";
                        var opDuration = _opStartTicks > 0
                            ? $"{Stopwatch.GetElapsedTime(_opStartTicks).TotalMilliseconds:F0}ms"
                            : "n/a";

                        var entry = $"[{hangStart:yyyy-MM-dd HH:mm:ss.fff}] UI THREAD HANG DETECTED (>3s)\n" +
                                    $"  Last UI action: {lastAction}\n" +
                                    $"  Current dispatcher op: {currentOp} (running for {opDuration})\n" +
                                    $"  Process threads:\n";

                        // Dump managed thread pool info
                        ThreadPool.GetAvailableThreads(out var workerAvail, out var ioAvail);
                        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);
                        entry += $"  ThreadPool: workers={workerMax - workerAvail}/{workerMax}, IO={ioMax - ioAvail}/{ioMax}\n";
                        entry += $"  GC memory: {GC.GetTotalMemory(false) / 1024 / 1024} MB\n";

                        // List OS-level threads
                        try
                        {
                            var proc = Process.GetCurrentProcess();
                            foreach (ProcessThread pt in proc.Threads)
                            {
                                try
                                {
                                    if (pt.ThreadState == System.Diagnostics.ThreadState.Running ||
                                        pt.TotalProcessorTime.TotalMilliseconds > 1000)
                                        entry += $"  Thread {pt.Id}: state={pt.ThreadState}, cpu={pt.TotalProcessorTime.TotalSeconds:F1}s, " +
                                                 $"wait={pt.WaitReason}\n";
                                }
                                catch { }
                            }
                        }
                        catch { }

                        entry += $"{"".PadRight(80, '=')}\n";

                        try { File.AppendAllText(HangLogFile, entry); }
                        catch { }
                        Managers.AppLogger.Error("Watchdog", $"UI thread hang detected! Last action: {lastAction}. Details in hang.log");

                        // Wait for recovery, then log how long it lasted
                        heartbeat.Wait(60000);
                        var duration = DateTime.Now - hangStart;
                        var recovery = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UI RECOVERED after {duration.TotalSeconds:F1}s\n{"".PadRight(80, '-')}\n";
                        try { File.AppendAllText(HangLogFile, recovery); }
                        catch { }
                        Managers.AppLogger.Warn("Watchdog", $"UI thread recovered after {duration.TotalSeconds:F1}s");
                    }
                }
            })
            {
                IsBackground = true,
                Name = "UI-Watchdog",
                Priority = ThreadPriority.AboveNormal
            };
            watchdog.Start();
        }

        public static void LogCrash(string source, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{ex}\n{"".PadRight(80, '-')}\n";
                File.AppendAllText(LogFile, entry);
            }
            catch { /* last resort: don't crash the crash handler */ }
        }
    }
}
