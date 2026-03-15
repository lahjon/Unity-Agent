# GAME PROJECT EXPLORATION
- Explore game project, then produce token-efficient description.

## STEP 1 — EXPLORE (use tools, do NOT guess)
- List top-level directory structure.
- Check for Unity (ProjectSettings/, Assets/), Unreal (*.uproject), other engines.
- Read key configs (ProjectSettings/ProjectSettings.asset, *.uproject, project.json, etc.).
- Find main game scripts in Scripts/, Source/, or similar.
- Identify game type, genre, key systems.

## STEP 2 — OUTPUT
Output EXACTLY this format:

<short>
One-line: game genre/type + engine. Max 200 chars. No preamble.
</short>

<long>
Terse bullet-points (no filler):
- Game: [type/genre + description]
- Engine: [Unity/Unreal/Godot/custom/etc.]
- Platform: [targets if identifiable]
- Key dirs: [Assets/, Scripts/, etc.]
- Systems: [major gameplay systems]
- Tech: [rendering, multiplayer, physics, etc.]
Max 800 chars. Omit empty sections. No preamble.
</long>

## RULES
- Focus on game-specific aspects, not generic code structure.
- Skip binary folders (Library/, Temp/, Builds/).
- Output ONLY <short> and <long> tags.
- Factual summaries, not agent responses.
