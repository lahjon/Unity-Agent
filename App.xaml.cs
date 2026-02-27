using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace UnityAgent
{
    public partial class App : Application
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityAgent", "logs");
        private static readonly string LogFile = Path.Combine(LogDir, "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            Directory.CreateDirectory(LogDir);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("DispatcherUnhandledException", e.Exception);

            // Show the error so the user can see what's going wrong
            var msg = e.Exception.InnerException?.Message ?? e.Exception.Message;
            var detail = e.Exception.InnerException?.GetType().Name ?? e.Exception.GetType().Name;
            MessageBox.Show(
                $"[{detail}] {msg}\n\nFull details logged to:\n{LogFile}",
                "UnityAgent Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

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
