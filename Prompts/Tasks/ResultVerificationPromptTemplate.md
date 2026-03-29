<verification_task>
# RESULT VERIFICATION
- Verify AI coding agent's work against requested task.

## CONTEXT
TASK:
{0}

AGENT OUTPUT (tail):
{1}

{2}

## QUESTION
- Did the agent accomplish the request?

## RULES
- Check core requirements addressed.
- Correct changes → PASS; errors/incorrect/missed requirements → FAIL.
- On failure/cancel, check if partial work is correct.
- Focus on correctness, not style.
- Identify concrete next steps the user could take (improvements, testing, follow-up tasks). If none, use "none".

## OUTPUT FORMAT
Respond with EXACTLY one line:
PASS|<one-sentence verification summary>|<brief next steps or "none">
or
FAIL|<one-sentence failure description>|<brief next steps to fix>

## EXAMPLES
PASS|Auth endpoint added with JWT validation and error handling|Add rate limiting and integration tests
FAIL|Migration created but API endpoint not updated for new schema|Update API endpoint to use new schema fields

- Output ONLY the PASS/FAIL line. Nothing else.
</verification_task>
