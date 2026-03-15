# CLAUDE.MD GENERATION
Generate a CLAUDE.md file for this project based on actual codebase exploration.

## STEP 1 — EXPLORE (use tools, do NOT guess)
- List top-level directory structure.
- Read project configs (.csproj, package.json, Cargo.toml, pyproject.toml, etc.).
- Read main entry point and 3-5 key source files.
- Identify architecture, patterns, major components, coding conventions.

## STEP 2 — OUTPUT
Output ONLY the raw markdown content for CLAUDE.md. No wrapping tags, no code fences, no commentary.

The file should include these sections (omit empty ones):

# Project Overview
One paragraph: what it is, what it does, tech stack.

# Architecture
- Key directories and their purpose
- Major components/modules and their roles
- Design patterns used (DI, MVVM, etc.)

# Coding Conventions
- Language style preferences observed in codebase
- Naming conventions (files, classes, methods, variables)
- Import/using patterns

# Key Files
- Entry points
- Configuration files
- Core modules

# Development
- Build commands (if discoverable from config)
- Test commands (if discoverable from config)

## RULES
- Base ENTIRELY on actual files read, not assumptions.
- Keep it concise and useful — aim for 40-80 lines.
- Use bullet points for lists, keep prose minimal.
- Do NOT include instructions about how to use Claude or AI tools.
- Do NOT include boilerplate or placeholder text.
- Output raw markdown only — no wrapping, no code fences.
