using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Spritely.Managers;

namespace Spritely.Controls
{
    /// <summary>
    /// Displays 3 parallel perspective agent outputs side-by-side in real-time.
    /// Reads streaming output from <see cref="StreamingOutputWriter"/> via polling.
    /// </summary>
    public class SynthesisPanel : Border
    {
        private static readonly string[] PerspectiveNames = { "Architecture", "Testing", "Edge Cases" };
        private static readonly string[] PerspectiveColors = { "#64B5F6", "#81C784", "#FFB74D" };

        private readonly RichTextBox[] _outputBoxes = new RichTextBox[3];
        private readonly TextBlock[] _statusLabels = new TextBlock[3];
        private readonly string?[] _taskIds = new string?[3];
        private readonly long[] _lastReadBytes = new long[3];
        private readonly StreamingOutputWriter _outputWriter;
        private readonly DispatcherTimer _pollTimer;
        private readonly TextBlock _synthesisStatus;

        public string? SynthesisResult { get; set; }

        public SynthesisPanel(StreamingOutputWriter outputWriter)
        {
            _outputWriter = outputWriter;

            Background = (Brush)Application.Current.FindResource("BgSurface");
            BorderBrush = (Brush)Application.Current.FindResource("BgElevated");
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(8);
            Margin = new Thickness(0, 4, 0, 0);

            var root = new DockPanel();

            // Header
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var title = new TextBlock
            {
                Text = "SYNTHESIS BOARD",
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontWeight = FontWeights.Bold,
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            _synthesisStatus = new TextBlock
            {
                Text = "Waiting for agents...",
                Foreground = (Brush)Application.Current.FindResource("TextSubtle"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(_synthesisStatus, Dock.Right);
            header.Children.Add(_synthesisStatus);
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // 3-column grid
            var grid = new Grid();
            for (int i = 0; i < 3; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (i < 2)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            }

            for (int i = 0; i < 3; i++)
            {
                var column = CreatePerspectiveColumn(i);
                Grid.SetColumn(column, i * 2);
                grid.Children.Add(column);
            }

            root.Children.Add(grid);
            Child = root;

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _pollTimer.Tick += PollOutputs;
        }

        private Border CreatePerspectiveColumn(int index)
        {
            var border = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgCard"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6),
                Margin = new Thickness(index > 0 ? 4 : 0, 0, index < 2 ? 4 : 0, 0)
            };

            var panel = new DockPanel();

            // Header with perspective name and status
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(PerspectiveColors[index]));
            color.Freeze();

            var nameLabel = new TextBlock
            {
                Text = PerspectiveNames[index],
                Foreground = color,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            };

            _statusLabels[index] = new TextBlock
            {
                Text = "Pending",
                Foreground = (Brush)Application.Current.FindResource("TextDisabled"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_statusLabels[index], Dock.Right);
            headerPanel.Children.Add(_statusLabels[index]);
            headerPanel.Children.Add(nameLabel);
            DockPanel.SetDock(headerPanel, Dock.Top);
            panel.Children.Add(headerPanel);

            // Output box
            _outputBoxes[index] = new RichTextBox
            {
                IsReadOnly = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = (Brush)Application.Current.FindResource("BgAbyss"),
                Foreground = (Brush)Application.Current.FindResource("TextBody"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                MinHeight = 120,
                MaxHeight = 300
            };
            var paraStyle = new Style(typeof(Paragraph));
            paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            _outputBoxes[index].Resources.Add(typeof(Paragraph), paraStyle);

            panel.Children.Add(_outputBoxes[index]);
            border.Child = panel;
            return border;
        }

        /// <summary>
        /// Assigns a perspective agent task ID to a column. Index: 0=Architecture, 1=Testing, 2=EdgeCases.
        /// </summary>
        public void SetPerspectiveAgent(int index, string taskId)
        {
            if (index < 0 || index >= 3) return;
            _taskIds[index] = taskId;
            _lastReadBytes[index] = 0;
            _statusLabels[index].Text = "Running";
            _statusLabels[index].Foreground = (Brush)Application.Current.FindResource("TextBody");
        }

        public void StartPolling()
        {
            _pollTimer.Start();
            _synthesisStatus.Text = "Agents running...";
        }

        public void StopPolling()
        {
            _pollTimer.Stop();
        }

        public void MarkComplete(int index)
        {
            if (index < 0 || index >= 3) return;
            _statusLabels[index].Text = "Complete";
            var greenBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            greenBrush.Freeze();
            _statusLabels[index].Foreground = greenBrush;

            // Check if all are complete
            bool allComplete = true;
            for (int i = 0; i < 3; i++)
            {
                if (_statusLabels[i].Text != "Complete")
                {
                    allComplete = false;
                    break;
                }
            }
            if (allComplete)
            {
                _synthesisStatus.Text = SynthesisResult != null ? "Synthesis complete" : "Synthesizing...";
            }
        }

        public void SetSynthesisComplete()
        {
            _synthesisStatus.Text = "Synthesis complete";
            _synthesisStatus.Foreground = (Brush)Application.Current.FindResource("Success");
        }

        private void PollOutputs(object? sender, EventArgs e)
        {
            for (int i = 0; i < 3; i++)
            {
                if (_taskIds[i] == null) continue;

                var currentBytes = _outputWriter.GetBytesWritten(_taskIds[i]!);
                if (currentBytes <= _lastReadBytes[i]) continue;

                // Read full output and get the new portion
                var fullOutput = _outputWriter.ReadAll(_taskIds[i]!);
                if (string.IsNullOrEmpty(fullOutput)) continue;

                // Approximate: convert byte position to character position
                var startChar = (int)Math.Min(_lastReadBytes[i], fullOutput.Length);
                if (startChar < fullOutput.Length)
                {
                    var newText = fullOutput[startChar..];
                    AppendToBox(i, newText);
                }
                _lastReadBytes[i] = currentBytes;
            }
        }

        private void AppendToBox(int index, string text)
        {
            var box = _outputBoxes[index];
            var para = box.Document.Blocks.LastBlock as Paragraph ?? new Paragraph();
            if (box.Document.Blocks.Count == 0 || box.Document.Blocks.LastBlock != para)
            {
                para = new Paragraph { Margin = new Thickness(0) };
                box.Document.Blocks.Add(para);
            }
            para.Inlines.Add(new Run(text) { Foreground = box.Foreground });
            box.ScrollToEnd();
        }

        public void Clear()
        {
            StopPolling();
            for (int i = 0; i < 3; i++)
            {
                _taskIds[i] = null;
                _lastReadBytes[i] = 0;
                _outputBoxes[i].Document.Blocks.Clear();
                _statusLabels[i].Text = "Pending";
                _statusLabels[i].Foreground = (Brush)Application.Current.FindResource("TextDisabled");
            }
            _synthesisStatus.Text = "Waiting for agents...";
            _synthesisStatus.Foreground = (Brush)Application.Current.FindResource("TextSubtle");
            SynthesisResult = null;
        }
    }
}
