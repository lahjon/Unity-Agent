# FEATURE CONTEXT RESOLVER
- Match a task description to relevant features from a project registry.

## TASK DESCRIPTION
{0}

## CANDIDATE FEATURES
{1}

## INSTRUCTIONS
1. Identify which candidates are relevant to the task. Assign confidence 0.0-1.0 (1.0 = directly targeted, 0.5 = likely touched, <0.3 = tangential).
2. Only include features with confidence >= 0.3.
3. If the task introduces genuinely new functionality not covered by any candidate, set `is_new_feature` to true and provide:
   - `new_feature_id`: kebab-case, concise (e.g. "player-inventory", "auth-middleware")
   - `new_feature_name`: short display name
   - `new_feature_keywords`: 5-8 relevant search terms
4. If the task is a modification/bugfix/refactor of existing features, set `is_new_feature` to false.

## RULES
- Be selective. Most tasks touch 1-3 features.
- Prefer existing features over creating new ones. Only flag new when truly novel.
- Confidence reflects how much the feature will be read or modified, not general relatedness.
- Output ONLY valid JSON matching the required schema. No other text.
