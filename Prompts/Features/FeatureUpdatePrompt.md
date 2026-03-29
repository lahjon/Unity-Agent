<feature_update>
# FEATURE REGISTRY UPDATE
- Update feature registry after task completion based on what changed.

## TASK DESCRIPTION
{0}

## COMPLETION SUMMARY
{1}

## CHANGED FILES
{2}

## CURRENT FEATURE INDEX
{3}

## INSTRUCTIONS
1. Map each changed file to existing features it belongs to. Use `updated_features` to add/remove files.
   - `add_primary_files`: files central to the feature's implementation.
   - `add_secondary_files`: files that support/configure the feature (tests, configs, styles).
   - `remove_files`: files deleted or no longer part of the feature.
   - `updated_description`: only if the feature's purpose materially changed. Omit otherwise.
2. If a genuinely new feature was introduced (not just extending an existing one), add it to `new_features` with:
   - `id`: kebab-case (e.g. "settings-panel")
   - `name`, `description` (1-2 sentences), `category`, `keywords` (5-8 terms)
   - `primary_files`, `secondary_files`: from the changed files list
3. Files may belong to multiple features. Not every changed file needs assignment.

## RULES
- Prefer updating existing features over creating new ones.
- Only create a new feature when the task introduced a distinct, self-contained capability.
- Omit empty arrays. Only include features that actually changed.
- Output ONLY valid JSON matching the required schema. No other text.
</feature_update>
