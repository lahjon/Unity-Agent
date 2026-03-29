<commit_message_task>
You are generating a git commit message for an automated coding task.

TASK DESCRIPTION:
{0}

GIT DIFF (numstat — lines added/removed per file):
{1}

GIT DIFF (patch — actual changes):
{2}

Generate a semantic commit message in this exact format:

1. A bold summary line (using **bold**) describing the main architectural or functional change, followed by an arrow (→) and a brief explanation of the impact.
2. A blank line.
3. A **Files changed:** header.
4. One bullet per modified file: `- \`path/to/file\` → brief description of what changed in that file`

Rules:
- The summary line must describe WHAT changed and WHY, not just "updated files"
- Each file bullet must describe the semantic change, not just "modified" or line counts
- Keep file descriptions to one short sentence
- If there are more than 12 files, group minor changes (test files, config) into a single "minor changes" bullet
- Do NOT include token counts, duration, cost, or other metrics
- Do NOT use markdown headers (#) — only bold (**) for the summary line
- Output ONLY the commit message, nothing else
</commit_message_task>
