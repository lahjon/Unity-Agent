using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HappyEngine.Dialogs;
using HappyEngine.Helpers;
using HappyEngine.Models;
using HappyEngine.Services;

namespace HappyEngine.Managers
{
    /// <summary>
    /// Manages the Git tab in the Statistics panel.
    /// Shows unpushed commits, uncommitted changes, and provides push/fetch operations.
    /// Respects the file lock system and NoGitWrite toggle.
    /// </summary>
    public class GitPanelManager
    {
        private readonly IGitHelper _gitHelper;
        private readonly Func<string> _getProjectPath;
        private readonly Func<bool> _getNoGitWrite;
        private readonly FileLockManager _fileLockManager;
        private readonly GitOperationGuard _gitOperationGuard;
        private readonly Dispatcher _dispatcher;
        private bool _isDirty = true;
        private bool _isRefreshing;
        private CancellationTokenSource? _refreshCts;

        // Cached state
        private bool _gitAvailable;
        private string? _currentBranch;
        private string? _remoteName;
        private List<GitCommitInfo> _unpushedCommits = new();
        private List<GitFileChange> _uncommittedChanges = new();
        private HashSet<string> _selectedFiles = new(); // Track selected files for commit
        private string? _lastError;
        private string? _lastOperationStatus;
        private DateTime? _lastOperationTime;

        // Cached UI elements for optimization
        private StackPanel? _cachedRoot;
        private TextBlock? _cachedBranchHeader;
        private WrapPanel? _cachedActionButtons;
        private Button? _cachedCreateCommitButton;
        private TextBlock? _cachedStatusMessage;
        private Border? _cachedStatusBorder;
        private StackPanel? _cachedUnpushedSection;
        private StackPanel? _cachedUncommittedSection;
        private bool _uiCacheValid = false;

        public GitPanelManager(
            IGitHelper gitHelper,
            Func<string> getProjectPath,
            Func<bool> getNoGitWrite,
            FileLockManager fileLockManager,
            GitOperationGuard gitOperationGuard,
            Dispatcher dispatcher)
        {
            _gitHelper = gitHelper;
            _getProjectPath = getProjectPath;
            _getNoGitWrite = getNoGitWrite;
            _fileLockManager = fileLockManager;
            _gitOperationGuard = gitOperationGuard;
            _dispatcher = dispatcher;
        }

        public void MarkDirty() => _isDirty = true;

        public void RefreshIfNeeded(ScrollViewer container)
        {
            if (!_isDirty) return;
            _isDirty = false;
            _ = RefreshAsync(container);
        }

        public async Task RefreshAsync(ScrollViewer container)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            _refreshCts?.Cancel();
            _refreshCts = new CancellationTokenSource();
            var ct = _refreshCts.Token;

            // Clear old status messages after 30 seconds
            if (_lastOperationTime.HasValue && (DateTime.Now - _lastOperationTime.Value).TotalSeconds > 30)
            {
                _lastOperationStatus = null;
                _lastOperationTime = null;
            }

            // Only show loading state if we don't have cached UI
            if (!_uiCacheValid)
            {
                _dispatcher.Invoke(() =>
                {
                    container.Content = BuildLoadingContent();
                });
            }

