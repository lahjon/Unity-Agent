# CODEBASE EXPLORATION
- Explore project source code, then produce token-efficient description.

## STEP 1 — EXPLORE (use tools, do NOT guess)
- List top-level directory structure.
- Read project configs (.csproj, package.json, Cargo.toml, etc.).
- Read main entry point and key source files.
- Identify architecture, patterns, major components.

## STEP 2 — OUTPUT
Output EXACTLY this format:

<short>
One-line: what it is + tech stack. Max 200 chars. No preamble.
</short>

<long>
Terse bullet-points (no filler):
- Purpose: [what project does]
- Stack: [languages, frameworks, runtime]
- Architecture: [MVVM/MVC/microservices/etc.]
- Key dirs: [top-level source dirs]
- Components: [major classes/modules + roles]
- Patterns: [DI, async, conventions, etc.]
Max 800 chars. Omit empty sections. No preamble.
</long>

## RULES
- Base on actual files read, not assumptions.
- Output ONLY <short> and <long> tags.
- No conversational text or commentary.
- Factual summaries, not agent responses.
