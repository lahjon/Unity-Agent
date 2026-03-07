# FEATURE REGISTRY INITIALIZATION
- Analyze a project and produce a comprehensive feature registry.

## PROJECT TYPE
{0}

## DIRECTORY TREE
{1}

## CODE SIGNATURES
{2}

## INSTRUCTIONS
Identify every logical feature or module in this project. A "feature" is a cohesive unit of functionality that a developer would think of as one concern — not a file, not a directory, but a logical responsibility.

For each feature provide:
- `id`: kebab-case identifier (e.g. "user-authentication", "task-orchestration", "inventory-system")
- `name`: short human-readable display name
- `description`: 1-2 sentences explaining what this feature does and why it exists
- `category`: one of "core", "ui", "data", "integration", "infrastructure", "testing", "config"
- `keywords`: 5-10 search terms a developer might use when looking for this feature (include class names, concepts, and domain terms)
- `primary_files`: files that are central to this feature's implementation (the files you would read first to understand it)
- `secondary_files`: supporting files (tests, configs, styles, shared utilities used by this feature)
- `related_feature_ids`: ids of other features this one depends on or closely interacts with
- `key_types`: compact type declarations central to this feature (e.g. "class TaskExecutionManager", "interface IPromptBuilder", "enum TaskStatus")
- `patterns`: architectural patterns or invariants this feature relies on (e.g. "Manager pattern with async lifecycle", "Fire-and-forget post-completion hook")
- `dependencies`: cross-feature and external dependencies as freeform strings (e.g. "Reads project config from ProjectManager", "Calls Claude CLI via Process")

## GUIDANCE ON FEATURE BOUNDARIES
- Group by **logical responsibility**, not by directory. A feature like "git-operations" may span files in Managers/, Models/, and Controls/.
- Avoid overly granular features. A single utility class is not a feature — it belongs to the feature it serves.
- Avoid overly broad features. "The entire backend" is not a feature. Break it into distinct concerns.
- Good size: a feature typically has 2-15 primary files. If you have 1 file, it probably belongs to another feature. If you have 20+, consider splitting.
- Entry points, configuration, and bootstrapping can be their own feature (e.g. "app-startup", "dependency-injection").
- UI features should be separate from their underlying logic features (e.g. "git-ui" vs "git-operations").
- Test files are secondary files of the feature they test, not their own feature.

## RULES
- Be thorough. Cover the entire project — every significant file should appear in at least one feature.
- Aim for 8-30 features depending on project size. Small projects may have fewer.
- Use relative file paths as they appear in the directory tree.
- Output ONLY valid JSON matching the required schema. No other text.
