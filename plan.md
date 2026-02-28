# Plan: Task Output Search and Filtering

## Overview
Add Ctrl+F search bar overlay and content-type filter toggles to each output tab.

## Files Changed

| File | Type | Description |
|------|------|-------------|
| `Models/OutputSegment.cs` | **NEW** | Enum + model for categorized output chunks |
| `AgentTask.cs` | Modified | Add `OutputSegments` list alongside `OutputBuilder` |
| `Managers/OutputTabManager.cs` | Major | Search overlay UI, filter bar, search/filter logic, enhanced `AppendOutput` |
| `Managers/TaskExecutionManager.cs` | Modified | Tag every `AppendOutput` call with correct segment type |

---

## Step 1: Create `Models/OutputSegment.cs`

New file with:
```csharp
public enum OutputSegmentType { AssistantText, ToolCall, Thinking, Error, Status, Result, Other }
public class OutputSegment { string Text; OutputSegmentType Type; }
```

## Step 2: Add segment storage to `AgentTask.cs`

Add one property:
```csharp
public List<OutputSegment> OutputSegments { get; } = new();
```
This stores categorized output alongside the existing `OutputBuilder` StringBuilder. Both are populated on every append.

## Step 3: Enhance `OutputTabManager.AppendOutput`

Currently `AppendOutput` does `box.AppendText(text)` as plain text. Change to:

- **New overload** accepting `OutputSegmentType segmentType`
- Creates a `Run` element with `Tag = segmentType` and type-specific foreground color
- Appends Run to the FlowDocument's last Paragraph (or creates one)
- Stores segment in `task.OutputSegments`
- If segment type is currently filtered out, skips adding Run to document
- If search is active, incrementally checks new text for matches

**Color mapping** (using existing theme palette):
- AssistantText: `#B0B0B0` (default light gray)
- ToolCall: `#999999` (TextSecondary - dimmer)
- Thinking: `#666666` (TextMuted)
- Error: `#A15252` (Danger red)
- Status: `#DA7756` (Accent orange)
- Result: `#5CB85C` (Success green)
- Other: `#B0B0B0` (default)

The existing `AppendColoredOutput` method stays untouched.

## Step 4: Tag calls in `TaskExecutionManager`

Every `AppendOutput(...)` call gets a segment type parameter. Mapping:

| Call site | Segment Type |
|-----------|-------------|
| `[UnityAgent]` prefixed lines (StartProcess, etc.) | `Status` |
| `[stderr]` lines (process.ErrorDataReceived) | `Error` |
| `text_delta` content blocks | `AssistantText` |
| `tool_use` / FormatToolAction output | `ToolCall` |
| `thinking` / `thinking_delta` blocks | `Thinking` |
| `result` blocks | `Result` |
| `error` blocks | `Error` |
| Completion messages | `Status` |
| Everything else / fallback | `Other` |

~30 call sites to update (mechanical - add one enum argument each).

## Step 5: Build filter bar UI in `CreateTab()`

Add a horizontal `StackPanel` (Dock.Top) with toggle buttons:

```
[Text ✓] [Tools ✓] [Errors ✓] [Status ✓]
```

- Small pill-shaped ToggleButtons, styled to match dark theme
- All checked by default (all content visible)
- Unchecking rebuilds the RichTextBox document from `task.OutputSegments`, skipping hidden types
- Rechecking does the same rebuild including those types again

**Filter state** tracked per tab in a new `Dictionary<string, OutputFilterState>`.

**Rebuild logic**: Clear FlowDocument → iterate `OutputSegments` → create Run for each visible segment → re-run search if active. Cost: ~50-100ms for 5000 segments, acceptable for a toggle action.

## Step 6: Build search overlay UI in `CreateTab()`

Wrap the RichTextBox in a `Grid` so the search bar can overlay on top:

```
Grid (fills remaining space)
  ├── RichTextBox (output)
  └── Border (searchOverlay, Collapsed, VerticalAlign=Top, HAlign=Stretch)
        └── DockPanel
              ├── Button "✕" (Dock.Right) — close search
              ├── Button "▼" (Dock.Right) — next match
              ├── Button "▲" (Dock.Right) — prev match
              ├── TextBlock "0 of 0" (Dock.Right) — match count
              └── TextBox (searchInput, fills rest)
```

Styled with `BgElevated` (#2C2C2C) background, `BorderSubtle` (#333333) border, 6px corner radius.

**Keyboard shortcuts**:
- `Ctrl+F` on RichTextBox → show search bar, focus input
- `Escape` in search input → hide search bar, clear highlights
- `Enter` in search input → next match
- `Shift+Enter` → previous match

## Step 7: Implement search logic

**Search state** per tab in `Dictionary<string, OutputSearchState>`:
```csharp
class OutputSearchState {
    string Query;
    List<TextRange> Matches;
    int CurrentMatchIndex;
}
```

**ExecuteSearch**:
1. Get full document text via `TextRange(doc.ContentStart, doc.ContentEnd).Text`
2. Find all occurrences (case-insensitive `IndexOf`)
3. Convert string offsets to `TextPointer` positions by walking the FlowDocument inline tree
4. Apply yellow-ish background (`#6B5B1A`) to all match `TextRange`s
5. Apply accent background (`#DA7756`) to the current match
6. Scroll current match into view

**Debounce**: 300ms `DispatcherTimer` on search input TextChanged, so search only fires after user stops typing.

**Match navigation**: GoToNext/GoToPrev cycle through matches, updating the current-match highlight and scrolling.

**Incremental search on streaming**: When new output appends and search is active, scan only the new text for matches and append to the match list (O(1) per append vs O(n) full re-scan).

## Step 8: Cleanup

In `CloseTab()`, clean up the new dictionaries:
```csharp
_searchOverlays.Remove(task.Id);
_searchInputs.Remove(task.Id);
_searchStates.Remove(task.Id);
_filterStates.Remove(task.Id);
```

---

## New tab content layout (final)

```
DockPanel (tab content)
  ├── DockPanel (inputPanel, Dock.Bottom)
  │     ├── Button "Send" (Dock.Right)
  │     └── TextBox (inputBox)
  ├── StackPanel (filterBar, Dock.Top, Horizontal)
  │     ├── ToggleButton "Text"
  │     ├── ToggleButton "Tools"
  │     ├── ToggleButton "Errors"
  │     └── ToggleButton "Status"
  └── Grid (fills rest)
        ├── RichTextBox (outputBox)
        └── Border (searchOverlay, Collapsed, top-aligned)
```

## Performance Summary

| Operation | Cost | Mitigation |
|-----------|------|------------|
| Streaming append | O(1) | Direct Run creation |
| Full search | O(n) | 300ms debounce |
| Filter toggle | O(segments) | One-time rebuild ~50-100ms |
| Incremental search | O(new text length) | Only scans appended text |
| Match navigation | O(1) | Direct TextRange highlight |
