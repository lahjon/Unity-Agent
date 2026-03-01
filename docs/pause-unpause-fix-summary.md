# Pause/Unpause Conversation Preservation Fix

## Issue
When tasks were paused and then unpaused (especially from the queue), they were starting new conversations instead of resuming the existing ones. This broke the continuity of the Claude conversation.

## Root Causes
1. **OnQueuedTaskResumed always started a new process**: The method called `StartProcess` unconditionally, creating a new conversation
2. **File lock conflicts killed processes**: When encountering file locks, processes were terminated instead of paused
3. **CheckQueuedTasks reset StartTime**: When resuming queued tasks, the StartTime was reset, indicating a fresh start

## Solutions Implemented

### 1. Modified OnQueuedTaskResumed (MainWindow.Orchestration.cs)
```csharp
// Check if we have an existing process that was paused
if (task.Process is { HasExited: false })
{
    // Resume the existing process
    _taskExecutionManager.ResumeTask(task, _activeTasks, _historyTasks);
}
else
{
    // No existing process, start a new one
    _ = _taskExecutionManager.StartProcess(task, _activeTasks, _historyTasks, MoveToHistory);
}
```

### 2. Changed File Lock Handling (FileLockManager.cs)
- Added `TaskNeedsPause` event to signal when a task should be paused
- Removed the `KillProcess` call that was terminating processes
- Process is now paused via the TaskExecutionManager instead

### 3. Added Debug Logging
- Added AppLogger calls to track conversation ID preservation
- Logs when resuming existing vs starting new processes

## Key Files Modified
1. `MainWindow.Orchestration.cs` - Modified OnQueuedTaskResumed and added OnTaskNeedsPause
2. `FileLockManager.cs` - Changed HandleFileLockConflictInternal to preserve processes
3. `MainWindow.xaml.cs` - Added event subscription for TaskNeedsPause

## Testing
The main project builds successfully with these changes. Manual testing should verify:
1. Pause a running task and unpause it - conversation should continue
2. Queue a task due to file lock, then let it resume - conversation should continue
3. Conversation ID should be preserved throughout pause/unpause cycles
4. Follow-up messages should use `--resume` with the conversation ID

## Future Considerations
- Add comprehensive unit tests for pause/unpause scenarios
- Consider adding UI indicators showing when a task has a preserved conversation
- Monitor for edge cases where processes might exit during pause state