# Feature System - Complete Implementation Plan

## 1. Problem Statement

Every Opus task currently starts blind — it must search the codebase to understand what exists before making changes. This wastes tokens (expensive at $15/M output) and time. Projects onboarded without Spritely have no structured context at all.

**Goal:** Build a hierarchical context management system that:
- Maintains a per-project registry of features with code signatures and relationships
- Automatically injects relevant context into Opus prompts so tasks start informed
- Self-updates after every task completion
- Is git-friendly (shared across developers, merge-safe)
- Pays for itself by reducing Opus exploration tokens

---

## 2. Cost Model — Why This Saves Money

### Current cost per task (typical Opus task)
| Phase | Model | Tokens | Cost |
|-------|-------|--------|------|
| Preprocessing | Haiku | ~2K in, ~500 out | $0.004 |
| Main execution | Opus | ~50K in, ~15K out | ~$1.05 |
| Verification | Haiku | ~2K in, ~500 out | $0.004 |
| **Total** | | | **~$1.06** |

A significant portion of the Opus input tokens are spent on file reads during the "understanding phase" — reading files to figure out what classes exist, what methods they have, and how they relate.

### With Feature System
| Phase | Model | Tokens | Cost |
|-------|-------|--------|------|
| Preprocessing | Haiku | ~2K in, ~500 out | $0.004 |
| **Feature resolution** | **Haiku** | **~3K in, ~800 out** | **$0.006** |
| Main execution | Opus | ~35K in, ~12K out | ~$0.73 |
| Verification | Haiku | ~2K in, ~500 out | $0.004 |
| **Feature update** | **Haiku** | **~3K in, ~800 out** | **$0.006** |
| **Total** | | | **~$0.75** |

**Estimated savings: ~30% per task** by front-loading ~2-3K tokens of structured context that replaces ~15K+ tokens of Opus file-reading exploration. The two extra Haiku calls cost $0.012 total — negligible.

### One-time initialization cost
| Phase | Model | Tokens | Cost |
|-------|-------|--------|------|
| Project scan | Sonnet | ~15K in, ~5K out | $0.12 |

---

## 3. Storage Design — Git-Friendly, Merge-Safe

### Directory structure (inside the target project repo)

```
{project_root}/
  .spritely/
    features/
      _index.json                    # lightweight manifest
      git-integration.json           # one file per feature
      task-orchestration.json
      prompt-building.json
      feature-mode.json
      ...
    .gitignore                       # ignore local-only caches
```

### Why one-file-per-feature?

| Scenario | Outcome |
|----------|---------|
| Two devs add different features | Auto-merge: different files + different lines in `_index.json` |
| Two devs modify the same feature | Conflict in one small `.json` file — easy to resolve |
| Dev adds feature + dev modifies existing | Auto-merge: different files |
| Two Spritely agents update same feature concurrently | Mutex-protected locally (same as `SafeFileWriter`) |

### .gitattributes entry (added by initializer)

```
.spritely/features/_index.json merge=union text eol=lf
.spritely/features/*.json text eol=lf
```

`merge=union` on the index means git keeps both sides for line-level conflicts — works well for sorted additive lists.

### Index file (`_index.json`)

```json
{
  "version": 1,
  "features": [
    { "id": "file-lock-management", "name": "File Lock Management" },
    { "id": "git-integration", "name": "Git Integration" },
    { "id": "task-orchestration", "name": "Task Orchestration" }
  ]
}
```

- Sorted alphabetically by `id` — new entries slot into predictable positions
- Only identity data — no fields that change frequently
- Minimal diff footprint

### Feature file (`git-integration.json`)

