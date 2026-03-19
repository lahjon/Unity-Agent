using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Spritely.Dialogs;
using Spritely.Helpers;
using Spritely.Models;

namespace Spritely.Managers
{
    /// <summary>
    /// Builds the project list UI cards in the project panel.
    /// </summary>
    public class ProjectListRenderer
    {
        private readonly IProjectDataProvider _data;
        private readonly ProjectColorManager _colors;
        private readonly McpConfigManager _mcp;
        private readonly ProjectSwitchManager _switch;
        private readonly Func<string, IEnumerable<AgentTask>, ProjectActivityStats> _getStats;

        private ObservableCollection<AgentTask>? _activeTasks;
        private ObservableCollection<AgentTask>? _historyTasks;

        public event Action<string, string>? ProjectRenamed;

        public ProjectListRenderer(
            IProjectDataProvider data,
            ProjectColorManager colors,
            McpConfigManager mcp,
            ProjectSwitchManager @switch,
            Func<string, IEnumerable<AgentTask>, ProjectActivityStats> getStats)
        {
            _data = data;
            _colors = colors;
            _mcp = mcp;
            _switch = @switch;
            _getStats = getStats;
        }

        public void SetTaskCollections(
            ObservableCollection<AgentTask> activeTasks,
            ObservableCollection<AgentTask> historyTasks)
        {
            _activeTasks = activeTasks;
            _historyTasks = historyTasks;
        }

