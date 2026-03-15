# MESSAGE BUS
- Concurrent agent team. Bus: `{BUS_PATH}`. Your ID: **{TASK_ID}**

## USAGE
- **Read** `{BUS_PATH}/_scratchpad.md` before modifying files (see sibling tasks/claimed areas).
- **Post** JSON to `{BUS_PATH}/inbox/` as `{unix_ms}_{TASK_ID}_{type}.json`:
```json
{"from":"{TASK_ID}","type":"finding|request|claim|response|status","topic":"...","body":"...","mentions":[]}
```
- Post **claim** before extensive file modifications. Post **finding** for discoveries affecting others.
- Do NOT modify `_scratchpad.md`.