```json
{
  "id": "git-integration",
  "name": "Git Integration",
  "description": "Handles all git operations including commit, push, status, diff, and branch management through dedicated manager classes.",
  "category": "Core",
  "keywords": [
    "branch",
    "commit",
    "diff",
    "git",
    "merge",
    "push",
    "status"
  ],
  "primaryFiles": [
    "Managers/GitHelper.cs",
    "Managers/GitOperationGuard.cs",
    "Managers/GitPanelManager.cs"
  ],
  "secondaryFiles": [
    "Models/GitResult.cs",
    "Prompts/NoGitWriteBlock.md"
  ],
  "relatedFeatureIds": [
    "file-lock-management",
    "task-orchestration"
  ],
  "context": {
    "signatures": {
      "Managers/GitHelper.cs": {
        "hash": "a1b2c3d4e5f6",
        "content": "class GitHelper\n  CommitAsync(string message, List<string> files) -> Task<GitResult>\n  GetStatusAsync() -> Task<string>\n  GetDiffAsync(string? baseBranch) -> Task<string>\n  GetCurrentBranchAsync() -> Task<string>"
      },
      "Managers/GitPanelManager.cs": {
        "hash": "f7e8d9c0b1a2",
        "content": "class GitPanelManager\n  RefreshStatus() -> Task\n  ShowCommitDialog(List<string> files) -> void\n  IsOperationInProgress : bool"
      },
      "Managers/GitOperationGuard.cs": {
        "hash": "1234567890ab",
        "content": "class GitOperationGuard\n  AcquireAsync() -> Task<IDisposable>\n  Release() -> void"
      }
    },
    "keyTypes": [
      "class GitResult { int ExitCode, string Output, string Error }",
      "enum GitOperationResult { Success, Conflict, AuthFailed, LockFailed }"
    ],
    "patterns": [
      "All git operations flow through GitHelper — never raw Process calls",
      "GitOperationGuard prevents concurrent git operations on same repo",
      "NoGitWriteBlock is always injected — the auto-commit system handles writes"
    ],
    "dependencies": [
      "file-lock-management: Uses FileLockManager to coordinate file access during commits",
      "task-orchestration: Tasks are queued when git operations are active"
    ]
  },
  "touchCount": 5,
  "lastUpdatedAt": "2026-03-07T12:00:00Z",
  "lastUpdatedByTaskId": "a1b2c3d4e5f6g7h8"
}
```

### Serialization rules (enforced in code)

- Sorted keys via `JsonSerializerOptions` with `PropertyNamingPolicy = CamelCase`
- All arrays sorted alphabetically (keywords, files, etc.)
- LF line endings only (`\n`, not `\r\n`)
- 2-space indent
- No trailing whitespace
- Deterministic output — same data always produces identical JSON

### Signature hash for staleness detection

The `hash` field in each signature entry is a short hash of the file content. At task start:
1. Check if the file's current hash matches the stored hash
2. If mismatch → re-extract signatures for that file only (fast, local, no LLM)
3. Update the hash in the feature file

This ensures signatures stay fresh even when files change outside Spritely.

---

## 4. Data Models (new files in `Models/`)

### `FeatureEntry.cs`

```csharp
public class FeatureEntry
{
    public string Id { get; set; }                      // kebab-case slug = filename
    public string Name { get; set; }                    // display name
    public string Description { get; set; }             // 1-3 sentence summary
    public string Category { get; set; }                // Core, UI, Integration, Model, etc.
    public List<string> Keywords { get; set; }           // always sorted
    public List<string> PrimaryFiles { get; set; }       // relative paths, sorted
    public List<string> SecondaryFiles { get; set; }     // relative paths, sorted
    public List<string> RelatedFeatureIds { get; set; }  // sorted
    public FeatureContext Context { get; set; }           // hierarchical code context
    public int TouchCount { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public string LastUpdatedByTaskId { get; set; }
}

public class FeatureContext
{
    public Dictionary<string, FileSignature> Signatures { get; set; }  // path → signature
    public List<string> KeyTypes { get; set; }           // type definitions
    public List<string> Patterns { get; set; }           // architectural patterns/invariants
    public List<string> Dependencies { get; set; }       // cross-feature relationships
}

public class FileSignature
{
    public string Hash { get; set; }                     // short content hash for staleness
    public string Content { get; set; }                  // class + method signatures as text
}
```

### `FeatureIndex.cs`

```csharp
public class FeatureIndex
{
    public int Version { get; set; } = 1;
    public List<FeatureIndexEntry> Features { get; set; }  // sorted by Id
}

public class FeatureIndexEntry
{
    public string Id { get; set; }
    public string Name { get; set; }
}
```

### `FeatureContextResult.cs`

```csharp
public class FeatureContextResult
{
    public List<MatchedFeature> RelevantFeatures { get; set; }
    public bool IsNewFeature { get; set; }
    public string SuggestedNewFeatureName { get; set; }
    public List<string> SuggestedKeywords { get; set; }
    public string ContextBlock { get; set; }             // pre-built markdown for prompt injection
}

public class MatchedFeature
{
    public string FeatureId { get; set; }
    public string FeatureName { get; set; }
    public double Confidence { get; set; }               // 0.0 - 1.0
}
```