        public void RefreshProjectList(
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            _switch.StoreCallbacks(updateTerminalWorkingDirectory, saveSettings, syncSettings);

            if (_data.View.ProjectListPanel == null) return;

            _data.View.ViewDispatcher.Invoke(() =>
            {
                _data.View.ProjectListPanel.UpdateLayout();
            });

            _data.View.ProjectListPanel.Children.Clear();

            foreach (var proj in _data.SavedProjects)
            {
                var card = BuildProjectCard(proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
                _data.View.ProjectListPanel.Children.Add(card);
            }
        }

        private Border BuildProjectCard(
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var isActive = proj.Path == _data.ProjectPath;

            var card = new Border
            {
                Background = (Brush)Application.Current.FindResource("BgElevated"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(isActive ? 2 : 1),
                BorderBrush = isActive
                    ? (Brush)Application.Current.FindResource("Accent")
                    : (Brush)Application.Current.FindResource("BorderMedium"),
                Cursor = Cursors.Hand,
                MaxHeight = 200
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoPanel = BuildInfoPanel(proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
            Grid.SetRow(infoPanel, 0);
            Grid.SetColumn(infoPanel, 0);
            grid.Children.Add(infoPanel);

            if (proj.IsGame)
            {
                var mcpButtonWrapper = BuildMcpButton(proj);
                Grid.SetRow(mcpButtonWrapper, 1);
                Grid.SetColumn(mcpButtonWrapper, 0);
                Grid.SetColumnSpan(mcpButtonWrapper, 2);
                grid.Children.Add(mcpButtonWrapper);
            }

            var btnPanel = BuildButtonPanel(proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
            Grid.SetRow(btnPanel, 0);
            Grid.SetColumn(btnPanel, 1);
            grid.Children.Add(btnPanel);

            card.Child = grid;

            var projPath = proj.Path;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is TextBox) return;
                _switch.HandleCardSwap(projPath);
            };

            return card;
        }

        private StackPanel BuildInfoPanel(
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var infoPanel = new StackPanel();

            var nameRow = BuildNameRow(proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
            infoPanel.Children.Add(nameRow);

            infoPanel.Children.Add(new TextBlock
            {
                Text = proj.Path,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = proj.Path
            });

            if (proj.IsInitializing)
            {
                var initRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                initRow.Children.Add(new TextBlock
                {
                    Text = "Initializing descriptions...",
                    Foreground = (Brush)Application.Current.FindResource("WarningAmber"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    FontFamily = new FontFamily("Segoe UI")
                });
                infoPanel.Children.Add(initRow);
            }
            else if (!string.IsNullOrWhiteSpace(proj.ShortDescription))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = proj.ShortDescription,
                    Foreground = (Brush)Application.Current.FindResource("TextTabHeader"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0, 4, 0, 0),
                    ToolTip = proj.ShortDescription
                });
            }

            AddInitIndicator(infoPanel, proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
            AddStatsPanel(infoPanel, proj);
            AddMcpStatusPanel(infoPanel, proj);

            return infoPanel;
        }

        private StackPanel BuildNameRow(
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };

            var typeIcon = new TextBlock
            {
                Text = proj.IsGame ? "\uE7FC" : "\uE770",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)Application.Current.FindResource("TextTabHeader"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = proj.IsGame ? "Game" : "App"
            };
            nameRow.Children.Add(typeIcon);

            var nameBrush = !string.IsNullOrEmpty(proj.Color)
                ? BrushCache.Get(proj.Color)
                : BrushCache.Theme("TextPrimary");
            var nameBlock = new TextBlock
            {
                Text = proj.DisplayName,
                Foreground = nameBrush,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.IBeam,
                ToolTip = "Click to rename"
            };
            nameBlock.MouseEnter += (_, _) => nameBlock.TextDecorations = TextDecorations.Underline;
            nameBlock.MouseLeave += (_, _) => nameBlock.TextDecorations = null;

            var editIcon = new TextBlock
            {
                Text = "\u270E",
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                Cursor = Cursors.IBeam,
                ToolTip = "Rename project"
            };

            var projEntry = proj;
            void StartRename()
            {
                var tb = nameBlock;
                var parent = tb.Parent as StackPanel;
                if (parent == null) return;
                var idx = parent.Children.IndexOf(tb);
                var editBox = new TextBox
                {
                    Text = projEntry.Name ?? projEntry.FolderName,
                    Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                    Background = (Brush)Application.Current.FindResource("BgDeep"),
                    BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                    BorderThickness = new Thickness(1),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(2, 0, 2, 0),
                    MinWidth = 80,
                    CaretBrush = (Brush)Application.Current.FindResource("TextPrimary"),
                    SelectionBrush = (Brush)Application.Current.FindResource("Accent")
                };

                parent.Children.RemoveAt(idx);
                parent.Children.Insert(idx, editBox);

                var committed = false;
                void CommitRename()
                {
                    if (committed) return;
                    committed = true;
                    var newName = editBox.Text?.Trim();
                    if (!string.IsNullOrEmpty(newName))
                    {
                        projEntry.Name = newName;
                        _data.SaveProjects();
                        _data.RefreshProjectCombo();
                        ProjectRenamed?.Invoke(projEntry.Path, newName);
                    }
                    RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                }

                editBox.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter) CommitRename();
                    else if (ke.Key == Key.Escape) RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                };

                _data.View.ViewDispatcher.BeginInvoke(new Action(() =>
                {
                    editBox.LostFocus += (_, _) => CommitRename();
                }), DispatcherPriority.Input);

                editBox.SelectAll();
                editBox.Focus();
            }

            nameBlock.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
            editIcon.MouseLeftButtonDown += (_, ev) => { ev.Handled = true; StartRename(); };
            nameRow.Children.Add(nameBlock);
            nameRow.Children.Add(editIcon);

            return nameRow;
        }

        private void AddInitIndicator(
            StackPanel infoPanel,
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var initBrush = proj.IsInitialized
                ? BrushCache.Theme("SuccessGreen")
                : BrushCache.Theme("WarningAmber");
            var initIndicator = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = proj.IsInitialized ? "Feature registry initialized" : "Feature registry not initialized"
            };
            initIndicator.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = initBrush,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            initIndicator.Children.Add(new TextBlock
            {
                Text = proj.IsInitialized ? "Initialized" : "Not Initialized",
                Foreground = initBrush,
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = proj.IsInitialized ? new Thickness(0) : new Thickness(0, 0, 6, 0)
            });

