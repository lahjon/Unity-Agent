using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Spritely.Dialogs;
using Spritely.Models;
using Spritely.Services;

namespace Spritely.Managers
{
    /// <summary>
    /// Manages the Git tab in the Statistics panel.
    /// Shows unpushed commits, uncommitted changes, and provides push/fetch operations.
    /// Respects the file lock system.
    /// </summary>
    public partial class GitPanelManager
    {
        private readonly IGitHelper _gitHelper;
        private readonly Func<string> _getProjectPath;
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

        private readonly SettingsManager _settingsManager;

        public GitPanelManager(
            IGitHelper gitHelper,
            Func<string> getProjectPath,
            FileLockManager fileLockManager,
            GitOperationGuard gitOperationGuard,
            Dispatcher dispatcher,
            SettingsManager settingsManager)
        {
            _gitHelper = gitHelper;
            _getProjectPath = getProjectPath;
            _fileLockManager = fileLockManager;
            _gitOperationGuard = gitOperationGuard;
            _dispatcher = dispatcher;
            _settingsManager = settingsManager;
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

                if (!gitCheck.IsSuccess || gitCheck.Output.Trim() != "true")
                {
                    _gitAvailable = false;
                    _lastError = gitCheck.IsSuccess ? "Not a git repository" : gitCheck.GetErrorMessage();
                    _uiCacheValid = false; // Invalidate cache when git becomes unavailable
                    _dispatcher.Invoke(() => container.Content = BuildContent());
                    return;
                }

                _gitAvailable = true;
                _lastError = null;

                // Track previous branch to detect changes
                var previousBranch = _currentBranch;

                // Get current branch
                var branchResult = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse --abbrev-ref HEAD", ct);
                if (ct.IsCancellationRequested) return;
                _currentBranch = branchResult.TrimmedOutput;

                // Invalidate cache if branch changed
                if (previousBranch != _currentBranch)
                {
                    _uiCacheValid = false;
                }

                // Get remote name
                var remoteResult = await _gitHelper.RunGitCommandAsync(projectPath, "remote", ct);
                if (ct.IsCancellationRequested) return;
                _remoteName = remoteResult.IsSuccess && !string.IsNullOrEmpty(remoteResult.Output)
                    ? remoteResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                    : null;

                // Get unpushed commits
                var previousUnpushedCount = _unpushedCommits.Count;
                await LoadUnpushedCommitsAsync(projectPath, ct);
                if (ct.IsCancellationRequested) return;

                // Get uncommitted changes
                var previousUncommittedCount = _uncommittedChanges.Count;
                await LoadUncommittedChangesAsync(projectPath, ct);
                if (ct.IsCancellationRequested) return;

                // Invalidate cache if change counts changed (buttons need rebuilding)
                if (_uncommittedChanges.Count != previousUncommittedCount ||
                    _unpushedCommits.Count != previousUnpushedCount)
                {
                    _uiCacheValid = false;
                }

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
            if (!trackingRef.IsSuccess) return; // No remote tracking branch

            var logResult = await _gitHelper.RunGitCommandAsync(projectPath,
                $"log {_remoteName}/{_currentBranch}..HEAD --format=%H|%h|%an|%ar|%s", ct);
            if (!logResult.IsSuccess || string.IsNullOrEmpty(logResult.Output)) return;

            foreach (var line in logResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

            var statusResult = await _gitHelper.RunGitCommandAsync(projectPath,
                "status --porcelain", ct);
            if (!statusResult.IsSuccess || string.IsNullOrEmpty(statusResult.Output)) return;

            foreach (var line in statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

                // Force immediate UI update to show the fetching status
                if (scrollViewer != null)
                {
                    await _dispatcher.InvokeAsync(async () =>
                    {
                        await RefreshAsync(scrollViewer);
                    });

                    // Give UI time to render the status message
                    await Task.Delay(100);
                }

                // Get current HEAD before fetch
                var headBeforeResult = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse HEAD");
                var headBefore = headBeforeResult.TrimmedOutput;

                // Fetch first
                var fetchResult = await _gitHelper.RunGitCommandAsync(projectPath, "fetch --prune", TimeSpan.FromSeconds(120));
                if (!fetchResult.IsSuccess)
                {
                    _lastOperationStatus = $"Fetch failed: {fetchResult.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException($"Failed to fetch from remote: {fetchResult.GetErrorMessage()}");
                }

                // Check if remote branch has new commits
                var remoteBranch = $"{_remoteName}/{_currentBranch}";
                var behindCountResult = await _gitHelper.RunGitCommandAsync(projectPath, $"rev-list --count HEAD..{remoteBranch}");
                var behind = behindCountResult.IsSuccess && int.TryParse(behindCountResult.Output.Trim(), out var count) ? count : 0;

                if (behind == 0)
                {
                    _lastOperationStatus = "Already up to date - no changes found";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", "Get Latest: already up to date");
                }
                else
                {
                    // Pull with rebase to keep history clean
                    var pullResult = await _gitHelper.RunGitCommandAsync(projectPath, "pull --rebase", TimeSpan.FromSeconds(120));
                    if (!pullResult.IsSuccess)
                    {
                        _lastOperationStatus = $"Pull failed: {pullResult.GetErrorMessage()}";
                        _lastOperationTime = DateTime.Now;
                        throw new InvalidOperationException($"Pull failed: {pullResult.GetErrorMessage()}");
                    }
                    else
                    {
                        // Get current HEAD after pull
                        var headAfterResult = await _gitHelper.RunGitCommandAsync(projectPath, "rev-parse HEAD");
                        var headAfter = headAfterResult.TrimmedOutput;

                        if (headBefore != headAfter)
                        {
                            _lastOperationStatus = $"Successfully pulled {behind} new commit{(behind != 1 ? "s" : "")}";
                        }
                        else
                        {
                            _lastOperationStatus = "Pull completed, but no new commits were applied";
                        }
                        _lastOperationTime = DateTime.Now;
                        AppLogger.Info("GitPanelManager", $"Get Latest completed: {pullResult.Output}");
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
                if (!result.IsSuccess)
                {
                    _lastOperationStatus = $"Failed to initialize git repository: {result.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException($"Git init command failed: {result.GetErrorMessage()}");
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

                // Force immediate UI update to show the pushing status
                if (_dispatcher != null)
                {
                    await _dispatcher.InvokeAsync(async () =>
                    {
                        // Find the parent ScrollViewer
                        ScrollViewer? scrollViewer = null;
                        if (_cachedRoot?.Parent is ScrollViewer sv)
                        {
                            scrollViewer = sv;
                        }
                        else
                        {
                            // Try to find it in the visual tree
                            var parent = _cachedRoot?.Parent as DependencyObject;
                            while (parent != null && !(parent is ScrollViewer))
                            {
                                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                            }
                            scrollViewer = parent as ScrollViewer;
                        }

                        if (scrollViewer != null)
                        {
                            await RefreshAsync(scrollViewer);
                        }
                    });

                    // Give UI time to render the status message
                    await Task.Delay(100);
                }

                var result = await _gitHelper.RunGitCommandAsync(projectPath, "push", TimeSpan.FromSeconds(120));
                if (!result.IsSuccess)
                {
                    _lastOperationStatus = $"Push failed: {result.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException($"Push failed: {result.GetErrorMessage()}");
                }
                else
                {
                    _lastOperationStatus = $"Successfully pushed {_unpushedCommits.Count} commit{(_unpushedCommits.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", "Push completed successfully");
                }

                MarkDirty();

                // Force refresh after successful push
#pragma warning disable CS8602 // InvokeAsync always returns non-null DispatcherOperation
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (_cachedRoot is not null && _cachedRoot.Parent is ScrollViewer sv)
                    {
                        await RefreshAsync(sv);
                    }
                });
#pragma warning restore CS8602
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
            // Count how many commits we're pushing
            var commitIndex = _unpushedCommits.FindIndex(c => c.FullHash == commitHash);
            var commitsToPush = commitIndex >= 0 ? _unpushedCommits.Count - commitIndex : 0;

            // Show pushing status
            _lastOperationStatus = $"Pushing {commitsToPush} selected commit{(commitsToPush != 1 ? "s" : "")} to remote...";
            _lastOperationTime = DateTime.Now;
            MarkDirty();

            // Force immediate UI update
            await _dispatcher.InvokeAsync(async () =>
            {
                if (_cachedRoot?.Parent is ScrollViewer sv)
                {
                    await RefreshAsync(sv);
                }
            });

            AppLogger.Info("GitPanelManager", $"Pushing up to {commitHash} for {projectPath}");
            var result = await _gitHelper.RunGitCommandAsync(projectPath,
                $"push {_remoteName} {commitHash}:{_currentBranch}", TimeSpan.FromSeconds(120));
            if (!result.IsSuccess)
            {
                _lastOperationStatus = $"Push failed: {result.GetErrorMessage()}";
                _lastOperationTime = DateTime.Now;
                throw new InvalidOperationException($"Push failed: {result.GetErrorMessage()}\n\nTry 'Get Latest' first, then push again.");
            }

            AppLogger.Info("GitPanelManager", "Selective push completed successfully");
            _lastOperationStatus = $"Successfully pushed {commitsToPush} commit{(commitsToPush != 1 ? "s" : "")}";
            _lastOperationTime = DateTime.Now;
            MarkDirty();

            // Force refresh after successful push
            await _dispatcher.InvokeAsync(async () =>
            {
                if (_cachedRoot?.Parent is ScrollViewer sv)
                {
                    await RefreshAsync(sv);
                }
            });
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
                if (!addResult.IsSuccess)
                {
                    _lastOperationStatus = $"Failed to stage changes: {addResult.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException($"Failed to stage changes: {addResult.GetErrorMessage()}");
                }

                // Use the secure commit method to prevent shell injection
                var commitResult = await _gitHelper.CommitSecureAsync(projectPath, message, pathArgs);
                if (!commitResult.IsSuccess)
                {
                    _lastOperationStatus = $"Commit failed: {commitResult.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException($"Commit failed: {commitResult.GetErrorMessage()}");
                }
                else
                {
                    _lastOperationStatus = $"Successfully committed {filePaths.Count} file{(filePaths.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                    AppLogger.Info("GitPanelManager", $"Commit completed: {commitResult}");
                }

                MarkDirty();

                // Force refresh after successful commit
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (_cachedRoot?.Parent is ScrollViewer sv)
                    {
                        await RefreshAsync(sv);
                    }
                });
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

                // Force immediate UI update to show the committing status
                await _dispatcher.InvokeAsync(async () =>
                {
                    if (_cachedRoot?.Parent is ScrollViewer sv)
                    {
                        await RefreshAsync(sv);
                    }
                });

                // Give UI time to render the status message
                await Task.Delay(100);

                // Stage only the selected files
                var filePaths = selectedChanges
                    .Select(c => GitHelper.EscapeGitPath(c.FilePath))
                    .ToList();
                var pathArgs = string.Join(" ", filePaths);

                AppLogger.Info("GitPanelManager", $"Committing {filePaths.Count} selected file(s) for {projectPath}");

                var addResult = await _gitHelper.RunGitCommandAsync(projectPath, $"add -- {pathArgs}");
                if (!addResult.IsSuccess)
                {
                    _lastOperationStatus = $"Failed to stage selected changes: {addResult.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    MarkDirty();
                    throw new InvalidOperationException($"Failed to stage selected changes: {addResult.GetErrorMessage()}");
                }

                // Use the secure commit method to prevent shell injection
                var commitResult = await _gitHelper.CommitSecureAsync(projectPath, result.Message, pathArgs);
                if (!commitResult.IsSuccess)
                {
                    _lastOperationStatus = $"Commit failed: {commitResult.GetErrorMessage()}";
                    _lastOperationTime = DateTime.Now;
                    throw new InvalidOperationException($"Commit failed: {commitResult.GetErrorMessage()}");
                }
                else
                {
                    _lastOperationStatus = $"Successfully committed {filePaths.Count} selected file{(filePaths.Count != 1 ? "s" : "")}";
                    _lastOperationTime = DateTime.Now;
                }

                MarkDirty();

                // Force refresh after successful commit
                await _dispatcher.InvokeAsync(async () =>
                {
                    _selectedFiles.Clear(); // Clear selected files after commit
                    if (_cachedRoot?.Parent is ScrollViewer sv)
                    {
                        await RefreshAsync(sv);
                    }
                });
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
