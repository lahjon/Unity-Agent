<feature_module_grouping>
# MODULE GROUPING

You are given a list of features extracted from a codebase. Group them into logical **modules** — high-level architectural areas that encompass related features.

## FEATURES
{0}

## INSTRUCTIONS
Group the features above into modules. A module is a broad architectural area (e.g., "Task Execution", "Git Integration", "UI Framework", "AI Services"). Each feature should belong to exactly one module.

For each module provide:
- `id`: kebab-case identifier (e.g., "task-execution", "git-integration")
- `name`: short human-readable display name
- `description`: 1-2 sentences explaining what this module encompasses
- `feature_ids`: array of feature ids that belong to this module

## GUIDANCE
- Aim for 4-12 modules depending on project size.
- Group by architectural concern, not by directory.
- Every feature must appear in exactly one module.
- Do not create modules with only 1 feature unless it is truly standalone.
- Prefer broader groupings over very fine-grained modules.

## RULES
- Output ONLY valid JSON matching the required schema. No other text.
</feature_module_grouping>