            if (!proj.IsInitialized)
            {
                var inlineInitBtn = new Button
                {
                    Content = "Init",
                    Style = (Style)Application.Current.FindResource("SmallBtn"),
                    Background = (Brush)Application.Current.FindResource("Accent"),
                    FontSize = 10,
                    Padding = new Thickness(8, 1, 8, 1),
                    Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Initialize Feature Registry"
                };
                var inlineInitEntry = proj;
                inlineInitBtn.Click += async (s, ev) =>
                {
                    ev.Handled = true;
                    if (s is not Button btn) return;
                    btn.IsEnabled = false;
                    btn.Content = "Analyzing...";

                    var pulseAnim = new DoubleAnimation(1.0, 0.4, TimeSpan.FromSeconds(0.8))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase()
                    };
                    btn.BeginAnimation(UIElement.OpacityProperty, pulseAnim);

                    try
                    {
                        var registryManager = new FeatureRegistryManager();
                        var initializer = new FeatureInitializer(registryManager);
                        initializer.ProgressChanged += msg =>
                        {
                            Application.Current?.Dispatcher?.BeginInvoke(() =>
                                btn.Content = msg.Length > 30 ? msg[..30] + "…" : msg);
                            AppLogger.Info("FeatureInit", $"[{inlineInitEntry.DisplayName}] {msg}");
                        };
                        var result = await initializer.InitializeAsync(inlineInitEntry.Path);
                        btn.BeginAnimation(UIElement.OpacityProperty, null);
                        btn.Opacity = 1.0;
                        if (result != null)
                        {
                            AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {inlineInitEntry.DisplayName}");
                            RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                        }
                        else
                        {
                            btn.Content = "Failed";
                            btn.IsEnabled = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        btn.BeginAnimation(UIElement.OpacityProperty, null);
                        btn.Opacity = 1.0;
                        AppLogger.Error("FeatureInit", $"Feature initialization failed for {inlineInitEntry.DisplayName}", ex);
                        btn.Content = "Failed – Retry";
                        btn.IsEnabled = true;
                    }
                };
                initIndicator.Children.Add(inlineInitBtn);
            }

            infoPanel.Children.Add(initIndicator);
        }

