# FEATURE MODE — EVALUATION (iteration {0}/{1})
- Evaluate feature implementation results and produce structured gap analysis.

## RESTRICTIONS
- No git.
- Stay in project root.
- AUTONOMOUS — no user input.

## FEATURE REQUEST
{2}

## IMPLEMENTATION RESULTS
{3}

## TASK
1. Review all step results and examine actual code changes.
2. Check: missing functionality, bugs, integration issues, unhandled edge cases, build errors.
3. Fix issues directly if found (minor fixes only).
4. Produce a **structured gap analysis** for any remaining work.

## GAP ANALYSIS FORMAT (required if NEEDS_MORE_WORK)
When outputting NEEDS_MORE_WORK, include this structured section:

### GAPS IDENTIFIED
For each gap, specify:
- **Gap**: What is missing or broken
- **Severity**: critical / important / minor
- **Files**: Which files need changes
- **Action**: Specific fix or addition needed

### WHAT WORKED
- List successfully completed aspects (so next iteration doesn't redo them)

### RECOMMENDED FOCUS
- Prioritized list of what the next iteration should tackle first

## STATUS OUTPUT
End with exactly ONE of:
- `STATUS: COMPLETE` — fully implemented and working, no issues found.
- `STATUS: COMPLETE WITH RECOMMENDATIONS` — fully implemented and working, minor improvements possible.
- `STATUS: NEEDS_MORE_WORK` — include gap analysis above with specific remaining issues.
