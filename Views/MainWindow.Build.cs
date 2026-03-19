using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Spritely
{
    public partial class MainWindow
    {
        // ── Export Build ─────────────────────────────────────────────

        private void ExportBuild_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            Managers.AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                button.IsEnabled = false;
                ExportStatusText.Text = "Building application...";
                ExportStatusText.Foreground = (Brush)Application.Current.FindResource("TextMuted");
                ExportStatusText.Visibility = Visibility.Visible;

                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;

                System.Diagnostics.Debug.WriteLine($"Export Build - Base directory: {baseDir}");
                System.Diagnostics.Debug.WriteLine($"Export Build - Current directory: {Environment.CurrentDirectory}");

                var projectFile = FindProjectFile(baseDir);
                if (projectFile == null)
                {
                    ExportStatusText.Text = $"Could not find Spritely.csproj file.\nSearched from: {baseDir}";
                    ExportStatusText.Foreground = (Brush)Application.Current.FindResource("Danger");
                    ExportStatusText.Visibility = Visibility.Visible;
                    button.IsEnabled = true;
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"Export Build - Found project file: {projectFile}");
                var projectDir = System.IO.Path.GetDirectoryName(projectFile)!;
                var publishDir = System.IO.Path.Combine(projectDir, "publish");
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"publish \"{projectFile}\" -c Release -r win-x64 --self-contained true",
                        WorkingDirectory = projectDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var outputLines = new List<string>();
                process.OutputDataReceived += (s, args) => { if (args.Data != null) outputLines.Add(args.Data); };
                process.ErrorDataReceived += (s, args) => { if (args.Data != null) outputLines.Add(args.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode == 0)
                {
                    var expectedPath = System.IO.Path.Combine(projectDir, @"bin\Release\net9.0-windows\win-x64\publish\");
                    var fullPath = System.IO.Path.GetFullPath(expectedPath);

                    if (System.IO.Directory.Exists(fullPath))
                    {
                        ExportStatusText.Text = $"Build completed successfully! Opening folder...";
                        ExportStatusText.Foreground = (Brush)Application.Current.FindResource("Success");

                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = fullPath,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        ExportStatusText.Text = $"Build completed but output directory not found at: {fullPath}";
                        ExportStatusText.Foreground = (Brush)Application.Current.FindResource("Danger");
                    }
                }
                else
                {
                    ExportStatusText.Text = $"Build failed with exit code {process.ExitCode}. Check output for errors.";
                    ExportStatusText.Foreground = (Brush)Application.Current.FindResource("Danger");

                    if (outputLines.Count > 0)
                    {
                        ExportStatusText.Text += "\n\nBuild output:\n" + string.Join("\n", outputLines.GetRange(Math.Max(0, outputLines.Count - 10), Math.Min(10, outputLines.Count)));
                    }
                }
                }
                catch (Exception ex)
                {
                    ExportStatusText.Text = $"Export failed: {ex.Message}";
                    ExportStatusText.Foreground = (Brush)Application.Current.FindResource("Danger");
                    ExportStatusText.Visibility = Visibility.Visible;
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }, "MainWindow.ExportBuild_Click");
        }

        private string? FindProjectFile(string startDirectory)
        {
            var dir = new System.IO.DirectoryInfo(startDirectory);

            int levels = 0;
            while (dir != null && levels < 10)
            {
                var projectFile = System.IO.Path.Combine(dir.FullName, "Spritely.csproj");
                if (System.IO.File.Exists(projectFile))
                {
                    return projectFile;
                }

                if (dir.Name == "bin" && dir.Parent != null)
                {
                    var rootProjectFile = System.IO.Path.Combine(dir.Parent.FullName, "Spritely.csproj");
                    if (System.IO.File.Exists(rootProjectFile))
                    {
                        return rootProjectFile;
                    }
                }

                dir = dir.Parent;
                levels++;
            }

            string[] fallbackPaths = new[]
            {
                System.IO.Path.Combine(startDirectory, "..", "..", "..", "Spritely.csproj"),
                System.IO.Path.Combine(startDirectory, "..", "..", "..", "..", "Spritely.csproj"),
                System.IO.Path.Combine(startDirectory, "..", "Spritely.csproj"),
                System.IO.Path.Combine(Environment.CurrentDirectory, "Spritely.csproj")
            };

            foreach (var path in fallbackPaths)
            {
                try
                {
                    var fullPath = System.IO.Path.GetFullPath(path);
                    if (System.IO.File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch { /* Ignore invalid paths */ }
            }

            return null;
        }
    }
}