        private void AddStatsPanel(StackPanel infoPanel, ProjectEntry proj)
        {
            if (_activeTasks == null || _historyTasks == null) return;

            var allTasks = _activeTasks
                .Concat(_historyTasks)
                .Where(t => t.IsFinished);
            var stats = _getStats(proj.Path, allTasks);
            if (stats.TotalTasks <= 0) return;

            var statsPanel = new StackPanel
            {
                Margin = new Thickness(0, 4, 0, 0)
            };

            var line1 = new TextBlock
            {
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)Application.Current.FindResource("TextMuted"),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            line1.Inlines.Add(new System.Windows.Documents.Run($"{stats.TotalTasks} tasks"));
            line1.Inlines.Add(new System.Windows.Documents.Run(" \u00b7 ") { Foreground = (Brush)Application.Current.FindResource("TextSubdued") });
            line1.Inlines.Add(new System.Windows.Documents.Run($"{stats.SuccessRate:P0} success")
            {
                Foreground = stats.SuccessRate >= 0.7
                    ? BrushCache.Theme("SuccessGreen")
                    : stats.SuccessRate >= 0.4
                        ? BrushCache.Theme("WarningAmber")
                        : BrushCache.Theme("DangerBright")
            });
            if (stats.AverageDuration > TimeSpan.Zero)
            {
                line1.Inlines.Add(new System.Windows.Documents.Run(" \u00b7 ") { Foreground = (Brush)Application.Current.FindResource("TextSubdued") });
                line1.Inlines.Add(new System.Windows.Documents.Run($"avg {ActivityDashboardManager.FormatDuration(stats.AverageDuration)}"));
            }
            statsPanel.Children.Add(line1);

            var line2Parts = new List<string>();
            if (stats.TotalTokens > 0)
                line2Parts.Add($"{FormatHelpers.FormatTokenCount(stats.TotalTokens)} tokens");
            if (stats.MostRecentTaskTime.HasValue)
                line2Parts.Add($"last: {FormatRelativeTime(stats.MostRecentTaskTime.Value)}");
            if (line2Parts.Count > 0)
            {
                statsPanel.Children.Add(new TextBlock
                {
                    Text = string.Join(" \u00b7 ", line2Parts),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            infoPanel.Children.Add(statsPanel);
        }

        private static void AddMcpStatusPanel(StackPanel infoPanel, ProjectEntry proj)
        {
            if (!proj.IsGame) return;

            var statusColor = proj.McpStatus switch
            {
                McpStatus.Connected => BrushCache.Theme("SuccessGreen"),
                McpStatus.Connecting => BrushCache.Theme("WarningAmber"),
                McpStatus.Failed => BrushCache.Theme("DangerBright"),
                _ => BrushCache.Theme("TextMuted")
            };

            var statusText = proj.McpStatus switch
            {
                McpStatus.Connected => "MCP Connected",
                McpStatus.Connecting => "MCP Connecting...",
                McpStatus.Failed => "MCP Failed",
                _ => "MCP Not Connected"
            };

            var mcpStatusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var statusIndicator = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = statusColor,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = statusText
            };

            if (proj.McpStatus == McpStatus.Connecting || proj.McpStatus == McpStatus.Connected)
            {
                var animation = new DoubleAnimation
                {
                    From = 0.3,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromSeconds(1)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                statusIndicator.BeginAnimation(UIElement.OpacityProperty, animation);
            }

            mcpStatusPanel.Children.Add(statusIndicator);
            mcpStatusPanel.Children.Add(new TextBlock
            {
                Text = statusText,
                Foreground = statusColor,
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            });

            infoPanel.Children.Add(mcpStatusPanel);
        }

        private StackPanel BuildMcpButton(ProjectEntry proj)
        {
            var mcpButton = new Button
            {
                Content = proj.McpStatus switch
                {
                    McpStatus.Connected => "Disconnect",
                    McpStatus.Connecting => "Connecting...",
                    _ => "Connect to MCP"
                },
                Style = (Style)Application.Current.FindResource("SmallBtn"),
                Background = proj.McpStatus switch
                {
                    McpStatus.Connected => (Brush)Application.Current.FindResource("BgHover"),
                    McpStatus.Connecting => (Brush)Application.Current.FindResource("BgCard"),
                    _ => (Brush)Application.Current.FindResource("Accent")
                },
                Foreground = proj.McpStatus == McpStatus.Connecting
                    ? (Brush)Application.Current.FindResource("TextMuted")
                    : (Brush)Application.Current.FindResource("TextPrimary"),
                Tag = proj.Path,
                IsEnabled = proj.McpStatus != McpStatus.Connecting
            };

            mcpButton.Click += async (s, ev) =>
            {
                ev.Handled = true;
                if (s is Button b && b.Tag is string path)
                {
                    var projEntry = _data.SavedProjects.FirstOrDefault(p => p.Path == path);
                    if (projEntry != null)
                    {
                        if (projEntry.McpStatus == McpStatus.Connected)
                            _mcp.DisconnectMcp(path);
                        else
                            await _mcp.ConnectMcpAsync(path);
                    }
                }
            };

            var wrapper = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 4, 0, 0)
            };
            wrapper.Children.Add(mcpButton);
            return wrapper;
        }

        private StackPanel BuildButtonPanel(
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var closeBtn = new Button
            {
                Content = "\u2715",
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Padding = new Thickness(4, 0, 4, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = proj.Path,
                ToolTip = "Remove project"
            };
            closeBtn.Click += (s, ev) =>
            {
                ev.Handled = true;
                var termCb = updateTerminalWorkingDirectory ?? _switch.StoredUpdateTerminal;
                var saveCb = saveSettings ?? _switch.StoredSaveSettings;
                var syncCb = syncSettings ?? _switch.StoredSyncSettings;
                if (s is Button b && b.Tag is string path && termCb != null && saveCb != null && syncCb != null)
                    _data.RemoveProject(path, termCb, saveCb, syncCb);
            };
            btnPanel.Children.Add(closeBtn);

            var gearBtn = new Button
            {
                Content = "\uE713",
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("TextSubdued"),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Project settings",
                Margin = new Thickness(0, 4, 0, 0)
            };
            var gearEntry = proj;
            gearBtn.Click += (s, ev) =>
            {
                ev.Handled = true;
                var syncCb = syncSettings ?? _switch.StoredSyncSettings;
                ProjectSettingsDialog.Show(gearEntry, _data.SaveProjects, () =>
                {
                    RefreshProjectList(null, null, null);
                    syncCb?.Invoke();
                }, _data as ProjectManager);
                RefreshProjectList(null, null, null);
                syncCb?.Invoke();
            };
            btnPanel.Children.Add(gearBtn);

            if (!proj.IsFeatureRegistryInitialized)
            {
                var initFeaturesBtn = BuildFeatureInitButton(proj, updateTerminalWorkingDirectory, saveSettings, syncSettings);
                btnPanel.Children.Add(initFeaturesBtn);
            }

            return btnPanel;
        }

        private Button BuildFeatureInitButton(
            ProjectEntry proj,
            Action<string>? updateTerminalWorkingDirectory,
            Action? saveSettings,
            Action? syncSettings)
        {
            var initFeaturesBtn = new Button
            {
                Content = "\uE946",
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = "Initialize Feature Registry",
                Margin = new Thickness(0, 4, 0, 0)
            };
            var initEntry = proj;
            initFeaturesBtn.Click += async (s, ev) =>
            {
                ev.Handled = true;
                if (s is not Button btn) return;
                btn.IsEnabled = false;
                btn.Content = "\u23F3";
                btn.ToolTip = "Analyzing project structure...";

                var pulseAnim = new DoubleAnimation(1.0, 0.4, TimeSpan.FromSeconds(0.8))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase()
                };
                btn.BeginAnimation(UIElement.OpacityProperty, pulseAnim);

                try
                {
                    var registryManager = new FeatureRegistryManager();
                    var initializer = new FeatureInitializer(registryManager);
                    initializer.ProgressChanged += msg =>
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(() => btn.ToolTip = msg);
                        AppLogger.Info("FeatureInit", $"[{initEntry.DisplayName}] {msg}");
                    };
                    var result = await initializer.InitializeAsync(initEntry.Path);
                    btn.BeginAnimation(UIElement.OpacityProperty, null);
                    btn.Opacity = 1.0;
                    if (result != null)
                    {
                        AppLogger.Info("FeatureInit", $"Initialized {result.Features.Count} features for {initEntry.DisplayName}");
                        RefreshProjectList(updateTerminalWorkingDirectory, saveSettings, syncSettings);
                    }
                    else
                    {
                        btn.Content = "\u274C";
                        btn.ToolTip = "Initialization returned no results. Click to retry.";
                        btn.IsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    btn.BeginAnimation(UIElement.OpacityProperty, null);
                    btn.Opacity = 1.0;
                    AppLogger.Error("FeatureInit", $"Feature initialization failed for {initEntry.DisplayName}", ex);
                    btn.Content = "\u274C";
                    btn.ToolTip = $"Failed: {ex.Message}. Click to retry.";
                    btn.IsEnabled = true;
                }
            };
            return initFeaturesBtn;
        }

        private static string FormatRelativeTime(DateTime time)
        {
            var elapsed = DateTime.Now - time;
            if (elapsed.TotalMinutes < 1) return "just now";
            if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
            return time.ToString("MMM d");
        }
    }
}
