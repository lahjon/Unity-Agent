# FEATURE MODE — PLAN CONSOLIDATION (iteration {0}/{1})
- Consolidate planning team findings into actionable step-by-step implementation plan.

## RESTRICTIONS
- No git.
- No file modifications.
- Stay in project root.

## PLANNING TEAM RESULTS
{2}

## TASK
- Create a detailed step-by-step plan. Each step = self-contained task for an independent agent.
- Scale dynamically: use as many steps as the feature requires (no artificial limits).
- Small features may need 2-3 steps; large features may need 10+.

## OUTPUT
```FEATURE_STEPS
[{{"description": "Self-contained prompt: what to do, files to modify, acceptance criteria", "depends_on": []}}]
```

## PARALLELISM — CRITICAL
Maximize parallel execution. Use a **layered** approach:
- **Layer 0** (no deps): All independent work — most steps should be here
- **Layer 1** (depends on layer 0): Integration, wiring, tests that need layer 0 outputs
- **Layer 2+** (depends on earlier layers): Final consolidation only if needed

### Example — Feature with backend API + frontend UI + tests:
- Step 0: Backend API models & data layer (no deps)
- Step 1: Backend API endpoints (no deps — use interfaces/contracts)
- Step 2: Frontend UI components (no deps — mock API)
- Step 3: Backend unit tests (no deps)
- Step 4: Frontend unit tests (no deps)
- Step 5: Integration wiring (depends_on: [0,1,2])
- Step 6: Integration tests (depends_on: [5])

### Anti-pattern — Sequential chain (AVOID):
- Step 0 → Step 1 → Step 2 → Step 3 (each depends on previous = no parallelism)

## RULES
- Self-contained steps with specific file paths, functions, changes.
- **depends_on ONLY for TRUE technical deps** (e.g. step B needs types/interfaces from step A).
- No deps for mere logical ordering — split independent areas into parallel steps.
- Each step focused, achievable by single agent. Include acceptance criteria.
- Add "Execute autonomously" to each step.
- Prefer more granular parallel steps over fewer sequential ones.

# FEATURE REQUEST
{3}
