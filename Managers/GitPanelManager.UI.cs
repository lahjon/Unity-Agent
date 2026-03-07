using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Spritely.Helpers;

namespace Spritely.Managers
{
    public partial class GitPanelManager
    {
        // ── UI Building ────────────────────────────────────────────────

        private StackPanel BuildLoadingContent()
        {
            var root = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
            root.Children.Add(new TextBlock
            {
                Text = "Loading git information...",
                Foreground = BrushCache.Theme("TextMuted"),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 12, 0, 0)
            });
            return root;
        }

        private StackPanel BuildContent()
        {
            // Create or reuse root panel
            _cachedRoot = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };

            if (!_gitAvailable)
            {
                _cachedRoot.Children.Add(BuildGitUnavailablePanel());
                _uiCacheValid = false; // Don't cache when git is unavailable
                return _cachedRoot;
            }

            // Branch info header - cache it
            _cachedBranchHeader = BuildBranchHeaderInternal();
            _cachedRoot.Children.Add(_cachedBranchHeader);

            // Action buttons row - cache it
            _cachedActionButtons = BuildActionButtonsInternal();
            _cachedRoot.Children.Add(_cachedActionButtons);

            // Auto-Commit toggle
            var autoCommitToggle = MakeToggleSwitch(_settingsManager.AutoCommit, "Auto-Commit");
            autoCommitToggle.Margin = new Thickness(0, 0, 0, 6);
            autoCommitToggle.Checked += (_, _) => _settingsManager.AutoCommit = true;
            autoCommitToggle.Unchecked += (_, _) => _settingsManager.AutoCommit = false;
            _cachedRoot.Children.Add(autoCommitToggle);

            // Status message container - create placeholder
            _cachedStatusBorder = new Border();
            _cachedRoot.Children.Add(_cachedStatusBorder);
            if (!string.IsNullOrEmpty(_lastOperationStatus))
            {
                UpdateStatusMessage();
            }

            // Unpushed commits section - cache container
            _cachedUnpushedSection = new StackPanel();
            _cachedRoot.Children.Add(_cachedUnpushedSection);
            UpdateUnpushedSection();

            if (_uncommittedChanges.Count > 0)
            {
                // Uncommitted changes section - cache container
                _cachedUncommittedSection = new StackPanel();
                _cachedRoot.Children.Add(_cachedUncommittedSection);
                UpdateUncommittedSection();
            }

