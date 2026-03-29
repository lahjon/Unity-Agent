<evaluation_phase>
# TEAMS MODE — EVALUATION (iteration {0}/{1})
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
3. Run `dotnet build` to verify the project compiles. Build errors = NEEDS_MORE_WORK.
4. Fix issues directly if found (minor fixes only).
5. Produce a **structured gap analysis** for any remaining work.
6. If previous iteration history is provided above, compare: did this iteration make measurable progress? Were previous gaps addressed?

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

## STATUS OUTPUT — CRITICAL
You MUST carefully evaluate the actual state of the implementation before choosing a status.
Do NOT default to COMPLETE. Only output COMPLETE if you have verified all requirements are met.

End with exactly ONE of:
- `STATUS: COMPLETE` — ALL requirements fully implemented and verified working. No gaps found.
- `STATUS: COMPLETE WITH RECOMMENDATIONS` — ALL requirements implemented and working, only minor optional improvements possible.
- `STATUS: NEEDS_MORE_WORK` — ANY missing functionality, bugs, integration issues, or unverified requirements exist. Include gap analysis above with specific remaining issues.

**Default to `STATUS: NEEDS_MORE_WORK`** unless you can confirm every aspect of the feature request is fully implemented and working. If in doubt, choose NEEDS_MORE_WORK.
</evaluation_phase>
