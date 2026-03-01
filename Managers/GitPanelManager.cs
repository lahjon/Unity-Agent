using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HappyEngine.Helpers;
using HappyEngine.Models;

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
        private string? _lastError;

        public GitPanelManager(
            IGitHelper gitHelper,
            Func<string> getProjectPath,
            Func<bool> getNoGitWrite,
            FileLockManager fileLockManager,
            Dispatcher dispatcher)
        {
            _gitHelper = gitHelper;
            _getProjectPath = getProjectPath;
            _getNoGitWrite = getNoGitWrite;
            _fileLockManager = fileLockManager;
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

            // Show loading state
            _dispatcher.Invoke(() =>
            {
                container.Content = BuildLoadingContent();
            });

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
                    _dispatcher.Invoke(() => container.Content = BuildContent());
                    return;
                }

                _gitAvailable = true;
                _lastError = null;

                // Get current branch
                _currentBranch = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse --abbrev-ref HEAD", ct);
                if (ct.IsCancellationRequested) return;

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

                _dispatcher.Invoke(() => container.Content = BuildContent());
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

            var statusOutput = await _gitHelper.RunGitCommandAsync(projectPath,
                "status --porcelain", ct);
            if (string.IsNullOrEmpty(statusOutput)) return;

            foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 3) continue;
                var statusCode = line[..2].Trim();
                var filePath = line[3..].Trim();
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
            var root = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };

            if (!_gitAvailable)
            {
                root.Children.Add(BuildGitUnavailablePanel());
                return root;
            }

            // Branch info header
            root.Children.Add(BuildBranchHeader());

            // Action buttons row
            root.Children.Add(BuildActionButtons());

            root.Children.Add(MakeSeparator());

            bool noGitWrite = _getNoGitWrite();

            if (noGitWrite && _uncommittedChanges.Count > 0)
            {
                // NoGitWrite mode: show uncommitted changes with encouragement to commit
                root.Children.Add(BuildCommitEncouragementPanel());
            }

            // Unpushed commits section
            root.Children.Add(BuildUnpushedCommitsSection());

            if (!noGitWrite && _uncommittedChanges.Count > 0)
            {
                root.Children.Add(MakeSeparator());
                root.Children.Add(BuildUncommittedChangesSection());
            }

            return root;
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

            return new Border
            {
                Background = BrushCache.Theme("BgSection"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 0),
                Child = panel
            };
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

            if (_unpushedCommits.Count > 0 && HasNoFileLocks())
            {
                // Push All button
                var pushAllBtn = MakeActionButton("\uE898", "Push All", "Push all unpushed commits to remote");
                pushAllBtn.Click += async (_, _) => await ExecutePushAsync();
                panel.Children.Add(pushAllBtn);
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
                    BorderThickness = new Thickness(1),
                    BorderBrush = BrushCache.Theme("BgElevated"),
                    Padding = new Thickness(8, 6, 8, 6),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
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

            // Push Selected button
            if (HasNoFileLocks() && commitCheckboxes.Count > 0)
            {
                var pushSelectedBtn = MakeActionButton("\uE898", "Push Selected", "Push only selected commits (interactive rebase)");
                pushSelectedBtn.Margin = new Thickness(0, 8, 0, 0);
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

            section.Children.Add(new TextBlock
            {
                Text = $"Uncommitted Changes ({_uncommittedChanges.Count})",
                Foreground = BrushCache.Theme("TextLight"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            section.Children.Add(BuildUncommittedFilesList());

            return section;
        }

        private StackPanel BuildUncommittedFilesList()
        {
            var list = new StackPanel();
            foreach (var change in _uncommittedChanges.Take(50)) // Cap at 50 to avoid UI lag
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };

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

            if (_uncommittedChanges.Count > 50)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"... and {_uncommittedChanges.Count - 50} more files",
                    Foreground = BrushCache.Theme("TextMuted"),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(8, 4, 0, 0)
                });
            }

            return list;
        }

        // ── Git Operations ─────────────────────────────────────────────

        private async Task ExecuteGetLatestAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            if (!HasNoFileLocks())
            {
                MessageBox.Show(
                    "Cannot fetch while file locks are active. Wait for running tasks to complete first.",
                    "Get Latest", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                AppLogger.Info("GitPanelManager", $"Fetching latest for {projectPath}");

                // Fetch first
                var fetchResult = await _gitHelper.RunGitCommandAsync(projectPath, "fetch --prune");
                if (fetchResult == null)
                {
                    MessageBox.Show("Failed to fetch from remote. Check your network connection and credentials.",
                        "Get Latest", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Pull with rebase to keep history clean
                var pullResult = await _gitHelper.RunGitCommandAsync(projectPath, "pull --rebase");
                if (pullResult == null)
                {
                    // Pull failed - might have conflicts
                    MessageBox.Show(
                        "Pull failed. There may be conflicts or uncommitted changes preventing the merge.\n\n" +
                        "Try committing or stashing your changes first.",
                        "Get Latest", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppLogger.Info("GitPanelManager", $"Get Latest completed: {pullResult}");
                MarkDirty();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GitPanelManager", "Get Latest failed", ex);
                MessageBox.Show($"Get Latest failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePushAsync()
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            if (!HasNoFileLocks())
            {
                MessageBox.Show(
                    "Cannot push while file locks are active. Wait for running tasks to complete first.",
                    "Push", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                AppLogger.Info("GitPanelManager", $"Pushing all for {projectPath}");
                var result = await _gitHelper.RunGitCommandAsync(projectPath, "push");
                if (result == null)
                {
                    MessageBox.Show(
                        "Push failed. The remote may have new changes.\n\nTry 'Get Latest' first, then push again.",
                        "Push", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppLogger.Info("GitPanelManager", "Push completed successfully");
                MarkDirty();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GitPanelManager", "Push failed", ex);
                MessageBox.Show($"Push failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecutePushUpToAsync(string commitHash)
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(_currentBranch)) return;

            if (!HasNoFileLocks())
            {
                MessageBox.Show(
                    "Cannot push while file locks are active. Wait for running tasks to complete first.",
                    "Push Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                AppLogger.Info("GitPanelManager", $"Pushing up to {commitHash} for {projectPath}");
                var result = await _gitHelper.RunGitCommandAsync(projectPath,
                    $"push {_remoteName} {commitHash}:{_currentBranch}");
                if (result == null)
                {
                    MessageBox.Show(
                        "Push failed. The remote may have new changes.\n\nTry 'Get Latest' first, then push again.",
                        "Push Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppLogger.Info("GitPanelManager", "Selective push completed successfully");
                MarkDirty();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GitPanelManager", "Selective push failed", ex);
                MessageBox.Show($"Push failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ExecuteCommitAllAsync(string message)
        {
            var projectPath = _getProjectPath();
            if (string.IsNullOrEmpty(projectPath)) return;

            if (!HasNoFileLocks())
            {
                MessageBox.Show(
                    "Cannot commit while file locks are active. Wait for running tasks to complete first.",
                    "Commit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                AppLogger.Info("GitPanelManager", $"Committing all changes for {projectPath}");

                // Stage all changes
                var addResult = await _gitHelper.RunGitCommandAsync(projectPath, "add -A");
                if (addResult == null)
                {
                    MessageBox.Show("Failed to stage changes.", "Commit", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Commit with the message - escape double quotes in message
                var escapedMessage = message.Replace("\"", "\\\"");
                var commitResult = await _gitHelper.RunGitCommandAsync(projectPath,
                    $"commit -m \"{escapedMessage}\"");
                if (commitResult == null)
                {
                    MessageBox.Show("Commit failed. There may be nothing to commit or a hook rejected it.",
                        "Commit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AppLogger.Info("GitPanelManager", $"Commit completed: {commitResult}");
                MarkDirty();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GitPanelManager", "Commit failed", ex);
                MessageBox.Show($"Commit failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            return new Button
            {
                Content = stack,
                ToolTip = tooltip,
                Foreground = BrushCache.Theme("TextLight"),
                Background = BrushCache.Theme("BgElevated"),
                BorderThickness = new Thickness(1),
                BorderBrush = BrushCache.Theme("BorderMedium"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
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
