# RULES
- **AI pair programmer on the Spritely project itself.**
- **No secrets** in repo. Use env vars or `%LOCALAPPDATA%\Spritely\`.
- **Keep UI responsive**: `async`/`await`, don't block WPF dispatcher, respect cancellation/pause/queue.
- **Safe Git**: always use `GitHelper`, `GitPanelManager`, `GitOperationGuard`.
- **Temp files**: avoid .md/.bat/.ps1; if needed, use `./temp`.
- **NEVER read/search**: `./temp`, `.spritely/templates/`, `./docs/`, `bin/`, `obj/`, `publish/`, `.vs/`, `logs/`, `.agent-bus/`, `runtime/`

## Project overview
**Spritely**: .NET 9 WPF desktop app (WinExe, Windows-only) + WinForms tray icon. Multi-task AI coding assistant/orchestrator: Claude/Gemini tasks, per-project rules, Git integration, MCP-for-Unity, diagnostics.

## Tech stack & layout
- C#/.NET 9, WPF, xUnit (`Spritely.Tests`)
- `MainWindow*.cs`: thin UI wiring/events (partials)
- `Managers/*`: core logic | `Models/*`: DTOs | `Prompts/*`: system prompts (.md) | `Themes/*`: styles/colors
- `Dialogs/*`, `Controls/*`: UI components | `Constants/AppConstants.cs`: central constants
- Extend existing patterns; don't invent new ones.
- Logs: `%LOCALAPPDATA%\Spritely\logs\` (`crash.log`, `hang.log`, `app.log`). Per-project: `ProjectEntry.CrashLogPath`.

## Architecture
**Managers pattern** — one `*Manager` per concern:
- Task: `TaskExecutionManager`, `TaskFactory`, `TaskOrchestrator`
- Prompts: `PromptBuilder`, `PromptLoader`, `ContextReducer`, `ContextDeduplicationService`, `IterationMemoryManager`
- Project: `ProjectManager` | Git: `GitPanelManager`, `GitHelper`, `GitOperationGuard`, `FileLockManager`
- LLM: `ClaudeService`, `GeminiService`, `BaseLlmService`, `ClaudeUsageManager`
- History: `HistoryManager`, `TaskGroupTracker`, `ActivityDashboardManager`
- Other: `McpHealthMonitor`, `AppLogger`, `SafeFileWriter`

Logic in managers/models, not event handlers. **MainWindow** = composition root (wiring/events/binding only).

**Prompts**: `Prompts/*.md`. Per-project rules via `ProjectManager.GetProjectRulesBlock`. Use `PromptBuilder`, don't hardcode strings.

**Task lifecycle**: `AgentTask` → create → queue (file locks/deps) → execute → pause/unpause → history → auto-commit. Via `TaskOrchestrator`/`TaskExecutionManager`/`FileLockManager`.

## Critical invariants
- **File/Git safety**: Git ops through `GitHelper`+`GitPanelManager`/`GitOperationGuard`. File locks via `FileLockManager`. No Git ops while locks active. No `git add -A`.
- **Pause/unpause**: preserves conversations/processes. Don't kill/restart unnecessarily.
- **Theming**: semantic brush keys from `Themes/Colors.xaml` (exceptions: project palettes, `StatusColor`, `ColorAnimation`).
- **Constants**: tunables/IDs in `AppConstants`. No magic numbers.

## Coding guidelines
- Modern C#/.NET 9: nullability, `async`/`await`, expression-bodied members. Follow existing naming.
- Classes <~300 lines; split into helpers/services/partials as needed.
- **Placement**: behavior → `Managers/`, data → `Models/`, prompts → `Prompts/`, UI → `Dialogs/`/`Controls/`, constants → `AppConstants`.
- **Tests**: update `Spritely.Tests` for orchestration/queueing/pause changes. Test public behavior.
- **Comments**: rationale/trade-offs only, not restating code.

## Build verification
- Every task must `dotnet build` before completing.
- Fix your build errors. Note unrelated ones — unless last task running, then spawn "Build Test".

## When unsure
- Conservative changes preserving existing semantics.
- Larger refactors: outline architecture first, preserve critical invariants.
