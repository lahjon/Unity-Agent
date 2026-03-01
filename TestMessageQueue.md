# Testing Message Queue Functionality

## Overview
This document describes how to test the new message queue functionality that allows messages to be appended mid-task and queued for processing when the task is ready.

## Implementation Summary
The message queue system ensures that messages sent while a Claude task is busy (e.g., during tool execution) are properly queued and delivered when Claude is ready for more input.

### Key Components:
1. **RuntimeTaskContext**: Added thread-safe message queue with:
   - `EnqueueMessage(string message)`
   - `DequeueMessage()`
   - `PendingMessageCount`
   - `IsProcessingMessage` flag

2. **TaskExecutionManager.SendFollowUp**: Modified to:
   - Check if task is busy (`IsProcessingMessage` or `HasToolActivity`)
   - Queue messages when busy
   - Mark task as processing when sending

3. **TaskProcessLauncher**: Modified to:
   - Detect when Claude is ready (`message_stop` event)
   - Clear processing flag and tool activity
   - Trigger queued message processing via callback

## Manual Testing Steps

### Test 1: Basic Message Queuing
1. Start a Claude task with a complex request that will use tools (e.g., "Create a new file test.txt with some content")
2. While Claude is executing tools (you'll see tool activity), quickly type a follow-up message and press Enter
3. You should see: `[Message queued - will be sent when task is ready]`
4. When Claude finishes, the queued message should be automatically sent

### Test 2: Multiple Queued Messages
1. Start a Claude task with: "Read the file README.md and tell me what it contains"
2. While Claude is reading the file, quickly send multiple messages:
   - "Also check if there are any TODO items"
   - "And count the number of lines"
   - "Finally, tell me the file size"
3. Each message should show as queued
4. When Claude finishes reading, the messages should be processed in order

### Test 3: Stress Test
1. Start a task that will take longer: "Search for all .cs files and list them"
2. Send 5-10 messages rapidly while the search is running
3. Verify all messages are queued and processed in the correct order

## Expected Behavior
- Messages sent while task is busy are queued with notification
- Queue count is logged: `Task is busy, queuing message. Queue size: N`
- When Claude becomes ready, log shows: `Claude is ready, processing N queued messages`
- Messages are processed in FIFO order
- Only one message is sent at a time to avoid overwhelming Claude

## Unit Tests
Run the unit tests to verify the implementation:
```bash
dotnet test HappyEngine.Tests\MessageQueueTests.cs
```

Tests verify:
- Message queue operations (enqueue/dequeue)
- Thread safety of queue operations
- Message queuing when task is busy
- Message processing when task becomes ready

## Troubleshooting
If messages are not being queued:
1. Check logs for `[{taskId}] Task is busy, queuing message`
2. Verify task has `HasToolActivity` or `IsProcessingMessage` set to true
3. Check for `message_stop` events in the output

If queued messages are not being sent:
1. Check logs for `Claude is ready, processing N queued messages`
2. Verify ProcessQueuedMessagesCallback is properly set
3. Ensure task process is still alive when messages are dequeued