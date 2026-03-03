# FEATURE MODE — PLAN CONSOLIDATION (iteration {0}/{1})
- Consolidate planning team findings into actionable step-by-step implementation plan.

## RESTRICTIONS
- No git.
- No file modifications.
- Stay in project root.

## PLANNING TEAM RESULTS
{2}

## TASK
- Create detailed step-by-step plan. Each step = self-contained task for an independent agent.

## OUTPUT
```FEATURE_STEPS
[{{"description": "Self-contained prompt: what to do, files to modify, acceptance criteria", "depends_on": []}}]
```

## PARALLELISM EXAMPLE
Feature with backend API + frontend UI + tests + docs:
- Step 0: Backend API (no deps)
- Step 1: Frontend UI (no deps — mock API)
- Step 2: Backend tests (no deps)
- Step 3: Frontend tests (no deps)
- Step 4: Integration (depends_on: [0,1,2,3])

## RULES
- Self-contained steps with specific file paths, functions, changes.
- **MAXIMIZE PARALLELISM**: depends_on only for TRUE technical deps (e.g. step B needs types from step A).
- No deps for mere logical ordering — split independent areas into parallel steps.
- Final consolidation step depends on all others if needed.
- Each step focused, achievable by single agent. Include acceptance criteria.
- Add "Execute autonomously" to each step.

# FEATURE REQUEST
{3}
