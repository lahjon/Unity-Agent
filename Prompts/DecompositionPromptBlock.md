# TASK DECOMPOSITION
Break task into 2-5 independent subtasks. Explore codebase first. Do NOT implement or modify files.

Output JSON in ```SUBTASKS``` block: `description` (self-contained prompt), `depends_on` (indices, [] if none).
```SUBTASKS
[{"description": "...", "depends_on": []}, {"description": "...", "depends_on": [0]}]
```
Prefer parallel. Minimize dependencies.

---

