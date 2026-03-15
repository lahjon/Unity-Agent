using System.Windows;
using System.Windows.Controls;
using Spritely.Controls;
using Spritely.Managers;

namespace Spritely
{
    public partial class MainWindow
    {
        private SynthesisPanel? _synthesisPanel;
        private Expander? _synthesisExpander;

        /// <summary>
        /// Initializes the synthesis board UI and wires events from TaskExecutionManager.
        /// Called during MainWindow initialization after TaskExecutionManager is created.
        /// </summary>
        private void InitializeSynthesisBoard()
        {
            // Subscribe to synthesis events
            _taskExecutionManager.SynthesisPerspectivesSpawned += OnSynthesisPerspectivesSpawned;
            _taskExecutionManager.SynthesisPerspectiveCompleted += OnSynthesisPerspectiveCompleted;
            _taskExecutionManager.SynthesisComplete += OnSynthesisComplete;
        }

        private void EnsureSynthesisPanel()
        {
            if (_synthesisPanel != null) return;

            _synthesisPanel = new SynthesisPanel(_taskExecutionManager.OutputTabManager.OutputWriter);

            _synthesisExpander = new Expander
            {
                Header = CreateSynthesisExpanderHeader(),
                Content = _synthesisPanel,
                IsExpanded = false,
                Margin = new Thickness(0, 4, 0, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            // Insert the expander into the task output area (parent of OutputTabs)
            if (OutputTabs.Parent is DockPanel outputDock)
            {
                DockPanel.SetDock(_synthesisExpander, Dock.Bottom);
                // Insert before the last child (OutputTabs fills remaining space)
                var idx = outputDock.Children.IndexOf(OutputTabs);
                outputDock.Children.Insert(idx, _synthesisExpander);
            }
        }

        private static StackPanel CreateSynthesisExpanderHeader()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = "\uE8A1", // Group icon
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Synthesis Board",
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBody"),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            return panel;
        }

        private void OnSynthesisPerspectivesSpawned(string parentTaskId, string[] perspectiveTaskIds)
        {
            Dispatcher.BeginInvoke(() =>
            {
                EnsureSynthesisPanel();
                _synthesisPanel!.Clear();

                for (int i = 0; i < 3; i++)
                    _synthesisPanel.SetPerspectiveAgent(i, perspectiveTaskIds[i]);

                _synthesisPanel.StartPolling();

                if (_synthesisExpander != null)
                {
                    _synthesisExpander.IsExpanded = true;
                    _synthesisExpander.Visibility = Visibility.Visible;
                }
            });
        }

        private void OnSynthesisPerspectiveCompleted(string parentTaskId, int perspectiveIndex)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _synthesisPanel?.MarkComplete(perspectiveIndex);
            });
        }

        private void OnSynthesisComplete(string parentTaskId, string synthesisResult)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_synthesisPanel != null)
                {
                    _synthesisPanel.SynthesisResult = synthesisResult;
                    _synthesisPanel.StopPolling();
                    _synthesisPanel.SetSynthesisComplete();
                }
            });
        }
    }
}