---

## 5. New Managers

### 5.1 `FeatureRegistryManager` — Persistence + Search

**Location:** `Managers/FeatureRegistryManager.cs`

**Responsibilities:**
- Load/save feature files from `.spritely/features/`
- CRUD on `FeatureEntry` objects
- Keyword-based search (local, no LLM)
- Signature staleness detection and refresh
- File-to-feature reverse lookup
- Concurrent access protection via named mutex

**Key methods:**
```csharp
Task<FeatureIndex> LoadIndexAsync(string projectPath)
Task<FeatureEntry> LoadFeatureAsync(string projectPath, string featureId)
Task<List<FeatureEntry>> LoadAllFeaturesAsync(string projectPath)
Task SaveFeatureAsync(string projectPath, FeatureEntry feature)  // saves file + updates index
Task RemoveFeatureAsync(string projectPath, string featureId)
List<FeatureEntry> FindMatchingFeatures(string taskDescription, List<FeatureEntry> allFeatures, int maxResults = 5)
Task RefreshStaleSignatures(string projectPath, FeatureEntry feature)  // re-extract if hash mismatch
string BuildFeatureContextBlock(List<FeatureEntry> features)  // build markdown for prompt injection
bool RegistryExists(string projectPath)  // check if .spritely/features/ exists
```

**Search algorithm (local, no LLM cost):**
1. Tokenize task prompt → remove stopwords → extract keywords
2. Score each feature: `keyword_overlap * 0.5 + description_match * 0.3 + file_mention * 0.2`
3. Return top N sorted by score
4. Pure string matching — fast, free, runs in <10ms

### 5.2 `FeatureContextResolver` — Task-Start Context Injection

**Location:** `Managers/FeatureContextResolver.cs`

**When:** Called in `TaskExecutionManager.StartProcess()`, after `TaskPreprocessor`, before `BuildAndWritePromptFile()`

**Flow:**
1. Check if registry exists for project → if not, skip gracefully
2. Load all features (cached in memory after first load per session)
3. Run local keyword search → get top 5-8 candidate features
4. Refresh stale signatures for candidates only (check file hashes)
5. **Haiku call:** Send candidates + task description → Haiku ranks them, confirms relevance, flags if new feature
   - Uses `Prompts/FeatureContextResolverPrompt.md`
   - `--max-turns 1 --output-schema` (same pattern as TaskPreprocessor)
   - Cost: ~$0.006 per call
6. Build `# FEATURE CONTEXT` markdown block from confirmed features
7. Return `FeatureContextResult` to be injected into prompt

**Haiku prompt structure:**
```
You are analyzing which project features are relevant to a task.

## Task Description
{taskDescription}

## Candidate Features
{for each candidate: id, name, description, keywords}

## Instructions
Return JSON: which features are relevant (with confidence 0-1), whether this is a new feature not in the list.
```

