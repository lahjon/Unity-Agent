using System;
using System.Threading;
using System.Threading.Tasks;
using HappyEngine.Helpers;
using HappyEngine.Managers;

namespace HappyEngine.Services
{
    /// <summary>
    /// Provides global synchronization for git operations across the application.
    /// Ensures that only one git write operation (commit, push, add, etc.) can occur at a time,
    /// and prevents file lock acquisitions during git operations.
    /// </summary>
    public class GitOperationGuard
    {
        private readonly SemaphoreSlim _gitOperationSemaphore = new(1, 1);
        private readonly FileLockManager _fileLockManager;
        private readonly object _stateLock = new();
        private bool _gitOperationInProgress;

        public GitOperationGuard(FileLockManager fileLockManager)
        {
            _fileLockManager = fileLockManager ?? throw new ArgumentNullException(nameof(fileLockManager));
        }

        /// <summary>
        /// Gets whether a git operation is currently in progress.
        /// </summary>
        public bool IsGitOperationInProgress
        {
            get
            {
                lock (_stateLock)
                {
                    return _gitOperationInProgress;
                }
            }
        }

        /// <summary>
        /// Executes an async git operation while ensuring exclusive access to git.
        /// This method:
        /// 1. Acquires a semaphore to prevent concurrent git operations
        /// 2. Sets a flag on FileLockManager to prevent new file lock acquisitions
        /// 3. Executes the provided operation
        /// 4. Releases the semaphore and clears the flag in a finally block
        /// </summary>
        public async Task<T> ExecuteGitOperationAsync<T>(
            Func<Task<T>> gitOperation,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            AppLogger.Info("GitOperationGuard", $"Acquiring lock for {operationName}");

            // Acquire the semaphore with cancellation support
            await _gitOperationSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Set the flag to prevent new file lock acquisitions
                lock (_stateLock)
                {
                    _gitOperationInProgress = true;
                }
                _fileLockManager.SetGitOperationInProgress(true);

                AppLogger.Info("GitOperationGuard", $"Executing {operationName}");

                // Execute the git operation
                return await gitOperation();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GitOperationGuard", $"{operationName} failed", ex);
                throw;
            }
            finally
            {
                // Always release the semaphore and clear the flag
                lock (_stateLock)
                {
                    _gitOperationInProgress = false;
                }
                _fileLockManager.SetGitOperationInProgress(false);
                _gitOperationSemaphore.Release();

                AppLogger.Info("GitOperationGuard", $"Released lock for {operationName}");
            }
        }

        /// <summary>
        /// Executes an async git operation with void return type.
        /// </summary>
        public async Task ExecuteGitOperationAsync(
            Func<Task> gitOperation,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            await ExecuteGitOperationAsync(async () =>
            {
                await gitOperation();
                return true;
            }, operationName, cancellationToken);
        }

        /// <summary>
        /// Executes a git operation while ensuring no file locks are held.
        /// This combines the functionality of FileLockManager.ExecuteWhileNoLocksHeldAsync
        /// with the global git operation mutex.
        /// </summary>
        public async Task<(bool success, string? errorMessage)> ExecuteWhileNoLocksHeldAsync(
            Func<Task> gitOperation,
            string operationName,
            CancellationToken cancellationToken = default)
        {
            // First acquire the semaphore to prevent race conditions
            await _gitOperationSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Check if any file locks are held inside the semaphore-protected region
                var lockCount = _fileLockManager.LockCount;
                if (lockCount > 0)
                {
                    // Release the semaphore before returning the error
                    _gitOperationSemaphore.Release();
                    return (false, $"Cannot {operationName} while file locks are active");
                }

                // We already have the semaphore, so call the inner ExecuteGitOperationAsync logic directly
                // to avoid double-acquiring the semaphore
                try
                {
                    // Set the flag to prevent new file lock acquisitions
                    lock (_stateLock)
                    {
                        _gitOperationInProgress = true;
                    }
                    _fileLockManager.SetGitOperationInProgress(true);

                    AppLogger.Info("GitOperationGuard", $"Executing {operationName}");

                    // Execute the git operation
                    await gitOperation();
                    return (true, null);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("GitOperationGuard", $"{operationName} failed", ex);
                    throw;
                }
                finally
                {
                    // Always clear the flag
                    lock (_stateLock)
                    {
                        _gitOperationInProgress = false;
                    }
                    _fileLockManager.SetGitOperationInProgress(false);

                    AppLogger.Info("GitOperationGuard", $"Released lock for {operationName}");
                }
            }
            catch (OperationCanceledException)
            {
                return (false, $"{operationName} was cancelled");
            }
            catch (Exception ex)
            {
                return (false, $"{operationName} failed: {ex.Message}");
            }
            finally
            {
                // Always release the semaphore (but only if we didn't already release it in the lock check)
                if (_gitOperationSemaphore.CurrentCount == 0)
                {
                    _gitOperationSemaphore.Release();
                }
            }
        }
    }
}