            return _cachedRoot;
        }

        private void UpdateDynamicContent()
        {
            if (_cachedRoot == null || !_uiCacheValid) return;

            // Update branch header text
            if (_cachedBranchHeader != null)
            {
                _cachedBranchHeader.Text = $"📍 {(_currentBranch ?? "unknown")} " +
                    (_remoteName == null ? "(no remote)" : $"→ {_remoteName}/{_currentBranch}");
            }

            // Update status message
            UpdateStatusMessage();

            // Update sections
            UpdateUnpushedSection();
            if (_cachedUncommittedSection != null)
            {
                UpdateUncommittedSection();
            }
        }

        private void UpdateStatusMessage()
        {
            if (_cachedStatusBorder == null) return;

            if (!string.IsNullOrEmpty(_lastOperationStatus))
            {
                if (_cachedStatusMessage == null)
                {
                    var statusPanel = BuildStatusMessage();
                    _cachedStatusBorder.Child = statusPanel;
                    _cachedStatusMessage = (statusPanel.Child as DockPanel)?.Children.OfType<TextBlock>()
                        .LastOrDefault(); // The message TextBlock is the second (last) TextBlock
                }
                else if (_cachedStatusMessage != null)
                {
                    _cachedStatusMessage.Text = _lastOperationStatus;
                }
                _cachedStatusBorder.Visibility = Visibility.Visible;
            }
            else
            {
                _cachedStatusBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUnpushedSection()
        {
            if (_cachedUnpushedSection == null) return;

            _cachedUnpushedSection.Children.Clear();

            if (_unpushedCommits.Count > 0)
            {
                // Build the unpushed commits content
                var content = BuildUnpushedCommitsSection();
                _cachedUnpushedSection.Children.Add(content);

                // Add separator if uncommitted changes will follow
                if (_cachedUncommittedSection != null)
                {
                    _cachedUnpushedSection.Children.Add(MakeSeparator());
                }
            }
        }

        private void UpdateUncommittedSection()
        {
            if (_cachedUncommittedSection == null) return;

            _cachedUncommittedSection.Children.Clear();

            // Build the uncommitted changes content
            var content = BuildUncommittedChangesSection();
            _cachedUncommittedSection.Children.Add(content);
        }

        private TextBlock BuildBranchHeaderInternal()
        {
            var branchHeader = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = BrushCache.Theme("TextLight"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            branchHeader.Text = $"📍 {(_currentBranch ?? "unknown")} " +
                (_remoteName == null ? "(no remote)" : $"→ {_remoteName}/{_currentBranch}");
            return branchHeader;
        }

        private WrapPanel BuildActionButtonsInternal()
        {
            return BuildActionButtons();
        }

        private void UpdateCommitButtonState()
        {
            if (_cachedCreateCommitButton == null) return;

            _dispatcher.InvokeAsync(() =>
            {
                // Update enabled state
                _cachedCreateCommitButton.IsEnabled = _selectedFiles.Count > 0 && HasNoFileLocks();

                // Update tooltip based on state
                string tooltip = "Create a commit with selected changes";
                if (_selectedFiles.Count == 0)
                    tooltip = "Select files to commit";
                else if (!HasNoFileLocks())
                    tooltip = "Cannot commit while files are locked by running tasks";

                _cachedCreateCommitButton.ToolTip = tooltip;
            });
        }

        private Border BuildGitUnavailablePanel()
        {
            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(new TextBlock
            {
                Text = "\uE783", // Warning icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = BrushCache.Theme("WarningOrange"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Git Not Available",
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
                Text = _lastError ?? "Unable to connect to git.",
                Foreground = BrushCache.Theme("TextMuted"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            // Add Initialize Git button if it's simply not a git repo (not a git installation issue)
            if (_lastError != null && _lastError.Contains("Not a git repository"))
            {
                var initButton = MakeActionButton("\uE710", "Initialize Git", "Initialize a new git repository in this project");
                initButton.Margin = new Thickness(0, 16, 0, 8);
                initButton.HorizontalAlignment = HorizontalAlignment.Center;
                initButton.Click += async (_, _) => await ExecuteInitializeGitAsync();
                panel.Children.Add(initButton);
            }

            return new Border
            {
                Background = BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 0),
                Child = panel
            };
        }

        private Border BuildStatusMessage()
        {
            var isRecent = _lastOperationTime.HasValue &&
                          (DateTime.Now - _lastOperationTime.Value).TotalSeconds < 10;

            var panel = new DockPanel();

            // Icon based on status type
            var icon = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var message = new TextBlock
            {
                Text = _lastOperationStatus ?? "",
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            // Check if this is an active operation (contains "..." or verbs ending in "ing")
            bool isActiveOperation = _lastOperationStatus?.Contains("...", StringComparison.Ordinal) == true ||
                                   (_lastOperationStatus?.Contains("ing ", StringComparison.OrdinalIgnoreCase) == true &&
                                    !_lastOperationStatus.Contains("nothing", StringComparison.OrdinalIgnoreCase));

            // Style based on message type
            if (_lastOperationStatus?.Contains("Successfully") == true)
            {
                icon.Text = "\uE73E"; // Checkmark
                icon.Foreground = BrushCache.Get("#4EC969");
                message.Foreground = BrushCache.Get("#4EC969");
            }
            else if (_lastOperationStatus?.Contains("up to date") == true ||
                     _lastOperationStatus?.Contains("No changes") == true)
            {
                icon.Text = "\uE73E"; // Checkmark
                icon.Foreground = BrushCache.Theme("TextMuted");
                message.Foreground = BrushCache.Theme("TextMuted");
            }
            else if (_lastOperationStatus?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true ||
                     _lastOperationStatus?.Contains("error", StringComparison.OrdinalIgnoreCase) == true)
            {
                icon.Text = "\uE814"; // Error
                icon.Foreground = BrushCache.Theme("ErrorRed");
                message.Foreground = BrushCache.Theme("ErrorRed");
            }
            else if (isActiveOperation)
            {
                // For active operations, use sync icon with spinning animation
                icon.Text = "\uE895"; // Sync icon
                icon.Foreground = BrushCache.Theme("Accent");
                message.Foreground = BrushCache.Theme("Accent");

                // Add spinning animation
                var rotateTransform = new RotateTransform(0);
                icon.RenderTransform = rotateTransform;
                icon.RenderTransformOrigin = new Point(0.5, 0.5);

                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(2),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
            }
            else
            {
                icon.Text = "\uE814"; // Info
                icon.Foreground = BrushCache.Theme("TextSecondary");
                message.Foreground = BrushCache.Theme("TextSecondary");
            }

            DockPanel.SetDock(icon, Dock.Left);
            panel.Children.Add(icon);
            panel.Children.Add(message);

            var border = new Border
            {
                Background = isRecent ? BrushCache.Get("#1A1A1A") : BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 4, 0, 4),
                Child = panel
            };

            // Fade out animation for recent messages
            if (isRecent)
            {
                border.Opacity = 1.0;
                var fadeAnimation = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.8,
                    Duration = TimeSpan.FromSeconds(5),
                    BeginTime = TimeSpan.FromSeconds(5)
                };
                border.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
            }

            return border;
        }

        private DockPanel BuildBranchHeader()
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            var branchIcon = new TextBlock
            {
                Text = "\uE8AD", // Branch icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            dock.Children.Add(branchIcon);

            var branchName = new TextBlock
            {
                Text = _currentBranch ?? "unknown",
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            dock.Children.Add(branchName);

            if (!string.IsNullOrEmpty(_remoteName))
            {
                var remoteLabel = new TextBlock
                {
                    Text = $"  \u2192  {_remoteName}",
                    Foreground = BrushCache.Theme("TextMuted"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                dock.Children.Add(remoteLabel);
            }

            // Unpushed count badge
            if (_unpushedCommits.Count > 0)
            {
                var badge = new Border
                {
                    Background = BrushCache.Theme("WarningOrange"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = $"{_unpushedCommits.Count} unpushed",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI")
                    }
                };
                dock.Children.Add(badge);
            }

            return dock;
        }

        private WrapPanel BuildActionButtons()
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };

            // Get Latest button - using download arrow icon
            var fetchBtn = MakeActionButton("\uE74A", "Get Latest", "Fetch and pull latest changes from remote");
            fetchBtn.Click += async (_, _) => await ExecuteGetLatestAsync();
            panel.Children.Add(fetchBtn);

            // Refresh button
            var refreshBtn = MakeActionButton("\uE72C", "Refresh", "Refresh git status");
            refreshBtn.Click += (_, _) =>
            {
                // Clear status message on manual refresh
                _lastOperationStatus = null;
                _lastOperationTime = null;
                MarkDirty();
                // Find the parent ScrollViewer to refresh
                if (refreshBtn.Parent is WrapPanel wp &&
                    wp.Parent is StackPanel sp &&
                    sp.Parent is ScrollViewer sv)
                {
                    _ = RefreshAsync(sv);
                }
            };
            panel.Children.Add(refreshBtn);

            // Push All button - always show it but disable when appropriate
            {
                var tooltip = "Push all unpushed commits to remote";
                if (_unpushedCommits.Count == 0)
                    tooltip = "No unpushed commits to push";
                else if (!HasNoFileLocks())
                    tooltip = "Cannot push while files are locked by running tasks";

                var pushAllBtn = MakeActionButton("\uE898", "Push All", tooltip);
                pushAllBtn.IsEnabled = _unpushedCommits.Count > 0 && HasNoFileLocks();
                pushAllBtn.Click += async (_, _) => await ExecutePushAsync();
                panel.Children.Add(pushAllBtn);
            }

            // Create Commit button (show when there are uncommitted changes)
            if (_uncommittedChanges.Count > 0)
            {
                // Determine tooltip based on state
                var tooltip = "Create a commit with selected changes";
                if (_selectedFiles.Count == 0)
                    tooltip = "Select files to commit";
                else if (!HasNoFileLocks())
                    tooltip = "Cannot commit while files are locked by running tasks";

                _cachedCreateCommitButton = MakeActionButton("\uE73E", "Create Commit", tooltip);
                _cachedCreateCommitButton.Click += async (_, _) => await ExecuteCreateCommitAsync();
                _cachedCreateCommitButton.IsEnabled = _selectedFiles.Count > 0 && HasNoFileLocks(); // Enabled when files are selected AND no locks
                panel.Children.Add(_cachedCreateCommitButton);
            }

            return panel;
        }

        private StackPanel BuildUnpushedCommitsSection()
        {
            var section = new StackPanel();

            var header = new TextBlock
            {
                Text = _unpushedCommits.Count > 0
                    ? $"Unpushed Commits ({_unpushedCommits.Count})"
                    : "No Unpushed Commits",
                Foreground = BrushCache.Theme(_unpushedCommits.Count > 0 ? "TextLight" : "TextMuted"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            section.Children.Add(header);

            if (_unpushedCommits.Count == 0)
            {
                section.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(_remoteName)
                        ? "No remote configured for this repository."
                        : "All commits are pushed to remote.",
                    Foreground = BrushCache.Theme("TextMuted"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI")
                });
                return section;
            }

            // Commit list with toggle switches for selective push
            var commitToggles = new List<ToggleButton>();
            foreach (var commit in _unpushedCommits)
            {
                var commitRow = BuildCommitRow(commit, commitToggles);
                section.Children.Add(commitRow);
            }

            // Push Selected button - always show if there are commits
            if (commitToggles.Count > 0)
            {
                var tooltip = "Push only selected commits (interactive rebase)";
                if (!HasNoFileLocks())
                    tooltip = "Cannot push while files are locked by running tasks";

                var pushSelectedBtn = MakeActionButton("\uE898", "Push Selected", tooltip);
                pushSelectedBtn.Margin = new Thickness(0, 8, 0, 0);
                pushSelectedBtn.IsEnabled = HasNoFileLocks();
                pushSelectedBtn.Click += async (_, _) =>
                {
                    var selected = commitToggles
                        .Where(tb => tb.IsChecked == true)
                        .Select(tb => tb.Tag as string)
                        .Where(h => h != null)
                        .ToList();

                    if (selected!.Count == 0)
                    {
                        MessageBox.Show("No commits selected.", "Push Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (selected.Count == _unpushedCommits.Count)
                    {
                        // All selected = push all
                        await ExecutePushAsync();
                    }
                    else
                    {
                        // Push up to the oldest selected commit
                        var oldestSelected = _unpushedCommits.Last(c => selected.Contains(c.FullHash));
                        await ExecutePushUpToAsync(oldestSelected.FullHash);
                    }
                };
                section.Children.Add(pushSelectedBtn);
            }

            return section;
        }

        private Border BuildCommitRow(GitCommitInfo commit, List<ToggleButton> toggles)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // toggle
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); // hash
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // message
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); // time

            var toggle = new ToggleButton
            {
                IsChecked = true,
                Tag = commit.FullHash,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Style = Application.Current.TryFindResource("ToggleSwitch") as Style
            };
            toggles.Add(toggle);
            Grid.SetColumn(toggle, 0);
            grid.Children.Add(toggle);

            var hash = new TextBlock
            {
                Text = commit.ShortHash,
                Foreground = BrushCache.Theme("Accent"),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hash, 1);
            grid.Children.Add(hash);

            var message = new TextBlock
            {
                Text = commit.Message,
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"{commit.Message}\n\nAuthor: {commit.Author}\nHash: {commit.FullHash}"
            };
            Grid.SetColumn(message, 2);
            grid.Children.Add(message);

            var time = new TextBlock
            {
                Text = commit.RelativeTime,
                Foreground = BrushCache.Theme("TextMuted"),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(time, 3);
            grid.Children.Add(time);

            return new Border
            {
                Background = BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Child = grid
            };
        }

        private StackPanel BuildUncommittedChangesSection()
        {
            var section = new StackPanel();

            // Header without select controls
            var header = new TextBlock
            {
                Text = $"Uncommitted Changes ({_uncommittedChanges.Count})",
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            };
            section.Children.Add(header);

            // Add select all/none buttons above the file list
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var selectAllButton = new Button
            {
                Content = "Select All",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("BgHover"),
                MinWidth = 80,
                Height = 26,
                Margin = new Thickness(0, 0, 8, 0)
            };
            selectAllButton.Click += (_, _) =>
            {
                foreach (var change in _uncommittedChanges)
                {
                    _selectedFiles.Add(change.FilePath);
                }
                UpdateUncommittedSection();
                UpdateCommitButtonState();
            };
            buttonPanel.Children.Add(selectAllButton);

            var selectNoneButton = new Button
            {
                Content = "Select None",
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = (Brush)Application.Current.FindResource("BgHover"),
                MinWidth = 80,
                Height = 26
            };
            selectNoneButton.Click += (_, _) =>
            {
                _selectedFiles.Clear();
                UpdateUncommittedSection();
                UpdateCommitButtonState();
            };
            buttonPanel.Children.Add(selectNoneButton);

            section.Children.Add(buttonPanel);
            section.Children.Add(BuildUncommittedFilesList());

            return section;
        }

        private UIElement BuildUncommittedFilesList()
        {
            var list = new StackPanel();

            // Display all uncommitted changes without limit
            foreach (var change in _uncommittedChanges)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };

                // Add toggle switch for file selection
                var toggle = new ToggleButton
                {
                    IsChecked = _selectedFiles.Contains(change.FilePath),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    Style = Application.Current.TryFindResource("ToggleSwitch") as Style
                };

                // Handle toggle state changes
                toggle.Checked += (_, _) =>
                {
                    _selectedFiles.Add(change.FilePath);
                    UpdateCommitButtonState();
                };
                toggle.Unchecked += (_, _) =>
                {
                    _selectedFiles.Remove(change.FilePath);
                    UpdateCommitButtonState();
                };

                DockPanel.SetDock(toggle, Dock.Left);
                row.Children.Add(toggle);

                var statusBadge = new Border
                {
                    Background = GetStatusColor(change.Status),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = change.DisplayStatus,
                        Foreground = Brushes.White,
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Segoe UI")
                    }
                };
                DockPanel.SetDock(statusBadge, Dock.Left);
                row.Children.Add(statusBadge);

                var filePath = new TextBlock
                {
                    Text = change.FilePath,
                    Foreground = BrushCache.Theme("TextSecondary"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = change.FilePath
                };
                row.Children.Add(filePath);

                list.Children.Add(new Border
                {
                    Background = BrushCache.Theme("BgSection"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = row
                });
            }

            // Wrap the list in a ScrollViewer
            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 300, // Reasonable height that allows seeing multiple items
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = list
            };

            return scrollViewer;
        }

        // ── UI Helpers ────────────────────────────────────────────────

        private static Button MakeActionButton(string icon, string label, string tooltip)
        {
            // Create an icon-only button to match task card style
            var button = new Button
            {
                Content = icon,
                ToolTip = tooltip,
                Style = Application.Current.TryFindResource("IconBtn") as Style,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 0),
                // Make buttons orange for better visibility
                Foreground = BrushCache.Theme("StatusOrange")
            };

            // Override hover color to maintain orange visibility
            button.MouseEnter += (s, e) =>
            {
                if (button.IsEnabled)
                    button.Foreground = BrushCache.Theme("WarningDeepOrange");
            };
            button.MouseLeave += (s, e) =>
            {
                button.Foreground = button.IsEnabled ? BrushCache.Theme("StatusOrange") : BrushCache.Theme("TextMuted");
            };

            // Update color when enabled state changes
            button.IsEnabledChanged += (s, e) =>
            {
                button.Foreground = button.IsEnabled ? BrushCache.Theme("StatusOrange") : BrushCache.Theme("TextMuted");
            };

            return button;
        }

        private static ToggleButton MakeToggleSwitch(bool isChecked, string labelText)
        {
            var toggle = new ToggleButton
            {
                IsChecked = isChecked,
                Style = Application.Current.TryFindResource("ToggleSwitch") as Style
            };

            // Add the label text
            toggle.Content = new TextBlock
            {
                Text = labelText,
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            };

            return toggle;
        }

        private static Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = BrushCache.Theme("BgElevated"),
                Margin = new Thickness(0, 8, 0, 8)
            };
        }

        private static string MapGitStatus(string code)
        {
            return code switch
            {
                "M" => "MOD",
                "A" => "ADD",
                "D" => "DEL",
                "R" => "REN",
                "C" => "CPY",
                "U" => "UPD",
                "??" => "NEW",
                "MM" => "MOD",
                "AM" => "ADD",
                _ => code
            };
        }

        private static SolidColorBrush GetStatusColor(string code)
        {
            return code switch
            {
                "M" or "MM" => BrushCache.Get("#E5C07B"),  // Modified = amber
                "A" or "AM" or "??" => BrushCache.Get("#4EC969"), // Added/New = green
                "D" => BrushCache.Get("#E06C75"),          // Deleted = red
                "R" or "C" => BrushCache.Get("#61AFEF"),   // Renamed/Copied = blue
                _ => BrushCache.Get("#666666")
            };
        }
    }
}