**Why Haiku and not just keyword matching?**
- Keywords alone miss semantic connections ("fix the commit dialog" → git-integration even if "commit dialog" isn't a keyword)
- Haiku costs $0.006 and takes ~1 second — negligible
- Haiku also detects new features that keyword matching can't

### 5.3 `FeatureUpdateAgent` — Post-Task Registry Update

**Location:** `Managers/FeatureUpdateAgent.cs`

**When:** Called in `CompleteWithVerificationAsync()`, after `CompletionAnalyzer.VerifyResultAsync()` succeeds. Fire-and-forget (does not block task teardown).

**Flow:**
1. Gather: task description, completion summary, changed files list, current feature index
2. **Haiku call:** Send gathered data → Haiku returns structured update instructions
   - Uses `Prompts/FeatureUpdatePrompt.md`
   - `--max-turns 1 --output-schema`
   - Cost: ~$0.006 per call
3. Apply updates:
   - Update `touchCount`, `lastUpdatedAt`, `lastUpdatedByTaskId` for touched features
   - Add new files to `primaryFiles`/`secondaryFiles` if task introduced them
   - Re-extract signatures for modified primary files
   - Create new `FeatureEntry` if Haiku identifies a genuinely new feature
   - Remove stale file references (files that no longer exist)
4. Save modified feature files

**Haiku prompt structure:**
```
You are updating a project's feature registry after a completed task.

## Task
Description: {description}
Summary: {completionSummary}
Changed files: {changedFiles}

## Current Features
{feature index with names}

## Instructions
Return JSON: features to update (add/remove files, update description), new features to create.
Only create a new feature if the task introduced genuinely new functionality not covered by existing features.
```

### 5.4 `FeatureInitializer` — Full Project Bootstrap

**Location:** `Managers/FeatureInitializer.cs`

**When:** User clicks "Initialize Features" button in project panel.

**Flow:**

**Phase 1 — File Discovery (local, fast, <1s):**
- Glob all source files (extensions based on project type)
- Skip noise: `node_modules`, `Library`, `Temp`, `bin`, `obj`, `.git`, `Builds`, `Logs`
- Group by directory/namespace
- Read file headers to extract class/interface/enum names (regex, not LLM)
- Build structural map: `{ directory: [files with class names] }`

**Phase 2 — Signature Extraction (local, fast, <5s):**
- For each source file: extract public class names, public method signatures, public properties
- Use regex patterns (not Roslyn — too heavy, and needs to work for non-C# projects too)
- Language-specific regex sets: C#, TypeScript, Python, GDScript, etc.
- Output: per-file signature blocks

**Phase 3 — Feature Identification (Sonnet call, ~$0.12):**
- Send structural map + signature summaries to Sonnet
- Uses `Prompts/FeatureInitializationPrompt.md`
- For large projects (>200 files): chunk by directory, make 2-3 calls, merge results
- Sonnet identifies logical features, assigns keywords, maps files to features

**Phase 4 — Registry Creation (local):**
- Parse Sonnet output into `FeatureEntry` objects
- Generate kebab-case IDs from feature names
- Build `_index.json`
- Write individual feature `.json` files
- Add `.gitattributes` entries if not present
- Report progress to UI

**Phase 5 — Git Integration (local):**
- Create `.spritely/` directory
- Write `.spritely/.gitignore` (track features/, ignore caches)
- Ensure `.gitattributes` has merge=union for `_index.json`

### 5.5 `SignatureExtractor` — Local Code Parsing

**Location:** `Managers/SignatureExtractor.cs`

**Purpose:** Extract public API signatures from source files without LLM calls.

**Supported languages:**
- **C#:** `class/interface/enum` declarations, `public` methods, `public` properties
- **TypeScript/JavaScript:** `export class/function/interface/type`, `export default`
- **Python:** `class` definitions, `def` at module level, type hints
- **GDScript:** `class_name`, `func`, `signal`, `export var`

**Output format per file:**
```
class GitHelper
  CommitAsync(string message, List<string> files) -> Task<GitResult>
  GetStatusAsync() -> Task<string>
  GetDiffAsync(string? baseBranch) -> Task<string>
```

Compact, human-readable, token-efficient. No method bodies — just signatures.

**Hash generation:** Short SHA256 of file content (first 12 hex chars). Used for staleness detection.

---

## 6. Prompt Integration

### 6.1 Where context gets injected

Current prompt assembly order in `BuildAndWritePromptFile()`:
```
1. Project Description
2. CLAUDE.MD RULES
3. PROJECT RULES
4. Skills Block
5. Game Rules (if game)
6. MCP Block (if MCP)
7. Git Restrictions
8. Apply Fix / Confirm
9. Output Efficiency
10. Planning blocks
11. USER PROMPT / TASK
12. Dependency Context
```

**New insertion point — between #3 and #4:**
```
1. Project Description
2. CLAUDE.MD RULES
3. PROJECT RULES
>>> 3.5. FEATURE CONTEXT (new) <<<
4. Skills Block
...
```

This puts feature context early in the prompt where it establishes architectural understanding before the task instructions.

### 6.2 What the injected block looks like

```markdown
# FEATURE CONTEXT
The following features are relevant to this task. Use this context to understand
the architecture before making changes. Read the listed files for full implementation details.

## Git Integration (confidence: 0.95)
**Core files:** Managers/GitHelper.cs, Managers/GitPanelManager.cs, Managers/GitOperationGuard.cs

### Signatures
```
class GitHelper
  CommitAsync(string message, List<string> files) -> Task<GitResult>
  GetStatusAsync() -> Task<string>
  GetDiffAsync(string? baseBranch) -> Task<string>
  GetCurrentBranchAsync() -> Task<string>

class GitPanelManager
  RefreshStatus() -> Task
  ShowCommitDialog(List<string> files) -> void
  IsOperationInProgress : bool

class GitOperationGuard
  AcquireAsync() -> Task<IDisposable>
  Release() -> void
```

### Key Types
- GitResult { ExitCode, Output, Error }
- enum GitOperationResult { Success, Conflict, AuthFailed, LockFailed }

### Patterns
- All git operations flow through GitHelper — never raw Process calls
- GitOperationGuard prevents concurrent git operations on same repo

### Related Features
- file-lock-management, task-orchestration

---

## Task Orchestration (confidence: 0.78)
**Core files:** Managers/TaskOrchestrator.cs, Managers/TaskExecutionManager.cs
...
```

### 6.3 Token budget for feature context

**Target: 1500-3000 tokens** for the feature context block. This is controlled by:
- Max 5 features per task (configurable in `FeatureConstants`)
- Max 500 tokens per feature (signatures truncated if needed)
- `SmartTruncationService` can be used if total exceeds budget

This replaces 5000-15000+ tokens of Opus file-reading exploration — net savings even in worst case.

### 6.4 CLAUDE.md duplication avoidance

The Claude Code CLI natively reads the project's `CLAUDE.md`. Spritely also injects it via `RulesManager`. The feature context is a **new block** (`# FEATURE CONTEXT`) that does not overlap with `CLAUDE.md` content, so no duplication concern.

However, if the project's `CLAUDE.md` already describes features/architecture, the Feature System's context will be more structured and actionable. We do NOT attempt to deduplicate against free-form CLAUDE.md text — the structured context is strictly additive value.

---

## 7. Integration Into Existing Pipeline

### 7.1 Task Start — `TaskExecutionManager.StartProcess()`

**Current flow (lines ~176-230):**
```
StartProcess()
  → TaskPreprocessor.PreprocessAsync()      // Haiku: header, toggles
  → BuildAndWritePromptFile()               // assemble prompt
  → GetCliModelForTask()                    // pick Sonnet/Opus
  → Write PS1 script
  → CreateManagedProcess()                  // launch
```

**New flow:**
```
StartProcess()
  → TaskPreprocessor.PreprocessAsync()      // Haiku: header, toggles
  → FeatureContextResolver.ResolveAsync()   // Haiku: match features (NEW)
  → BuildAndWritePromptFile()               // assemble prompt (now includes feature context)
  → GetCliModelForTask()                    // pick Sonnet/Opus
  → Write PS1 script
  → CreateManagedProcess()                  // launch
```

**Changes to `BuildAndWritePromptFile()`:**
- Accept optional `FeatureContextResult` parameter
- If provided, inject `result.ContextBlock` after PROJECT RULES

**Changes to `PromptBuilder`:**
- Add `string featureContextBlock` parameter to `BuildBasePrompt()` and `BuildFullPrompt()`
- Insert block at position 3.5 in assembly order

### 7.2 Task Completion — `CompleteWithVerificationAsync()`

**Current flow (lines ~458-505):**
```
CompleteWithVerificationAsync()
  → Release locks (failed only)
  → Leave message bus
  → AppendCompletionSummary()               // Haiku: verify + summarize
  → Set final status
  → CheckQueuedTasks()
  → Fire TaskCompleted
```

**New flow:**
```
CompleteWithVerificationAsync()
  → Release locks (failed only)
  → Leave message bus
  → AppendCompletionSummary()               // Haiku: verify + summarize
  → FeatureUpdateAgent.UpdateAsync()        // Haiku: update registry (NEW, fire-and-forget)
  → Set final status
  → CheckQueuedTasks()
  → Fire TaskCompleted
```

The `FeatureUpdateAgent` call is non-blocking (`_ = UpdateAsync(task)`) — it runs in the background and does not delay task teardown or the next queued task.

### 7.3 New Feature Detection (during task start)

When `FeatureContextResolver` returns `IsNewFeature = true`:
1. Create a placeholder `FeatureEntry` with suggested name/keywords
2. No signatures yet (the feature doesn't exist in code yet)
3. The completion `FeatureUpdateAgent` fills in real details after the task creates the code
4. This ensures follow-up tasks can find the feature immediately

### 7.4 Project Initialization UI

**In `MainWindow.xaml` project panel:**
- Add "Initialize Features" button (or icon button) per project
- Enabled when `!FeatureRegistryManager.RegistryExists(project.Path)`
- Shows progress bar/text during initialization
- Disabled + shows checkmark when registry exists

**In `ProjectEntry.cs`:**
- Add computed property: `IsFeatureRegistryInitialized` → checks `.spritely/features/_index.json` exists

---

## 8. Graceful Degradation

The entire feature system is optional and additive:

| Condition | Behavior |
|-----------|----------|
| No `.spritely/features/` directory | Feature resolver skips entirely, task runs as before |
| Registry exists but is empty | Resolver finds no matches, task runs as before |
| Feature file is corrupted | Log warning, skip that feature, continue |
| Haiku resolver call fails | Log warning, skip feature context, task runs as before |
| Haiku update call fails | Log warning, registry unchanged, task still completes |
| Concurrent access conflict | Retry with backoff (3 attempts), then skip |

No existing functionality is broken if the feature system is absent or fails.

---

## 9. Concurrent Access & Merge Safety

### Local concurrency (multiple Spritely agents)
- Named mutex: `Global\Spritely_FeatureRegistry_{projectPathHash}`
- Acquired for: index writes, feature file writes
- Not needed for: reads (file system handles concurrent reads)
- Pattern: same as existing `SafeFileWriter`

### Git merge strategy
- `_index.json` with `merge=union`: additive changes auto-merge
- Individual feature files: rarely conflict (different tasks touch different features)
- When conflict occurs: standard JSON merge — small file, easy to resolve manually
- Sorted keys + sorted arrays = minimal diff noise

### Cross-machine consistency
- Feature files use relative paths — portable across machines
- No machine-specific data (no absolute paths, no PIDs, no temp paths)
- Signatures include file hash — each machine validates freshness independently
- Timestamps use UTC ISO 8601

---

## 10. File Manifest

### New files

| File | Type | Purpose |
|------|------|---------|
| `Models/FeatureEntry.cs` | Model | Feature definition + context (maps to one `.json` file) |
| `Models/FeatureIndex.cs` | Model | Lightweight manifest (`_index.json`) |
| `Models/FeatureContextResult.cs` | Model | Result of task-to-feature matching |
| `Managers/FeatureRegistryManager.cs` | Manager | Persistence, search, staleness, mutex |
| `Managers/FeatureContextResolver.cs` | Manager | Haiku-powered task→feature matching at task start |
| `Managers/FeatureUpdateAgent.cs` | Manager | Haiku-powered registry update at task end |
| `Managers/FeatureInitializer.cs` | Manager | Full project bootstrap (Sonnet) |
| `Managers/SignatureExtractor.cs` | Manager | Local code signature extraction (no LLM) |
| `Prompts/FeatureContextResolverPrompt.md` | Prompt | Haiku matching template |
| `Prompts/FeatureUpdatePrompt.md` | Prompt | Haiku update template |
| `Prompts/FeatureInitializationPrompt.md` | Prompt | Sonnet scan template |
| `Constants/FeatureConstants.cs` | Constants | Tuning: max features, token budgets, hash length, etc. |

### Modified files

| File | Change |
|------|--------|
| `Managers/TaskExecutionManager.cs` | Insert `FeatureContextResolver.ResolveAsync()` in `StartProcess()` |
| `Managers/PromptBuilder.cs` + `IPromptBuilder.cs` | Accept + inject `featureContextBlock` parameter |
| `Models/ProjectEntry.cs` | Add `IsFeatureRegistryInitialized` computed property |
| `MainWindow.xaml` | Add "Initialize Features" button to project panel |
| `MainWindow.xaml.cs` (or relevant partial) | Wire button to `FeatureInitializer` |
| `Constants/AppConstants.cs` | Reference `FeatureConstants` or add feature model constants |

---

## 11. Implementation Phases

### Phase 1: Foundation — Models + Registry Manager + Signature Extractor
**Scope:**
- Create `FeatureEntry`, `FeatureIndex`, `FeatureContextResult` models
- Create `FeatureRegistryManager` with load/save/search/mutex
- Create `SignatureExtractor` with C# regex patterns (other languages later)
- Create `FeatureConstants`
- Unit tests for: keyword search, signature extraction, JSON serialization determinism, concurrent access

**Deliverable:** Can manually create/read/update feature files. No LLM integration yet.

### Phase 2: Project Initialization
**Scope:**
- Create `FeatureInitializer` with full-scan pipeline
- Create `Prompts/FeatureInitializationPrompt.md`
- Add "Initialize Features" button to project panel
- Wire UI → initializer with progress reporting
- Add `IsFeatureRegistryInitialized` to `ProjectEntry`
- Add `.gitattributes` entries during initialization

**Deliverable:** Can bootstrap a full feature registry for any existing project.

### Phase 3: Task Start Integration
**Scope:**
- Create `FeatureContextResolver` + `Prompts/FeatureContextResolverPrompt.md`
- Integrate into `TaskExecutionManager.StartProcess()` after preprocessor
- Modify `PromptBuilder` to accept and inject feature context block
- Add staleness detection (hash check + re-extract) to resolver flow
- Handle graceful degradation (no registry, resolver failure)

**Deliverable:** Every Opus task gets relevant feature context injected. Measurable token savings.

### Phase 4: Task Completion Integration
**Scope:**
- Create `FeatureUpdateAgent` + `Prompts/FeatureUpdatePrompt.md`
- Integrate into `CompleteWithVerificationAsync()` as fire-and-forget
- Handle: feature updates, new feature creation, stale file cleanup
- Handle: new feature placeholder creation when `IsNewFeature = true` at task start

**Deliverable:** Registry self-updates after every task. New features auto-registered.

### Phase 5: Multi-Language Support + Polish
**Scope:**
- Add signature extraction patterns for: TypeScript, Python, GDScript
- Large project chunking in initializer (>200 files)
- Dashboard/UI to view features per project (optional)
- Registry version migration logic
- Feature merge/dedup when Haiku reports overlapping features
- Edge case tests: corrupted files, empty registries, concurrent initialization

**Deliverable:** Production-ready system supporting all project types.

---

## 12. Constants (`FeatureConstants.cs`)

```csharp
public static class FeatureConstants
{
    // Directory structure
    public const string SpritelyDir = ".spritely";
    public const string FeaturesDir = "features";
    public const string IndexFileName = "_index.json";

    // Search tuning
    public const int MaxFeaturesPerTask = 5;
    public const int MaxTokensPerFeature = 500;
    public const int MaxTotalFeatureContextTokens = 3000;

    // Staleness
    public const int SignatureHashLength = 12;  // hex chars from SHA256

    // Initialization
    public const int MaxFilesPerSonnetChunk = 200;
    public const int MaxSignatureLinesPerFile = 20;

    // Concurrency
    public const int MutexTimeoutMs = 5000;
    public const int MutexRetryCount = 3;

    // Ignored directories (for initialization scan)
    public static readonly string[] IgnoredDirectories = {
        "node_modules", "Library", "Temp", "bin", "obj",
        ".git", "Builds", "Logs", ".vs", ".idea",
        "packages", "dist", "build", "__pycache__"
    };
}
```

---

## 13. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Feature registry becomes stale | Hash-based staleness detection + post-task updates |
| Haiku misidentifies features | Local keyword pre-filter limits candidates; Haiku only ranks |
| Large projects have too many features | Cap at reasonable number; use categories for grouping |
| Merge conflicts in feature files | One-file-per-feature + sorted deterministic JSON minimizes this |
| Feature context inflates prompt too much | Token budget cap (3000 tokens max) + truncation |
| Initialization takes too long | Chunked Sonnet calls with progress reporting |
| Registry corrupted | Graceful degradation — skip corrupted files, log warnings |
| Multiple agents create same new feature | Mutex + dedup on feature name/files |

---

## 14. Success Metrics

- **Token savings:** Measure Opus input tokens per task before/after. Target: 30% reduction.
- **Task speed:** Measure time-to-first-edit. Target: 20% faster (less exploration).
- **Registry coverage:** After 10 tasks, >80% of project files should be mapped to features.
- **Merge conflicts:** Track git conflicts in `.spritely/features/` — target: <5% of merges.
- **False positives:** Track how often injected features are irrelevant — target: <20%.
