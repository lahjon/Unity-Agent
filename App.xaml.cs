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

namespace HappyEngine
{
    public partial class App : Application
    {
        private static string LogDir => Managers.AppLogger.GetLogDir();
        private static string LogFile => Path.Combine(LogDir, "crash.log");
        private static string HangLogFile => Path.Combine(LogDir, "hang.log");

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
            ShowDarkError($"[{detail}] {msg}\n\nFull details logged to:\n{LogFile}", "HappyEngine Error");

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

        private static Brush GetBrush(string key, Color fallback)
        {
            try { return (Brush)Current.FindResource(key); }
            catch (Exception ex) { Managers.AppLogger.Debug("App", $"Resource '{key}' not found, using fallback: {ex.Message}"); return new SolidColorBrush(fallback); }
        }

        private static void ShowDarkError(string message, string title)
        {
            var bgSurface = GetBrush("BgSurface", Color.FromRgb(0x22, 0x22, 0x22));
            var borderMedium = GetBrush("BorderMedium", Color.FromRgb(0x3A, 0x3A, 0x3A));
            var danger = GetBrush("DangerBright", Color.FromRgb(0xE0, 0x55, 0x55));
            var textLight = GetBrush("TextLight", Color.FromRgb(0xCC, 0xCC, 0xCC));
            var textBright = GetBrush("TextBright", Color.FromRgb(0xE0, 0xE0, 0xE0));
            var accent = GetBrush("Accent", Color.FromRgb(0xDA, 0x77, 0x56));
            var accentHover = GetBrush("AccentHover", Color.FromRgb(0xE8, 0x9B, 0x7E));

            var dlg = new Window
            {
                Title = title,
                Width = 500,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 450,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true
            };

            var outerBorder = new Border
            {
                Background = bgSurface,
                BorderBrush = borderMedium,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            outerBorder.MouseLeftButtonDown += (_, me) => { if (me.ClickCount == 1) dlg.DragMove(); };

            var stack = new StackPanel { Margin = new Thickness(24) };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = danger,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = textLight,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Build a rounded button with a proper template
            var btnBorder = new Border
            {
                Background = accent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(28, 8, 28, 8),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnText = new TextBlock
            {
                Text = "OK",
                Foreground = textBright,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            btnBorder.Child = btnText;
            btnBorder.MouseEnter += (s, _) => ((Border)s).Background = accentHover;
            btnBorder.MouseLeave += (s, _) => ((Border)s).Background = accent;
            btnBorder.MouseLeftButtonUp += (_, _) => dlg.Close();
            stack.Children.Add(btnBorder);

            outerBorder.Child = stack;
            dlg.Content = outerBorder;

            try { dlg.Owner = Current.MainWindow; } catch (Exception ex) { Managers.AppLogger.Debug("App", $"Could not set dialog owner: {ex.Message}"); }
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
                    catch (Exception ex) { Managers.AppLogger.Warn("Watchdog", $"Failed to write slow-op entry to hang log: {ex.Message}"); }
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
                                catch (Exception ex) { Managers.AppLogger.Debug("Watchdog", $"Failed to read thread {pt.Id} info: {ex.Message}"); }
                            }
                        }
                        catch (Exception ex) { Managers.AppLogger.Debug("Watchdog", $"Failed to enumerate process threads: {ex.Message}"); }

                        entry += $"{"".PadRight(80, '=')}\n";

                        try { File.AppendAllText(HangLogFile, entry); }
                        catch (Exception ex) { Managers.AppLogger.Warn("Watchdog", $"Failed to write hang entry to hang log: {ex.Message}"); }
                        Managers.AppLogger.Error("Watchdog", $"UI thread hang detected! Last action: {lastAction}. Details in hang.log");

                        // Wait for recovery, then log how long it lasted
                        heartbeat.Wait(60000);
                        var duration = DateTime.Now - hangStart;
                        var recovery = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UI RECOVERED after {duration.TotalSeconds:F1}s\n{"".PadRight(80, '-')}\n";
                        try { File.AppendAllText(HangLogFile, recovery); }
                        catch (Exception ex) { Managers.AppLogger.Warn("Watchdog", $"Failed to write recovery entry to hang log: {ex.Message}"); }
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

        /// <summary>
        /// Safety-net shutdown handler. Runs after the main window has closed.
        /// Flushes any remaining background file writes and kills orphaned child processes
        /// that may have been missed by OnWindowClosing.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            Managers.AppLogger.Info("App", "OnExit: starting final cleanup");

            // Flush buffered log entries to disk before anything else
            try { Managers.AppLogger.Flush(); }
            catch (Exception ex) { LogCrash("OnExit.AppLoggerFlush", ex); }

            // Flush any file writes that were queued after OnWindowClosing
            try { Managers.SafeFileWriter.FlushAll(timeoutMs: 3000); }
            catch (Exception ex) { LogCrash("OnExit.FlushAll", ex); }

            // Kill any child processes still running under our process tree
            try { KillOrphanedChildProcesses(); }
            catch (Exception ex) { LogCrash("OnExit.KillOrphans", ex); }

            Managers.AppLogger.Info("App", "OnExit: cleanup complete");

            // Final drain: capture any log entries written after the initial Flush,
            // including the "cleanup complete" message above.
            try { Managers.AppLogger.Flush(); }
            catch { /* must not throw during exit */ }

            base.OnExit(e);

            // Force-terminate the process at the OS level. Without this, native
            // resources (ConPTY handles, pipe handles) and un-finalized objects
            // can keep the CLR alive indefinitely, leaving a zombie process in
            // Task Manager.
            Environment.Exit(0);
        }

        /// <summary>
        /// Enumerates child processes of the current process and kills any that are still alive.
        /// This catches headless launches and helper processes that have no tracked reference.
        /// </summary>
        private static void KillOrphanedChildProcesses()
        {
            var currentPid = Environment.ProcessId;
            Process[] allProcesses;
            try { allProcesses = Process.GetProcesses(); }
            catch (Exception ex) { Managers.AppLogger.Warn("App", $"Failed to enumerate processes during orphan cleanup: {ex.Message}"); return; }

            foreach (var proc in allProcesses)
            {
                try
                {
                    // Skip processes that have already exited
                    if (proc.HasExited) { proc.Dispose(); continue; }

                    // Check if this process is a direct child of ours via WMI-free parent PID query
                    if (GetParentProcessId(proc.Id) == currentPid)
                    {
                        Managers.AppLogger.Info("App", $"Killing orphaned child process {proc.Id} ({proc.ProcessName})");
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) { Managers.AppLogger.Debug("App", $"Failed to kill orphaned process: {ex.Message}"); }
                finally
                {
                    try { proc.Dispose(); } catch (Exception ex) { Managers.AppLogger.Debug("App", $"Failed to dispose process handle: {ex.Message}"); }
                }
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);

        /// <summary>Returns the parent PID of the given process, or -1 on failure.</summary>
        private static int GetParentProcessId(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var pbi = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(
                    proc.Handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _);
                if (status == 0)
                    return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
            catch (Exception ex) { Managers.AppLogger.Debug("App", $"Failed to query parent PID for process {pid}: {ex.Message}"); }
            return -1;
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
