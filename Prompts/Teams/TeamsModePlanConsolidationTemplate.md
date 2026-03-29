<consolidation_phase>
# TEAMS MODE — PLAN CONSOLIDATION (iteration {0}/{1})
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

## REQUIRED OUTPUT — MANDATORY
Your ONLY job is to produce the ```TEAM_STEPS``` block below. This is machine-parsed. Without it, the orchestrator cannot proceed and the entire pipeline fails.
```TEAM_STEPS
[{{"description": "Self-contained prompt: what to do, files to modify, acceptance criteria", "depends_on": []}}]
```
You MUST output this block. Do NOT output just a status marker — the TEAM_STEPS block IS the deliverable.
**CRITICAL**: Do NOT output `STATUS: COMPLETE` or any `STATUS:` marker. Those are for other phases. This phase's ONLY deliverable is the ```TEAM_STEPS``` block above. If you output a STATUS marker without TEAM_STEPS, the orchestrator will fail.

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

## PROGRESSIVE IMPROVEMENT (iteration {0})
- If iteration > 1, previous iteration history is provided above.
- **DO NOT** create steps for work already completed in previous iterations.
- **FOCUS** steps on remaining gaps and unresolved issues from previous evaluations.
- Each step description MUST reference what it's fixing/improving from prior work if applicable.
- Verify existing work first before re-implementing — add verification checks to steps.

# FEATURE REQUEST
{3}
</consolidation_phase>
