# Hybrid Semantic Index with Multimodal Embeddings — Architecture Plan

## Executive Summary

This document compares the current Feature System with a proposed Hybrid Semantic Index using multimodal embeddings, and provides a detailed architectural plan for the latter. The hybrid approach combines the current system's structured, deterministic feature matching with dense vector embeddings for semantic retrieval — yielding better recall, cross-modal understanding, and reduced reliance on keyword heuristics.

---

## 1. Current Feature System — Strengths & Limitations

### Strengths

| Aspect | Detail |
|--------|--------|
| **Zero infrastructure** | Plain JSON files in `.spritely/features/`, no database or service required |
| **Git-friendly** | One file per feature, deterministic JSON, `merge=union` strategy |
| **Self-maintaining** | Post-task Haiku updates keep registry current |
| **Fast local matching** | Keyword + symbol matching runs in <10ms, no network call |
| **Graceful degradation** | Every failure path returns null and lets the task proceed normally |
| **Cost-effective** | Two Haiku calls ($0.012) save ~30% on Opus tokens |
| **Multi-language** | SignatureExtractor handles C#, TS, Python, GDScript |

### Limitations

| Limitation | Impact |
|------------|--------|
| **Keyword-only local matching** | Misses semantic connections ("fix the login flow" won't match `auth-session-management` unless exact keywords overlap) |
| **No cross-modal understanding** | Can't relate a UI screenshot, error log, or architecture diagram to relevant features |
| **Flat confidence scoring** | Linear weighted formula (`keyword*0.5 + desc*0.3 + file*0.2`) is brittle; adding new signals requires manual weight tuning |
| **Feature granularity is fixed at init** | Sonnet decides feature boundaries during initialization; organic drift isn't captured well |
| **No code-level semantic search** | Can find `GitHelper` by name but can't answer "which code handles retry logic for failed pushes?" |
| **Haiku as semantic bridge** | The fast-path threshold (0.85) skips Haiku, but the normal path requires a full Haiku call just for ranking — an embedding dot-product would be faster and cheaper |
| **Symbol index is exact-match** | `_codebase_index.json` maps symbol names to features but doesn't understand that `CommitHelper` and `GitCommitService` are semantically related |
| **No learning from task history** | Past tasks and their outcomes aren't factored into relevance scoring |

---

## 2. Hybrid Semantic Index — Design Overview

### Core Idea

Maintain the current structured feature registry (it's excellent for deterministic lookups and Git persistence) but augment it with a dense vector index that enables:

1. **Semantic matching** — find relevant features by meaning, not just keywords
2. **Cross-modal retrieval** — relate task descriptions, code, error logs, screenshots, and architecture diagrams
3. **Historical learning** — embed past task descriptions + outcomes to improve future matching
4. **Graduated retrieval** — fast vector search replaces Haiku for most tasks; Haiku only for ambiguous cases

### Architecture Layers

```
┌──────────────────────────────────────────────────────┐
│                   Query Layer                         │
│  Task description → Vector search → Ranked features  │
├──────────────────────────────────────────────────────┤
│                  Fusion Layer                         │
│  Combines: vector scores + keyword scores + symbol   │
│  matches + dependency graph + historical affinity     │
├──────────────────────────────────────────────────────┤
│               Embedding Store                         │
│  SQLite + binary vector blobs (local, no server)     │
├──────────────────────────────────────────────────────┤
│              Embedding Pipeline                       │
│  Code → chunks → embeddings (local or API)           │
│  Images → embeddings (API, optional)                 │
├──────────────────────────────────────────────────────┤
│           Structured Feature Registry                 │
│  .spritely/features/*.json (unchanged)               │
└──────────────────────────────────────────────────────┘
```

---

## 3. Embedding Strategy

### 3.1 Which Models

| Modality | Model | Dims | Cost | Latency | Notes |
|----------|-------|------|------|---------|-------|
| **Code + Text** | Voyage Code 3 (`voyage-code-3`) | 1024 | $0.06/M tokens | ~100ms/batch | Best-in-class code embeddings; understands code semantics, not just syntax |
| **Text (fallback)** | Voyage 3 Large (`voyage-3-large`) | 1024 | $0.06/M tokens | ~100ms/batch | For natural language descriptions, task summaries |
| **Multimodal** | Voyage Multimodal 3 (`voyage-multimodal-3`) | 1024 | $0.12/M tokens | ~200ms | For screenshots, architecture diagrams, error images |
| **Local fallback** | ONNX MiniLM-L6 or similar | 384 | Free | ~5ms | Offline mode; lower quality but instant |

**Why Voyage over OpenAI embeddings?**
- Voyage Code 3 significantly outperforms `text-embedding-3-large` on code retrieval benchmarks
- Same dimension (1024) means unified vector space
- Multimodal variant uses the same embedding space — code and images are directly comparable
- Competitive pricing ($0.06-0.12/M tokens)

**Why not local-only embeddings?**
- Local models (ONNX/Sentence Transformers) lack code-specific training and multimodal support
- But we include a local fallback for offline/cost-sensitive scenarios
- The hybrid approach uses local for rough filtering, API for precision

### 3.2 What Gets Embedded

| Item | Embedding Model | When Embedded | Storage Key |
|------|----------------|---------------|-------------|
| Feature description + keywords | Voyage Code 3 | At feature creation/update | `feature:{featureId}:desc` |
| Code signatures (per feature) | Voyage Code 3 | At feature creation/update + staleness refresh | `feature:{featureId}:sigs` |
| Full file content (chunked) | Voyage Code 3 | At initialization + on file change | `file:{relativePath}:chunk:{n}` |
| Task description | Voyage Code 3 | At task start | `task:{taskId}:desc` (ephemeral) |
| Task completion summary | Voyage 3 Large | At task end | `task:{taskId}:summary` (persisted) |
| Error logs / stack traces | Voyage Code 3 | When task fails | `task:{taskId}:error` |
| Screenshots / diagrams | Voyage Multimodal 3 | On user attachment | `image:{hash}` |

### 3.3 Chunking Strategy

Code files need intelligent chunking to preserve semantic units:

```
ChunkStrategy:
  - Primary: Split on class/function boundaries (use SignatureExtractor's AST knowledge)
  - Fallback: Fixed-size with overlap (512 tokens, 64-token overlap)
  - Metadata per chunk: file path, line range, containing class/function, feature ID
  - Max chunks per file: 20 (cap for very large files)
```

**Why class/function boundary splitting?**
- A chunk containing a complete method is far more useful than one split mid-function
- SignatureExtractor already identifies these boundaries — reuse that infrastructure
- Preserves the "unit of meaning" that embeddings work best with

### 3.4 Embedding Dimensions and Quantization

```
Storage format per vector:
  - Full precision: float32 × 1024 = 4KB per vector
  - Quantized: int8 × 1024 = 1KB per vector (scalar quantization)
  - Binary: 1024 bits = 128 bytes per vector (for pre-filtering)

Strategy:
  - Store full float32 for accuracy
  - Pre-compute binary vectors for fast hamming-distance pre-filtering
  - Typical project (50 features, 500 files, avg 5 chunks/file):
    ~2,750 vectors × 4KB = ~11MB (fits comfortably in memory)
```

---

## 4. Data Model & Storage

### 4.1 Storage Architecture

```
{project_root}/
  .spritely/
    features/                     # UNCHANGED — structured registry
      _index.json
      {feature}.json
      _codebase_index.json
    embeddings/                   # NEW — vector store
      vectors.db                  # SQLite database (vectors + metadata)
      .gitignore                  # vectors.db is NOT git-tracked (machine-specific)
```

**Why SQLite, not flat files?**
- Vector search requires indexed access patterns (k-NN, filtered search)
- JSON files would require loading all vectors into memory on every query
- SQLite is zero-config, single-file, and .NET has excellent support (`Microsoft.Data.Sqlite`)
- The file is machine-local (not git-tracked) — rebuilt from source code on each machine

**Why not a vector database (Qdrant, Pinecone, etc.)?**
- Spritely is a desktop app — no server dependency
- SQLite + brute-force search handles 10K vectors in <10ms
- If scale requires it later, we can add HNSW indexing via `usearch` or `hnswlib` (both have .NET bindings)

### 4.2 SQLite Schema

```sql
-- Core vector storage
CREATE TABLE vectors (
    id TEXT PRIMARY KEY,           -- e.g., "feature:git-integration:sigs"
    category TEXT NOT NULL,        -- "feature_desc", "feature_sigs", "file_chunk", "task", "image"
    feature_id TEXT,               -- nullable, links to feature registry
    file_path TEXT,                -- nullable, for file chunks
    chunk_index INTEGER DEFAULT 0, -- for multi-chunk files
    line_start INTEGER,            -- nullable, source location
    line_end INTEGER,              -- nullable, source location
    content_preview TEXT,          -- first 200 chars of embedded content
    embedding BLOB NOT NULL,       -- float32[] serialized
    binary_embedding BLOB,         -- binary quantization for pre-filtering
    content_hash TEXT,             -- for staleness detection (reuse SignatureExtractor hashes)
    model_id TEXT NOT NULL,        -- which embedding model produced this
    created_at TEXT NOT NULL,      -- ISO 8601 UTC
    updated_at TEXT NOT NULL       -- ISO 8601 UTC
);

CREATE INDEX idx_vectors_category ON vectors(category);
CREATE INDEX idx_vectors_feature ON vectors(feature_id);
CREATE INDEX idx_vectors_file ON vectors(file_path);
CREATE INDEX idx_vectors_hash ON vectors(content_hash);

-- Task history embeddings (for learning from past tasks)
CREATE TABLE task_embeddings (
    task_id TEXT PRIMARY KEY,
    description_embedding BLOB NOT NULL,
    summary_embedding BLOB,        -- filled after task completion
    matched_feature_ids TEXT,       -- JSON array of feature IDs this task used
    outcome TEXT,                   -- "success", "failed", "partial"
    created_at TEXT NOT NULL
);

CREATE INDEX idx_task_feature ON task_embeddings(matched_feature_ids);

-- Embedding metadata / versioning
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
-- Keys: "schema_version", "last_full_reindex", "default_model_id"
```

### 4.3 New Data Models (C#)

```csharp
// Models/EmbeddingVector.cs
public class EmbeddingVector
{
    public string Id { get; set; }
    public string Category { get; set; }      // "feature_desc", "feature_sigs", "file_chunk", etc.
    public string? FeatureId { get; set; }
    public string? FilePath { get; set; }
    public int ChunkIndex { get; set; }
    public int? LineStart { get; set; }
    public int? LineEnd { get; set; }
    public string ContentPreview { get; set; }
    public float[] Embedding { get; set; }     // 1024-dim float32
    public byte[]? BinaryEmbedding { get; set; } // 128-byte binary quantization
    public string ContentHash { get; set; }
    public string ModelId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Models/SemanticSearchResult.cs
public class SemanticSearchResult
{
    public string VectorId { get; set; }
    public string Category { get; set; }
    public string? FeatureId { get; set; }
    public string? FilePath { get; set; }
    public string ContentPreview { get; set; }
    public float Score { get; set; }            // cosine similarity
    public float? KeywordScore { get; set; }    // from existing keyword matching
    public float FusedScore { get; set; }       // combined score
}

// Models/HybridSearchRequest.cs
public class HybridSearchRequest
{
    public string Query { get; set; }
    public string[]? Categories { get; set; }    // filter to specific categories
    public string[]? FeatureIds { get; set; }    // filter to specific features
    public int TopK { get; set; } = 10;
    public float MinScore { get; set; } = 0.3f;
    public bool IncludeFileChunks { get; set; } = true;
    public bool IncludeTaskHistory { get; set; } = true;
    public byte[]? ImageData { get; set; }       // optional multimodal query
}
```

---

## 5. New Managers

### 5.1 `EmbeddingService` — Embedding Generation

**Location:** `Managers/EmbeddingService.cs`

**Responsibilities:**
- Generate embeddings via Voyage API (primary) or local ONNX (fallback)
- Batch embedding requests for efficiency (Voyage supports up to 128 inputs per call)
- Cache API key from `%LOCALAPPDATA%\Spritely\` (never in repo)
- Handle rate limiting and retries
- Support text and image inputs

**Key methods:**
```csharp
Task<float[]> EmbedTextAsync(string text, string? modelOverride = null, CancellationToken ct = default)
Task<float[][]> EmbedBatchAsync(string[] texts, string? modelOverride = null, CancellationToken ct = default)
Task<float[]> EmbedImageAsync(byte[] imageData, CancellationToken ct = default)
Task<float[]> EmbedCodeAsync(string code, CancellationToken ct = default)  // uses voyage-code-3
float CosineSimilarity(float[] a, float[] b)
byte[] ToBinaryVector(float[] embedding)  // scalar quantization
```

**Cost per operation:**
- Single embedding: ~100 tokens × $0.06/M = $0.000006 (negligible)
- Full project init (500 files × 5 chunks): ~250K tokens × $0.06/M = $0.015
- Per-task query: 1 embedding = $0.000006

### 5.2 `VectorStore` — Local Vector Database

**Location:** `Managers/VectorStore.cs`

**Responsibilities:**
- SQLite-backed vector storage and retrieval
- K-nearest-neighbor search (brute force cosine similarity; HNSW upgrade path)
- Two-phase search: binary pre-filter → full-precision rerank
- Staleness detection via content hashes
- Index management (create, rebuild, prune)

**Key methods:**
```csharp
Task InitializeAsync(string projectPath)
Task UpsertAsync(EmbeddingVector vector)
Task UpsertBatchAsync(List<EmbeddingVector> vectors)
Task<List<SemanticSearchResult>> SearchAsync(float[] queryEmbedding, int topK = 10, string[]? categories = null, float minScore = 0.3f)
Task<List<SemanticSearchResult>> SearchWithPreFilterAsync(float[] queryEmbedding, byte[] binaryQuery, int preFilterK = 100, int topK = 10)
Task DeleteByFeatureAsync(string featureId)
Task DeleteByFileAsync(string filePath)
Task<bool> IsStaleAsync(string id, string currentHash)
Task PruneOrphansAsync(HashSet<string> validFeatureIds, HashSet<string> validFilePaths)
Task<int> GetVectorCountAsync(string? category = null)
```

**Search algorithm:**
```
SearchWithPreFilter(query, binaryQuery, preFilterK=100, topK=10):
  1. Compute hamming distance of binaryQuery against all binary_embeddings
  2. Take top preFilterK candidates (fast: ~1ms for 10K vectors)
  3. Compute full cosine similarity for candidates only
  4. Return top topK sorted by cosine similarity
```

### 5.3 `HybridSearchManager` — Fusion & Orchestration

**Location:** `Managers/HybridSearchManager.cs`

**Responsibilities:**
- Orchestrate the full search pipeline
- Fuse vector scores with keyword/symbol scores from existing `FeatureRegistryManager`
- Decide when to skip Haiku (replaces current fast-path heuristic with embedding confidence)
- Incorporate task history for relevance boosting
- Handle multimodal queries (text + optional image)

**Key methods:**
```csharp
Task<FeatureContextResult> SearchAsync(HybridSearchRequest request, string projectPath, CancellationToken ct = default)
Task IndexFeatureAsync(string projectPath, FeatureEntry feature)
Task IndexFileAsync(string projectPath, string relativePath)
Task IndexTaskCompletionAsync(string taskId, string description, string? summary, List<string> matchedFeatureIds, string outcome)
Task ReindexProjectAsync(string projectPath, IProgress<string>? progress = null, CancellationToken ct = default)
```

**Fusion algorithm (Reciprocal Rank Fusion):**
```
For each candidate feature:
  vectorScore = cosine_similarity(query_embedding, feature_embedding)
  keywordScore = FeatureRegistryManager.ScoreFeature(query, feature) / max_keyword_score
  symbolScore = symbol_match ? SymbolMatchScoreBoost : 0
  historyScore = avg(cosine_similarity(query_embedding, past_task_embeddings_for_this_feature))
  dependencyScore = any_high_confidence_neighbor ? 0.1 : 0

  fusedScore = (
    vectorScore * 0.45 +
    keywordScore * 0.25 +
    symbolScore * 0.15 +
    historyScore * 0.10 +
    dependencyScore * 0.05
  )
```

**Haiku skip decision (replaces current fast-path):**
```
if (topCandidate.fusedScore >= 0.80 AND candidateCount <= 3):
    skip Haiku → use vector-ranked results directly
elif (topCandidate.fusedScore >= 0.60):
    skip Haiku → use top 5 by fusedScore
else:
    call Haiku for disambiguation (same as current system)
```

This should skip Haiku for ~70% of tasks (up from current ~30% fast-path rate), saving $0.006 per skipped call.

### 5.4 `SemanticIndexInitializer` — Project Bootstrap

**Location:** `Managers/SemanticIndexInitializer.cs`

**Responsibilities:**
- Bootstrap the vector store for a project (runs alongside or after FeatureInitializer)
- Chunk and embed all source files
- Embed all feature descriptions and signatures
- Build binary pre-filter index
- Report progress to UI

**Flow:**
```
Phase 1: Reuse FeatureInitializer's file discovery + signature extraction
Phase 2: Chunk source files by class/function boundaries
Phase 3: Batch-embed chunks via EmbeddingService (128 per API call)
Phase 4: Embed feature descriptions and signatures
Phase 5: Build binary quantization index
Phase 6: Store everything in SQLite
Phase 7: Report statistics (vector count, coverage, cost)
```

**Cost estimate for initialization:**
- 500 files × 5 chunks avg = 2,500 chunks
- ~200 tokens per chunk average = 500K tokens
- Voyage Code 3: $0.06/M = $0.03
- Plus 50 feature descriptions: negligible
- **Total init cost: ~$0.03** (vs $0.12 for Sonnet feature scan)

---

## 6. Integration Points

### 6.1 Task Start — Modified Flow

```
Current:
  StartProcess()
    → TaskPreprocessor (Haiku)     ─┐
    → FeatureContextResolver (Haiku) ─┤ parallel
    → BuildAndWritePromptFile()      │
    → Launch CLI                     │

Proposed:
  StartProcess()
    → TaskPreprocessor (Haiku)          ─┐
    → HybridSearchManager.SearchAsync()  ─┤ parallel
    → BuildAndWritePromptFile()           │  (vector search replaces Haiku for ~70% of tasks)
    → Launch CLI                          │
```

**Changes to `TaskExecutionManager.StartProcess()`:**
- Replace `_featureContextResolver.ResolveAsync()` with `_hybridSearchManager.SearchAsync()`
- The `HybridSearchManager` internally calls `FeatureContextResolver` only when vector confidence is low
- Result type unchanged: `FeatureContextResult` (same `ContextBlock` for prompt injection)

**Changes to `FeatureContextResolver`:**
- Accepts optional pre-computed vector scores to avoid redundant keyword matching
- New method: `ResolveWithVectorHintsAsync(preRankedFeatures, taskDescription)`
- Haiku is called only when `HybridSearchManager` determines disambiguation is needed

### 6.2 Task Completion — Modified Flow

```
Current:
  CompleteWithVerification()
    → AppendCompletionSummary (Haiku)
    → FeatureUpdateAgent.UpdateAsync() (fire-and-forget)

Proposed:
  CompleteWithVerification()
    → AppendCompletionSummary (Haiku)
    → FeatureUpdateAgent.UpdateAsync() (fire-and-forget)   # UNCHANGED
    → HybridSearchManager.IndexTaskCompletionAsync()        # NEW (fire-and-forget)
    → HybridSearchManager.UpdateChangedFileEmbeddingsAsync() # NEW (fire-and-forget)
```

**New post-task operations (all fire-and-forget, non-blocking):**
1. Embed task description + completion summary → store in `task_embeddings`
2. Re-embed changed files (only the chunks whose content hashes changed)
3. Re-embed updated features (description/signatures may have changed via FeatureUpdateAgent)

### 6.3 PromptBuilder — No Changes

The `PromptBuilder.BuildBasePrompt()` already accepts `featureContextBlock` parameter. The `HybridSearchManager` produces the same `FeatureContextResult.ContextBlock` format. No PromptBuilder changes needed.

### 6.4 FeatureInitializer — Extended

```
Current:
  InitializeAsync()
    → Phase 1-7 (file discovery, signatures, Sonnet, registry, deps, modules, git)

Proposed:
  InitializeAsync()
    → Phase 1-7 (UNCHANGED)
    → Phase 8 (NEW): SemanticIndexInitializer.BuildIndexAsync()
      → Chunk files, embed, store in SQLite
```

### 6.5 UI Integration

**Project panel additions:**
- "Rebuild Embeddings" button (per project, next to "Initialize Features")
- Embedding stats in project info: `{vectorCount} vectors, {lastIndexed} last indexed`
- Optional: "Semantic Search" debug panel for testing queries against the index

---

## 7. Comparison: Feature System vs. Hybrid Semantic Index

| Dimension | Current Feature System | Hybrid Semantic Index |
|-----------|----------------------|----------------------|
| **Matching method** | Keyword + symbol + Haiku | Vector similarity + keyword + symbol + history + optional Haiku |
| **Semantic understanding** | None (exact token matching) | Full (dense embeddings capture meaning) |
| **Cross-modal** | No | Yes (code, text, images share embedding space) |
| **Historical learning** | No | Yes (past task embeddings improve future matching) |
| **Haiku dependency** | Required for ~70% of tasks | Required for ~30% of tasks |
| **Cost per task** | $0.012 (2 Haiku calls) | ~$0.006-0.012 (1 Haiku call when needed + negligible embedding cost) |
| **Init cost** | $0.12 (Sonnet scan) | $0.15 ($0.12 Sonnet + $0.03 embeddings) |
| **Latency (task start)** | ~1.5s (Haiku call) | ~200ms (vector search) or ~1.5s (fallback to Haiku) |
| **Offline capability** | Partial (keyword matching works, Haiku doesn't) | Better (local ONNX embeddings + keyword matching) |
| **Git tracking** | Full (JSON files) | Partial (features git-tracked, vectors rebuilt locally) |
| **Infrastructure** | None | SQLite (zero-config) + Voyage API key |
| **Accuracy (est.)** | ~80% relevant features in top 5 | ~92% relevant features in top 5 |
| **Storage** | ~500KB for 50 features | ~500KB features + ~11MB vectors |
| **New dependency** | None | `Microsoft.Data.Sqlite`, Voyage API client |

### When the Hybrid Approach Wins Big

1. **Semantic queries**: "fix the thing that handles user authentication retries" — embeddings understand this relates to `auth-session-management` even without keyword overlap
2. **Cross-feature discovery**: "the commit dialog has a bug where it shows stale diff" — embeddings can surface both `git-integration` AND `task-ui` features
3. **Image-based queries**: user pastes a screenshot of a broken dialog — multimodal embedding matches it to the relevant UI feature
4. **Historical affinity**: "do the same kind of refactor we did last week" — task history embeddings find the previous task and its feature matches
5. **Cold start for new projects**: embedding all code files provides immediate semantic search before any features are manually defined

### When Current System is Sufficient

1. **Well-named features with good keywords** — keyword matching already works
2. **Symbol-heavy queries** — "fix GitHelper.CommitAsync" is served perfectly by symbol index
3. **Small projects** (<20 features) — the improvement margin is small
4. **Offline-only workflows** — API embeddings not available

### Recommendation

**Implement as an augmentation, not a replacement.** The current feature system is the source of truth for structured data. The semantic index is a query-time enhancement layer. If the embedding service is unavailable, the system falls back to the current keyword + Haiku approach transparently.

---

## 8. Cost & Performance Analysis

### Per-Task Cost Comparison

| Phase | Current | Hybrid (fast path) | Hybrid (Haiku fallback) |
|-------|---------|--------------------|-----------------------|
| Preprocessing | $0.004 (Haiku) | $0.004 (Haiku) | $0.004 (Haiku) |
| Feature resolution | $0.006 (Haiku) | $0.000006 (1 embedding) | $0.006 (Haiku) |
| Main execution | $0.73 (Opus, reduced) | $0.65 (Opus, further reduced) | $0.65 (Opus) |
| Verification | $0.004 (Haiku) | $0.004 (Haiku) | $0.004 (Haiku) |
| Feature update | $0.006 (Haiku) | $0.006 (Haiku) | $0.006 (Haiku) |
| Embedding updates | — | $0.0001 (re-embed changes) | $0.0001 |
| **Total** | **$0.75** | **$0.66** | **$0.67** |

**Why further Opus savings?** Better feature matching → more relevant context injected → Opus spends fewer tokens exploring. Estimated 5-10% additional reduction in Opus tokens from improved retrieval accuracy.

**Estimated savings: ~12% additional per task** on top of the current 30% savings from the feature system.

### Latency Comparison

| Operation | Current | Hybrid |
|-----------|---------|--------|
| Feature resolution (fast path) | ~50ms | ~200ms (embed query + vector search) |
| Feature resolution (Haiku) | ~1.5s | ~1.5s (same, but triggered less often) |
| Post-task updates | ~2s (Haiku) | ~2.5s (Haiku + re-embed) |
| Full project init | ~30s | ~45s (+ embedding phase) |

**Net latency impact:** Slightly slower for vector search (~200ms vs ~50ms for fast path), but Haiku is skipped more often, so average resolution time decreases from ~1.1s to ~0.6s.

### Storage

| Component | Size (50-feature project) |
|-----------|--------------------------|
| Feature JSON files | ~500KB (unchanged) |
| SQLite vector DB | ~11MB |
| Total | ~11.5MB |

SQLite DB is `.gitignored` — rebuilt from source on each machine in ~15s.

---

## 9. Migration & Rollout Strategy

### Phase 1: Foundation (No behavior change)
**Scope:** EmbeddingService, VectorStore, SQLite schema, data models
**Risk:** Zero — no integration with task pipeline
**Deliverable:** Can generate and store embeddings, run k-NN queries

### Phase 2: Index Building (Background, opt-in)
**Scope:** SemanticIndexInitializer, "Build Embeddings" UI button
**Risk:** Low — runs alongside existing feature system, doesn't replace anything
**Deliverable:** Projects can build vector indexes; admin can query them via debug panel

### Phase 3: Hybrid Search (Feature-flagged)
**Scope:** HybridSearchManager with fusion algorithm
**Risk:** Medium — replaces feature resolution logic, but behind feature flag
**Approach:**
- Add `AppConstants.UseHybridSearch` flag (default: false)
- When enabled, `HybridSearchManager.SearchAsync()` replaces `FeatureContextResolver.ResolveAsync()`
- When disabled, existing behavior unchanged
- Log both paths' results for A/B comparison during testing

### Phase 4: Post-Task Embedding Updates
**Scope:** Task completion embedding updates, historical learning
**Risk:** Low — fire-and-forget, same pattern as FeatureUpdateAgent
**Deliverable:** Vector index stays current; task history improves future matching

### Phase 5: Multimodal Support (Optional)
**Scope:** Image embedding via Voyage Multimodal 3
**Risk:** Low — additive capability
**Prerequisite:** User must configure Voyage API key with multimodal access
**Deliverable:** Screenshots and diagrams can be matched to relevant features

### Phase 6: Remove Feature Flag, Optimize
**Scope:** Make hybrid search the default, tune fusion weights, add HNSW if needed
**Deliverable:** Production-ready system

### Rollback Plan
Each phase is independently reversible:
- Delete `vectors.db` → system falls back to keyword + Haiku
- Set `UseHybridSearch = false` → reverts to current FeatureContextResolver
- No schema changes to feature JSON files at any phase

---

## 10. File Manifest

### New Files

| File | Type | Purpose |
|------|------|---------|
| `Models/EmbeddingVector.cs` | Model | Vector entry with metadata |
| `Models/SemanticSearchResult.cs` | Model | Search result with fused scores |
| `Models/HybridSearchRequest.cs` | Model | Query configuration |
| `Managers/EmbeddingService.cs` | Manager | Voyage API client + local fallback |
| `Managers/VectorStore.cs` | Manager | SQLite-backed vector storage + k-NN search |
| `Managers/HybridSearchManager.cs` | Manager | Fusion orchestration, Haiku skip logic |
| `Managers/SemanticIndexInitializer.cs` | Manager | Project-wide embedding bootstrap |
| `Managers/CodeChunker.cs` | Manager | Intelligent code chunking by AST boundaries |
| `Constants/EmbeddingConstants.cs` | Constants | Model IDs, dimensions, thresholds, batch sizes |

### Modified Files

| File | Change |
|------|--------|
| `Managers/TaskExecutionManager.cs` | Replace `FeatureContextResolver` call with `HybridSearchManager` (behind flag) |
| `Managers/FeatureContextResolver.cs` | Add `ResolveWithVectorHintsAsync()` method |
| `Managers/FeatureInitializer.cs` | Add Phase 8: trigger embedding index build |
| `Constants/AppConstants.cs` | Add `UseHybridSearch` flag |
| `MainWindow.xaml` | Add "Build Embeddings" button |
| `Spritely.csproj` | Add `Microsoft.Data.Sqlite` NuGet reference |

### Unchanged Files

- All existing feature JSON files (`.spritely/features/*.json`)
- `PromptBuilder.cs` / `IPromptBuilder.cs` (already accepts `featureContextBlock`)
- `FeatureRegistryManager.cs` (still handles structured data)
- `FeatureUpdateAgent.cs` (still handles post-task registry updates)
- `SignatureExtractor.cs` (still used for signature extraction)
- All prompt templates

---

## 11. Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|------------|------------|
| Voyage API unavailable / rate limited | Medium | Low | Local ONNX fallback; graceful degradation to keyword + Haiku |
| Embedding quality degrades on niche code | Low | Medium | Fusion with keyword scores compensates; Haiku fallback for low-confidence |
| SQLite corruption | Medium | Very Low | Rebuild from source files (~15s); no data loss since features are in JSON |
| Vector index drift (embeddings stale) | Low | Medium | Content hash staleness detection (same as current system); periodic reindex |
| Fusion weights produce worse results | Medium | Medium | A/B logging during Phase 3; easy to tune weights or revert to current system |
| Increased init time annoys users | Low | Medium | Embedding phase runs after feature init; progress bar; can be skipped |
| API key management complexity | Low | Low | Same pattern as Claude/Gemini keys in `%LOCALAPPDATA%\Spritely\` |
| Memory pressure from vector index | Low | Low | ~11MB for typical project; lazy loading for large projects |

---

## 12. Open Questions for Discussion

1. **Voyage vs. alternatives**: Should we also evaluate Cohere Embed v3 or Jina Embeddings v3? Both support code, but Voyage has the strongest code benchmarks.

2. **Local-first vs. API-first**: Should the local ONNX model be the primary path (free, fast, private) with Voyage as an upgrade? Or Voyage primary with ONNX as fallback?

3. **Embedding refresh frequency**: Re-embed changed files on every task completion, or batch-reindex periodically (e.g., every 10 tasks)?

4. **Image support priority**: Is multimodal embedding a Phase 1 requirement, or can it wait until the text/code path is proven?

5. **Cost ceiling**: Should there be a per-project monthly embedding budget cap? At $0.06/M tokens, a very active project might spend $0.50-1.00/month on embeddings.

6. **HNSW vs. brute force**: At what vector count should we switch from brute-force cosine to HNSW approximate search? Likely 50K+ vectors, which only mega-projects would hit.

---

## 13. Summary

The Hybrid Semantic Index is an **augmentation layer** over the existing feature system, not a replacement. It adds:

- **Semantic retrieval** via dense code embeddings (Voyage Code 3)
- **Cross-modal search** via multimodal embeddings (Voyage Multimodal 3)
- **Historical learning** from past task embeddings
- **Reduced Haiku dependency** (~70% of tasks skip Haiku, up from ~30%)
- **Better accuracy** (~92% vs ~80% relevant features in top 5)

At a marginal cost of ~$0.03 for initialization and ~$0.0001 per task, with full backward compatibility and graceful degradation.

The existing feature system's structured JSON registry, deterministic serialization, Git integration, and self-maintaining lifecycle remain unchanged and continue to serve as the authoritative source of feature metadata.
