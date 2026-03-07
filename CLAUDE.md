# RULES
- **You are an AI pair programmer working on the Spritely project itself.**
- **Never introduce secrets** (API keys, tokens, passwords) into the repository. Use environment variables or `%LOCALAPPDATA%\Spritely\`
- **Keep UI responsive**: prefer `async`/`await`, avoid blocking the WPF dispatcher thread, and respect existing cancellation / pause / queue semantics.
- **Write safe, reversible Git workflows**. Do not bypass `GitHelper`, `GitPanelManager`, or `GitOperationGuard` when changing Git behavior.
- **Avoid adding temporary files in the project** Avoid creating temporary .md, .bat or .ps1 files for quick. If required, add too `./temp`
- **NEVER read or search in ./temp** This folder is only for temp files that should not be tracked
- **NEVER read or search in .spritely/templates/** These are per-project saved task templates managed by the app

---

## Project overview

- **Name**: Spritely
- **Type**: .NET 9 WPF desktop application (WinExe, Windows-only) with a small WinForms dependency for the tray icon.
- **Purpose**: Spritely is a multi-task AI coding assistant and workflow orchestrator. It:
  - Manages long-running Claude / Gemini tasks against local projects.
  - Tracks per-project descriptions and rules, especially for Unity game projects.
  - Integrates with Git for safe commit/push flows and status visualization.
  - Integrates with MCP-for-Unity and the Unity Editor for in-editor automation.
  - Provides diagnostics, logging, and dashboards for task groups and activity.

---

## Tech stack and structure

- **Languages & frameworks**
  - C# targeting **.NET 9**.
  - **WPF** for the main UI (`App.xaml`, `MainWindow.xaml` and its partial classes).
  - WinForms `NotifyIcon` for the system tray.
  - **xUnit** test project (`Spritely.Tests`) for behavior and regression tests.

- **Key assemblies**
  - `Spritely`: main WPF app, all managers, models, prompts, and UI.
  - `Spritely.Tests`: xUnit tests for task orchestration, session cleanup, Git & prompt behavior, etc.

- **High-level layout (conventions to follow)**
  - `MainWindow*.cs` partials: UI orchestration and event wiring, kept relatively thin.
  - `Managers/*`: core logic and services (task execution, prompts, history, Git, MCP, logging, skills).
  - `Models/*`: data models and simple DTOs (tasks, history entries, projects, stats).
  - `Prompts/*`: Markdown system prompts and specialized prompt blocks with their own `RULES`.
  - `Themes/*`: WPF styles, templates, and color resources.
  - `Dialogs/*` and `Controls/*`: self-contained UI components and windows.
  - `Constants/AppConstants.cs`: central “magic-number” and config constants.

When adding new functionality, prefer extending these existing areas over inventing new patterns.

- **Log and crash files** (`%LOCALAPPDATA%\Spritely\logs\`)
  - `crash.log`: unhandled-exception crash logs (written by `App.xaml.cs`).
  - `hang.log`: hang/deadlock detection logs (written by `App.xaml.cs`).
  - `app.log`: general application log (written by `AppLogger`).
  - Per-project crash log path is configurable via `ProjectEntry.CrashLogPath` (defaults to `crash.log` above).

---

## Architectural patterns and responsibilities

- **Managers pattern**
  - Each major concern is encapsulated in a `*Manager`:
    - `TaskExecutionManager`, `TaskFactory`, `TaskOrchestrator`: creating, launching, and orchestrating AI tasks and task dependency graphs.
    - `PromptBuilder`, `PromptLoader`, `ContextReducer`, `ContextDeduplicationService`, `IterationMemoryManager`: constructing prompts, optimizing context, and managing iteration memory.
    - `ProjectManager`: per-project metadata, project tasks, project rules, and MCP configuration.
    - `GitPanelManager`, `GitHelper`, `GitOperationGuard`, `FileLockManager`: Git operations, UI, and file-lock safety.
    - `ClaudeService`, `GeminiService`, `BaseLlmService`, `ClaudeUsageManager`: LLM configuration and usage tracking.
    - `HistoryManager`, `TaskGroupTracker`, `ActivityDashboardManager`: tracking and surfacing historical activity and aggregates.
    - `McpHealthMonitor`: monitoring MCP-for-Unity health and restart logic.
    - `AppLogger`, `SafeFileWriter`: logging and durable file writes.
  - **Guideline**: new cross-cutting or domain logic belongs in a manager or model, not in event handlers or code-behind.

- **MainWindow as composition root**
  - `MainWindow` partials (`MainWindow.xaml.cs`, `.TaskExecution.cs`, `.Orchestration.cs`, `.Skills.cs`, `.SavedPrompts.cs`, etc.) act as the composition and wiring layer between the UI and manager classes.
  - **Guideline**: keep `MainWindow` focused on wiring, event handling, and data binding; put heavy logic into managers.

- **Prompt and rules composition**
  - System prompts and prompt blocks live under `Prompts/*.md` and typically start with a `# RULES` section.
  - Per-project rules and instructions are managed via `ProjectManager` and assembled into a `# PROJECT RULES` block (`GetProjectRulesBlock`).
  - MCP and game-project specific behavior is configured via dedicated prompt blocks (`McpPromptBlock.md`, `GameRulesBlock.md`, etc.).
  - **Guideline**: when changing how tasks are constructed or how prompts are built, integrate with `PromptBuilder` and existing prompt files, instead of hardcoding large strings inline.

- **Task lifecycle**
  - Tasks are represented by models like `AgentTask`, `AgentTaskData`, `TaskHistoryEntry`, and `StoredTaskEntry`.
  - The lifecycle includes creation, queuing (due to file locks or dependencies), execution, pause/unpause, auto-recovery, history, and optional auto-commit.
  - Queueing, pause/unpause, and process reuse are carefully tuned (see `docs/pause-unpause-fix-summary.md`) to preserve Claude conversation IDs and avoid killing processes unnecessarily.
  - **Guideline**: any change to task states or queue semantics should go through `TaskOrchestrator`, `TaskExecutionManager`, and `FileLockManager` instead of adding custom state machines elsewhere.

---

## Critical invariants to preserve

- **File and Git safety**
  - File locks and task queueing are coordinated via `FileLockManager`. Do not bypass it when allowing tasks or Git operations to modify files.
  - All Git operations should flow through `GitHelper` and be orchestrated by `GitPanelManager` + `GitOperationGuard`.
  - Behavior like “no Git operations while any file locks are active” is intentional; do not weaken it without a very strong reason and clear tests.
  - Git commits should stick to the existing patterns (staging known files, no all-encompassing `git add -A` from code).

- **Task lifecycle & pause/unpause**
  - Pausing and queueing are designed to **preserve conversations and processes** where possible. Avoid introducing code that kills and restarts processes unnecessarily.

- **Theming and UI consistency**
  - The color and theming is implemented in `Themes/Colors.xaml`
  - New UI elements should prefer existing semantic brush keys (e.g., for backgrounds, borders, text, error/success) instead of introducing new raw hex color strings.
  - There are intentional exceptions (e.g., project color palettes in `ProjectManager.cs`, `AgentTask.StatusColor` strings, some `ColorAnimation` storyboard values); keep those unless you are explicitly working on the theming plan.

- **Configuration and constants**
  - Global tunables and identifiers (e.g., model IDs, timeouts) live in `AppConstants` or other dedicated constants classes.
  - Avoid scattering new magic numbers or model strings; add them to `AppConstants` and reference from there.

---

## Coding guidelines for this repo

- **Style and language**
  - Use idiomatic modern C# for .NET 9 (nullability annotations, `async`/`await`, expression-bodied members where appropriate).
  - Follow existing naming conventions for methods, properties, and private fields.
  - Keep methods cohesive and reasonably short; extract private helpers or new classes when logic becomes complex.

- **Class and file size**
  - Prefer small, focused classes with a single responsibility over large monolithic files.
  - When adding new features, create dedicated classes/files rather than expanding existing large files.
  - If a class grows beyond ~300 lines, consider splitting it into smaller, composable pieces (e.g., extract a helper class, a dedicated service, or a partial class).

- **Where to put new code**
  - New app-wide behavior: usually a new or extended manager under `Managers/`.
  - New data shape or persisted entity: add/extend types in `Models/`.
  - New prompts or reusable instruction blocks: add or edit files under `Prompts/`.
  - New UI elements or dialogs: place them in `Dialogs/` or `Controls/` and wire them via XAML + code-behind.
  - New constants: add to `AppConstants` or another dedicated constants file.

- **Testing**
  - When changing task orchestration, queueing, or pause/unpause behavior, add or update tests in `Spritely.Tests` (e.g., `TaskOrchestratorTests`, `TaskLauncherTests`, `SessionDataCleanupTests`, `ProjectSwapTests`).
  - Prefer tests that exercise public behavior through managers rather than testing private implementation details.

- **Comments**
  - Avoid comments that merely restate what the code clearly does.
  - Do add comments that capture **rationale, trade-offs, invariants, and constraints** (for example, why a particular timeout is chosen or why a special-case branch exists).

---

## How to behave when unsure

- If you are uncertain about an intended behavior:
  - Prefer a **conservative change** that clearly preserves existing semantics.
  - Suggest **small, incremental steps** that can be tested easily (including which tests to run or add).

- When proposing larger refactors:
  - Start with a brief architectural outline and call out which managers, models, and prompts will be touched.
  - Ensure you preserve the critical invariants listed above (file and Git safety, prompt composition, task lifecycle, theming, configuration).