            try
            {
                var projectPath = _getProjectPath();
                if (string.IsNullOrEmpty(projectPath))
                {
                    _gitAvailable = false;
                    _lastError = "No project selected.";
                    _dispatcher.Invoke(() => container.Content = BuildContent());
                    return;
                }

                // Check if git is available
                var gitCheck = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse --is-inside-work-tree", ct);
                if (ct.IsCancellationRequested) return;

                if (gitCheck == null || gitCheck.Trim() != "true")
                {
                    _gitAvailable = false;
                    _lastError = "Not a git repository or git is not installed.";
                    _uiCacheValid = false; // Invalidate cache when git becomes unavailable
                    _dispatcher.Invoke(() => container.Content = BuildContent());
                    return;
                }

                _gitAvailable = true;
                _lastError = null;

                // Track previous branch to detect changes
                var previousBranch = _currentBranch;

                // Get current branch
                _currentBranch = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse --abbrev-ref HEAD", ct);
                if (ct.IsCancellationRequested) return;

                // Invalidate cache if branch changed
                if (previousBranch != _currentBranch)
                {
                    _uiCacheValid = false;
                }

                // Get remote name
                _remoteName = await _gitHelper.RunGitCommandAsync(projectPath, "remote", ct);
                if (ct.IsCancellationRequested) return;
                _remoteName = _remoteName?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

                // Get unpushed commits
                await LoadUnpushedCommitsAsync(projectPath, ct);
                if (ct.IsCancellationRequested) return;

                // Get uncommitted changes (for NoGitWrite mode)
                await LoadUncommittedChangesAsync(projectPath, ct);
                if (ct.IsCancellationRequested) return;

                _dispatcher.Invoke(() =>
                {
                    if (_uiCacheValid && _cachedRoot != null)
                    {
                        // Update dynamic content only
                        UpdateDynamicContent();
                        container.Content = _cachedRoot;
                    }
                    else
                    {
                        // Build new content and cache it
                        container.Content = BuildContent();
                        _uiCacheValid = true;
                    }
                });
            }
            catch (OperationCanceledException) { /* cancelled, ignore */ }
            catch (Exception ex)
            {
                _lastError = $"Error: {ex.Message}";
                AppLogger.Warn("GitPanelManager", "Failed to refresh git panel", ex);
                _dispatcher.Invoke(() => container.Content = BuildContent());
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async Task LoadUnpushedCommitsAsync(string projectPath, CancellationToken ct)
        {
            _unpushedCommits.Clear();

            if (string.IsNullOrEmpty(_remoteName) || string.IsNullOrEmpty(_currentBranch))
                return;

            // Check if remote tracking branch exists
            var trackingRef = await _gitHelper.RunGitCommandAsync(projectPath,
                $"rev-parse --verify {_remoteName}/{_currentBranch}", ct);
            if (trackingRef == null) return; // No remote tracking branch

            var logOutput = await _gitHelper.RunGitCommandAsync(projectPath,
                $"log {_remoteName}/{_currentBranch}..HEAD --format=%H|%h|%an|%ar|%s", ct);
            if (string.IsNullOrEmpty(logOutput)) return;

            foreach (var line in logOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 5);
                if (parts.Length < 5) continue;
                _unpushedCommits.Add(new GitCommitInfo
                {
                    FullHash = parts[0].Trim(),
                    ShortHash = parts[1].Trim(),
                    Author = parts[2].Trim(),
                    RelativeTime = parts[3].Trim(),
                    Message = parts[4].Trim()
                });
            }
        }

