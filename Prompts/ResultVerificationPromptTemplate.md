Verify AI coding agent's work against requested task.

TASK:
{0}

AGENT OUTPUT (tail):
{1}

{2}Did the agent accomplish the request?

Rules:
- Check core requirements addressed
- Correct changes → PASS; errors/incorrect/missed requirements → FAIL
- On failure/cancel, check if partial work is correct
- Focus on correctness, not style

Respond with EXACTLY one line:
PASS|<one-sentence verification summary>
or
FAIL|<one-sentence failure description>

Examples:
PASS|Auth endpoint added with JWT validation and error handling
FAIL|Migration created but API endpoint not updated for new schema

Output ONLY the PASS/FAIL line. Nothing else.
