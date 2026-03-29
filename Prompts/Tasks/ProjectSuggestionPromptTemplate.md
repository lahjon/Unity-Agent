<suggestion_task>
# PROJECT SUGGESTION
- Explore codebase and suggest improvements.

## STEP 1 — EXPLORE
- List top-level directory structure.
- Read key source files, configs, entry points.
- Understand architecture, patterns, current state.

## STEP 2 — SUGGEST
Focus on: {0}

Generate 5-8 actionable suggestions. Each:
- Title: short, starts with action verb (Add/Refactor/Fix/Implement).
- Description: 2-4 sentences as implementation instructions — files to change, code to write, expected outcome. No analytical observations.
- Files: list the specific files (relative paths) that should be read/modified to implement the suggestion. Include only files that actually exist in the codebase.

## STEP 3 — OUTPUT
- Output ONLY a JSON object matching the required schema: {{"suggestions": [{{"title": "...", "description": "...", "files": ["path/to/file.cs", ...]}}]}}
- No other text, no markdown fences.
</suggestion_task>