        private async Task LoadUncommittedChangesAsync(string projectPath, CancellationToken ct)
        {
            _uncommittedChanges.Clear();
            _selectedFiles.Clear(); // Clear selections when refreshing

            var statusOutput = await _gitHelper.RunGitCommandAsync(projectPath,
                "status --porcelain", ct);
            if (string.IsNullOrEmpty(statusOutput)) return;

            foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 3) continue;
                var statusCode = line[..2].Trim();
                var filePath = line[2..].Trim();
                _uncommittedChanges.Add(new GitFileChange
                {
                    Status = statusCode,
                    FilePath = filePath,
                    DisplayStatus = MapGitStatus(statusCode)
                });
            }
        }

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

            // Status message container - create placeholder
            _cachedStatusBorder = new Border();
            _cachedRoot.Children.Add(_cachedStatusBorder);
            if (!string.IsNullOrEmpty(_lastOperationStatus))
            {
                UpdateStatusMessage();
            }

            _cachedRoot.Children.Add(MakeSeparator());

            bool noGitWrite = _getNoGitWrite();

            if (noGitWrite && _uncommittedChanges.Count > 0)
            {
                // NoGitWrite mode: show uncommitted changes with encouragement to commit
                _cachedRoot.Children.Add(BuildCommitEncouragementPanel());
            }

            // Unpushed commits section - cache container
            _cachedUnpushedSection = new StackPanel();
            _cachedRoot.Children.Add(_cachedUnpushedSection);
            UpdateUnpushedSection();

            if (!noGitWrite && _uncommittedChanges.Count > 0)
            {
                _cachedRoot.Children.Add(MakeSeparator());
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
                _cachedCreateCommitButton.IsEnabled = _selectedFiles.Count > 0;
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
            else if (_lastOperationStatus?.Contains("Fetching") == true)
            {
                icon.Text = "\uE895"; // Sync icon
                icon.Foreground = BrushCache.Theme("Accent");
                message.Foreground = BrushCache.Theme("Accent");
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
                var fadeAnimation = new System.Windows.Media.Animation.DoubleAnimation
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

            // Get Latest button
            var fetchBtn = MakeActionButton("\uE895", "Get Latest", "Fetch and pull latest changes from remote");
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
            if (_uncommittedChanges.Count > 0 && HasNoFileLocks())
            {
                _cachedCreateCommitButton = MakeActionButton("\uE73E", "Create Commit", "Create a commit with selected changes");
                _cachedCreateCommitButton.Click += async (_, _) => await ExecuteCreateCommitAsync();
                _cachedCreateCommitButton.IsEnabled = _selectedFiles.Count > 0; // Only enabled when files are selected
                panel.Children.Add(_cachedCreateCommitButton);
            }

            return panel;
        }

        private Border BuildCommitEncouragementPanel()
        {
            var panel = new StackPanel();

            // Header
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            header.Children.Add(new TextBlock
            {
                Text = "\uE946", // Lightbulb icon
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = BrushCache.Theme("WarningAmber"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            header.Children.Add(new TextBlock
            {
                Text = "Uncommitted Changes Detected",
                Foreground = BrushCache.Theme("WarningAmber"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(header);

            panel.Children.Add(new TextBlock
            {
                Text = $"There are {_uncommittedChanges.Count} changed file(s) that should be committed. " +
                       "Since No Git Write is enabled, tasks are not committing their changes automatically. " +
                       "Review the diff below and commit with a proper summary.",
                Foreground = BrushCache.Theme("TextSecondary"),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Generate commit summary suggestion
            var suggestionText = GenerateCommitSummary();
            if (!string.IsNullOrEmpty(suggestionText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Suggested commit message:",
                    Foreground = BrushCache.Theme("TextMuted"),
                    FontSize = 10,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 0, 0, 2)
                });

                var suggestionBox = new TextBox
                {
                    Text = suggestionText,
                    Background = BrushCache.Get("#1A1A2E"),
                    Foreground = BrushCache.Theme("TextLight"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    Padding = new Thickness(8, 6, 8, 6),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                    BorderThickness = new Thickness(1)
                };
                panel.Children.Add(suggestionBox);
            }

            // Commit All button (when NoGitWrite is enabled, this allows manual commit)
            if (HasNoFileLocks())
            {
                var commitBtn = MakeActionButton("\uE73E", "Commit All Changes", "Stage and commit all changes with the suggested message");
                commitBtn.Click += async (_, _) =>
                {
                    // Find the suggestion textbox for commit message
                    var msg = suggestionText ?? "chore: commit pending changes";
                    await ExecuteCommitAllAsync(msg);
                };
                panel.Children.Add(commitBtn);
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Cannot commit while file locks are active. Wait for running tasks to complete.",
                    Foreground = BrushCache.Theme("WarningOrange"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            panel.Children.Add(MakeSeparator());

            // Show file list
            panel.Children.Add(BuildUncommittedFilesList());

            return new Border
            {
                Background = BrushCache.Get("#1A1A1A"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = BrushCache.Theme("WarningAmber"),
                Child = panel
            };
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

            // Commit list with checkboxes for selective push
            var commitCheckboxes = new List<CheckBox>();
            foreach (var commit in _unpushedCommits)
            {
                var commitRow = BuildCommitRow(commit, commitCheckboxes);
                section.Children.Add(commitRow);
            }

            // Push Selected button - always show if there are commits
            if (commitCheckboxes.Count > 0)
            {
                var tooltip = "Push only selected commits (interactive rebase)";
                if (!HasNoFileLocks())
                    tooltip = "Cannot push while files are locked by running tasks";

                var pushSelectedBtn = MakeActionButton("\uE898", "Push Selected", tooltip);
                pushSelectedBtn.Margin = new Thickness(0, 8, 0, 0);
                pushSelectedBtn.IsEnabled = HasNoFileLocks();
                pushSelectedBtn.Click += async (_, _) =>
                {
                    var selected = commitCheckboxes
                        .Where(cb => cb.IsChecked == true)
                        .Select(cb => cb.Tag as string)
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

        private Border BuildCommitRow(GitCommitInfo commit, List<CheckBox> checkboxes)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // checkbox
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) }); // hash
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // message
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); // time

            var cb = new CheckBox
            {
                IsChecked = true,
                Tag = commit.FullHash,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            checkboxes.Add(cb);
            Grid.SetColumn(cb, 0);
            grid.Children.Add(cb);

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

            // Header with select all/none controls
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var header = new TextBlock
            {
                Text = $"Uncommitted Changes ({_uncommittedChanges.Count})",
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI")
            };
            DockPanel.SetDock(header, Dock.Left);
            headerPanel.Children.Add(header);

            // Add select all/none links
            var selectControls = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var selectAll = new TextBlock
            {
                Text = "Select All",
                Foreground = BrushCache.Theme("Accent"),
                FontSize = 10,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0)
            };
            selectAll.MouseLeftButtonDown += (_, _) =>
            {
                foreach (var change in _uncommittedChanges)
                {
                    _selectedFiles.Add(change.FilePath);
                }
                UpdateUncommittedSection();
                UpdateCommitButtonState();
            };
            selectControls.Children.Add(selectAll);

            var selectNone = new TextBlock
            {
                Text = "Select None",
                Foreground = BrushCache.Theme("Accent"),
                FontSize = 10,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            selectNone.MouseLeftButtonDown += (_, _) =>
            {
                _selectedFiles.Clear();
                UpdateUncommittedSection();
                UpdateCommitButtonState();
            };
            selectControls.Children.Add(selectNone);

            DockPanel.SetDock(selectControls, Dock.Right);
            headerPanel.Children.Add(selectControls);

            section.Children.Add(headerPanel);
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

                // Add checkbox for file selection
                var checkBox = new CheckBox
                {
                    IsChecked = _selectedFiles.Contains(change.FilePath),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };

                // Handle checkbox state changes
                checkBox.Checked += (_, _) =>
                {
                    _selectedFiles.Add(change.FilePath);
                    UpdateCommitButtonState();
                };
                checkBox.Unchecked += (_, _) =>
                {
                    _selectedFiles.Remove(change.FilePath);
                    UpdateCommitButtonState();
                };

                DockPanel.SetDock(checkBox, Dock.Left);
                row.Children.Add(checkBox);

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

        // ── Git Operations ─────────────────────────────────────────────

        private async Task ExecuteGetLatestAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            // Store the parent ScrollViewer to refresh UI
            ScrollViewer? scrollViewer = null;
            if (Application.Current.Dispatcher.CheckAccess())
            {
                // Try to find the ScrollViewer in the visual tree
                var window = Application.Current.MainWindow;
                if (window != null)
                {
                    // This is a bit hacky but works for finding the git panel's ScrollViewer
                    var gitPanels = FindVisualChildren<ScrollViewer>(window)
                        .Where(sv => sv.Parent is Border && sv.Content is StackPanel);
                    scrollViewer = gitPanels.FirstOrDefault(sv =>
                        sv.Content is StackPanel sp && sp.Children.Count > 0 &&
                        sp.Children[0] is Border b && b.Child is StackPanel);
                }
            }

            // Execute the fetch/pull operation atomically while ensuring no locks
            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                AppLogger.Info("GitPanelManager", $"Fetching latest for {projectPath}");

                // Show fetching status
                _lastOperationStatus = "Fetching latest changes from remote...";
                _lastOperationTime = DateTime.Now;
                MarkDirty();
                if (scrollViewer != null)
                {
                    await _dispatcher.InvokeAsync(async () => await RefreshAsync(scrollViewer));
                }

                // Get current HEAD before fetch
                var headBefore = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse HEAD");

                // Fetch first
                var fetchResult = await _gitHelper.RunGitCommandAsync(projectPath, "fetch --prune");
                if (fetchResult == null)
                {
                    _lastOperationStatus = "Failed to fetch from remote. Check your network connection and credentials.";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException("Failed to fetch from remote");
                }

                // Check if remote branch has new commits
                var remoteBranch = $"{_remoteName}/{_currentBranch}";
                var behindCount = await _gitHelper.RunGitCommandAsync(projectPath, $"rev-list --count HEAD..{remoteBranch}");
                var behind = int.TryParse(behindCount?.Trim(), out var count) ? count : 0;

                if (behind == 0)
                {
                    _lastOperationStatus = "Already up to date - no changes found";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", "Get Latest: already up to date");
                }
                else
                {
                    // Pull with rebase to keep history clean
                    var pullResult = await _gitHelper.RunGitCommandAsync(projectPath, "pull --rebase");
                    if (pullResult == null)
                    {
                        _lastOperationStatus = "Pull failed. There may be conflicts or uncommitted changes preventing the merge.";
                        _lastOperationTime = DateTime.Now;
                        throw new InvalidOperationException("Pull failed");
                    }
                    else
                    {
                        // Get current HEAD after pull
                        var headAfter = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse HEAD");

                        if (headBefore != headAfter)
                        {
                            _lastOperationStatus = $"Successfully pulled {behind} new commit{(behind != 1 ? "s" : "")}";
                        }
                        else
                        {
                            _lastOperationStatus = "Pull completed, but no new commits were applied";
                        }
                        _lastOperationTime = DateTime.Now;
                        AppLogger.Info("GitPanelManager", $"Get Latest completed: {pullResult}");
                    }
                }

                MarkDirty();
            }, "fetch");

            if (!success)
            {
                _lastOperationStatus = errorMessage!;
                _lastOperationTime = DateTime.Now;
                MarkDirty();
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T t)
                {
                    yield return t;
                }

                foreach (T childOfChild in FindVisualChildren<T>(child))
                {
                    yield return childOfChild;
                }
            }
        }

        private async Task ExecuteInitializeGitAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            var confirmResult = MessageBox.Show(
                $"Initialize a new git repository in:\n\n{projectPath}\n\nThis will create a .git folder and enable version control for this project.",
                "Initialize Git Repository",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes) return;

            _lastOperationStatus = "Initializing git repository...";
            _lastOperationTime = DateTime.Now;

            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                AppLogger.Info("GitPanelManager", $"Initializing git repository for {projectPath}");

                var result = await _gitHelper.RunGitCommandAsync(projectPath, "init");
                if (result == null)
                {
                    _lastOperationStatus = "Failed to initialize git repository";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException("Git init command failed");
                }

                _lastOperationStatus = "Git repository initialized successfully!";
                _lastOperationTime = DateTime.Now;

                // Also create an initial .gitignore if it doesn't exist
                var gitignorePath = Path.Combine(projectPath, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    // Basic .gitignore for common files
                    var gitignoreContent = @"# Build outputs
bin/
obj/
*.dll
*.exe
*.pdb

# User-specific files
*.user
*.suo
.vs/
.vscode/
.idea/

# OS files
.DS_Store
Thumbs.db

# Logs
*.log

# Package files
packages/
node_modules/

# Temporary files
*.tmp
*.temp
~*

# Sensitive files
*.env
appsettings.*.json
secrets.json
";
                    await File.WriteAllTextAsync(gitignorePath, gitignoreContent);
                    AppLogger.Info("GitPanelManager", "Created default .gitignore file");
                }

                // Force refresh
                MarkDirty();

                // Find the ScrollViewer to trigger immediate refresh
                await _dispatcher.InvokeAsync(async () =>
                {
                    // The button that triggered this is in the git panel, find its ScrollViewer parent
                    if (Application.Current.MainWindow is MainWindow mainWindow)
                    {
                        var gitTabContent = mainWindow.FindName("GitTabContent") as ScrollViewer;
                        if (gitTabContent != null)
                        {
                            await RefreshAsync(gitTabContent);
                        }
                    }
                });
            }, "init");

            if (!success)
            {
                _lastOperationStatus = errorMessage ?? "Git initialization failed";
                _lastOperationTime = DateTime.Now;
                MessageBox.Show($"Failed to initialize git repository: {errorMessage}\n\nPlease check if git is installed and accessible.",
                    "Initialize Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePushAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            // Execute the push operation atomically while ensuring no locks
            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                AppLogger.Info("GitPanelManager", $"Pushing all for {projectPath}");

                // Show pushing status
                _lastOperationStatus = $"Pushing {_unpushedCommits.Count} commit{(_unpushedCommits.Count != 1 ? "s" : "")} to remote...";
                _lastOperationTime = DateTime.Now;
                MarkDirty();

                var result = await _gitHelper.RunGitCommandAsync(projectPath, "push");
                if (result == null)
                {
                    _lastOperationStatus = "Push failed. The remote may have new changes. Try 'Get Latest' first.";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException("Push failed");
                }
                else
                {
                    _lastOperationStatus = $"Successfully pushed {_unpushedCommits.Count} commit{(_unpushedCommits.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", "Push completed successfully");
                }

                MarkDirty();
            }, "push");

            if (!success)
            {
                _lastOperationStatus = errorMessage!;
                _lastOperationTime = DateTime.Now;
                MarkDirty();
            }
        }

        private async Task ExecutePushUpToAsync(string commitHash)
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(_currentBranch)) return;

            // Execute the push operation atomically while ensuring no locks
            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                await ExecutePushUpToInternalAsync(projectPath, commitHash);
            }, "push selected commits");

            if (!success)
            {
                MessageBox.Show(
                    errorMessage + ". Wait for running tasks to complete first.",
                    "Push Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task ExecutePushUpToInternalAsync(string projectPath, string commitHash)
        {
            AppLogger.Info("GitPanelManager", $"Pushing up to {commitHash} for {projectPath}");
            var result = await _gitHelper.RunGitCommandAsync(projectPath,
                $"push {_remoteName} {commitHash}:{_currentBranch}");
            if (result == null)
            {
                throw new InvalidOperationException("Push failed. The remote may have new changes.\n\nTry 'Get Latest' first, then push again.");
            }

            AppLogger.Info("GitPanelManager", "Selective push completed successfully");
            MarkDirty();
        }

        private async Task ExecuteCommitAllAsync(string message)
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            if (_uncommittedChanges.Count == 0)
            {
                _lastOperationStatus = "No uncommitted changes to commit";
                _lastOperationTime = DateTime.Now;
                MarkDirty();
                return;
            }

            // Execute the commit operation atomically while ensuring no locks
            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                // Show committing status
                _lastOperationStatus = $"Committing {_uncommittedChanges.Count} file{(_uncommittedChanges.Count != 1 ? "s" : "")}...";
                _lastOperationTime = DateTime.Now;
                MarkDirty();

                // Stage only the specific files we know about — never use "add -A"
                // because concurrent tasks may have created changes we shouldn't include
                var filePaths = _uncommittedChanges
                    .Select(c => GitHelper.EscapeGitPath(c.FilePath))
                    .ToList();
                var pathArgs = string.Join(" ", filePaths);

                AppLogger.Info("GitPanelManager", $"Committing {filePaths.Count} file(s) for {projectPath}");

                var addResult = await _gitHelper.RunGitCommandAsync(projectPath, $"add -- {pathArgs}");
                if (addResult == null)
                {
                    _lastOperationStatus = "Failed to stage changes";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException("Failed to stage changes");
                }

                // Use the secure commit method to prevent shell injection
                var commitResult = await _gitHelper.CommitSecureAsync(projectPath, message, pathArgs);
                if (commitResult == null)
                {
                    _lastOperationStatus = "Commit failed. There may be nothing to commit or a hook rejected it";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException("Commit failed");
                }
                else
                {
                    _lastOperationStatus = $"Successfully committed {filePaths.Count} file{(filePaths.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", $"Commit completed: {commitResult}");
                }

                MarkDirty();
            }, "commit");

            if (!success)
            {
                _lastOperationStatus = errorMessage!;
                _lastOperationTime = DateTime.Now;
                MarkDirty();
            }
        }

        private async Task ExecuteCreateCommitAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            if (_selectedFiles.Count == 0)
            {
                MessageBox.Show("Please select files to commit.", "Create Commit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Get selected files
            var selectedChanges = _uncommittedChanges
                .Where(c => _selectedFiles.Contains(c.FilePath))
                .ToList();

            if (selectedChanges.Count == 0)
            {
                MessageBox.Show("Selected files are no longer in the uncommitted changes list.", "Create Commit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Generate suggested commit message for selected files only
            var suggestionText = GenerateCommitSummaryForFiles(selectedChanges);

            // Show commit dialog
            var result = CommitDialog.Show(selectedChanges, suggestionText);
            if (result.Cancelled) return;

            // Execute the commit operation atomically while ensuring no locks
            var (success, errorMessage) = await _gitOperationGuard.ExecuteWhileNoLocksHeldAsync(async () =>
            {
                // Show committing status
                _lastOperationStatus = $"Committing {selectedChanges.Count} selected file{(selectedChanges.Count != 1 ? "s" : "")}...";
                _lastOperationTime = DateTime.Now;
                MarkDirty();

                // Stage only the selected files
                var filePaths = selectedChanges
                    .Select(c => GitHelper.EscapeGitPath(c.FilePath))
                    .ToList();
                var pathArgs = string.Join(" ", filePaths);

                AppLogger.Info("GitPanelManager", $"Committing {filePaths.Count} selected file(s) for {projectPath}");

                var addResult = await _gitHelper.RunGitCommandAsync(projectPath, $"add -- {pathArgs}");
                if (addResult == null)
                {
                    _lastOperationStatus = "Failed to stage selected changes";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException("Failed to stage selected changes");
                }

                // Use the secure commit method to prevent shell injection
                var commitResult = await _gitHelper.CommitSecureAsync(projectPath, result.Message, pathArgs);
                if (commitResult == null)
                {
                    _lastOperationStatus = "Commit failed. There may be nothing to commit or a hook rejected it";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException("Commit failed");
                }
                else
                {
                    _lastOperationStatus = $"Successfully committed {filePaths.Count} selected file{(filePaths.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                }

                MarkDirty();
            }, "commit");

            if (!success)
            {
                _lastOperationStatus = errorMessage!;
                _lastOperationTime = DateTime.Now;
                MarkDirty();
                MessageBox.Show($"Commit failed: {errorMessage}", "Create Commit", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // Clear selections after successful commit
                _selectedFiles.Clear();
                MarkDirty();
            }
        }

        private string GenerateCommitSummaryForFiles(List<GitFileChange> files)
        {
            if (files.Count == 0) return "";

            var added = files.Count(c => c.Status.Contains("A") || c.Status == "??");
            var modified = files.Count(c => c.Status.Contains("M"));
            var deleted = files.Count(c => c.Status.Contains("D"));
            var renamed = files.Count(c => c.Status.Contains("R"));

            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} added");
            if (modified > 0) parts.Add($"{modified} modified");
            if (deleted > 0) parts.Add($"{deleted} deleted");
            if (renamed > 0) parts.Add($"{renamed} renamed");

            // Try to determine common directory or pattern
            var dirs = files
                .Select(c => System.IO.Path.GetDirectoryName(c.FilePath)?.Replace('\\', '/'))
                .Where(d => !string.IsNullOrEmpty(d))
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            var scope = dirs.Count > 0 ? string.Join(", ", dirs!) : "project";
            return $"chore: update {scope} ({string.Join(", ", parts)})";
        }

        // ── Helpers ────────────────────────────────────────────────────

        private bool HasNoFileLocks()
        {
            return _fileLockManager.LockCount == 0;
        }

        private string GenerateCommitSummary()
        {
            if (_uncommittedChanges.Count == 0) return "";

            var added = _uncommittedChanges.Count(c => c.Status.Contains("A") || c.Status == "??");
            var modified = _uncommittedChanges.Count(c => c.Status.Contains("M"));
            var deleted = _uncommittedChanges.Count(c => c.Status.Contains("D"));
            var renamed = _uncommittedChanges.Count(c => c.Status.Contains("R"));

            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} added");
            if (modified > 0) parts.Add($"{modified} modified");
            if (deleted > 0) parts.Add($"{deleted} deleted");
            if (renamed > 0) parts.Add($"{renamed} renamed");

            // Try to determine common directory or pattern
            var dirs = _uncommittedChanges
                .Select(c => System.IO.Path.GetDirectoryName(c.FilePath)?.Replace('\\', '/'))
                .Where(d => !string.IsNullOrEmpty(d))
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key)
                .ToList();

            var scope = dirs.Count > 0 ? string.Join(", ", dirs!) : "project";
            return $"chore: update {scope} ({string.Join(", ", parts)})";
        }

        private static Button MakeActionButton(string icon, string label, string tooltip)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var button = new Button
            {
                Content = stack,
                ToolTip = tooltip,
                Style = Application.Current.TryFindResource("SmallBtn") as Style,
                Background = BrushCache.Theme("Accent"),
                Margin = new Thickness(0, 0, 6, 4)
            };

            return button;
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

    // ── Data Models ────────────────────────────────────────────────

    public class GitCommitInfo
    {
        public string FullHash { get; set; } = "";
        public string ShortHash { get; set; } = "";
        public string Author { get; set; } = "";
        public string RelativeTime { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class GitFileChange
    {
        public string Status { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string DisplayStatus { get; set; } = "";
    }
}
