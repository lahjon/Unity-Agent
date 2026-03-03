# MCP
- Server: mcp-for-unity-server @ http://127.0.0.1:8080/mcp (Unity Editor ops).
- WORKFLOW: Scene→Prefab (create GameObjects in scene, save as Prefabs). No script generation for scene construction.
- BATCH ALL: Use `batch_execute` for multiple ops (10-100x faster). Plan first, execute once.
