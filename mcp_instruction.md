# MCP Server Connection for Unity Agent

This project uses MCP (Model Context Protocol) for Unity-specific operations.

## Server Details

- **Server Name**: mcp-for-unity-server
- **Connection URL**: http://127.0.0.1:8080/mcp
- **Transport**: HTTP

## Requirements

1. **Unity Editor** must be running BEFORE attempting to connect
   - Happy Engine will check if Unity is running and show a warning if it's not
   - Start Unity Editor first, then click "Connect to MCP"
2. **MCP plugin** must be installed and enabled in the Unity Editor
3. **Port 8080** must be available (not used by other applications)

## Start Command

The MCP server is automatically started by Happy Engine when you click "Connect to MCP" on a game project card. The default command is:

```
%USERPROFILE%\.local\bin\uvx.exe --from "mcpforunityserver==9.4.7" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools
```

## Usage

When the MCP connection is active (green "Connected" status), Claude Code can:
- Create and modify Unity prefabs
- Manipulate scenes and GameObjects
- Take screenshots of the Unity Editor or Game view
- Perform other Unity Editor operations that cannot be done through file edits alone

**PREFERRED WORKFLOW**:
- Create GameObjects directly in the current Unity scene
- Configure components and properties using MCP commands
- Convert configured GameObjects to Prefabs for reusability
- This follows the Unity best practice of working visually in the scene

**IMPORTANT**: Never generate C# scripts for GameObject creation or scene setup. Always use MCP operations instead. Scripts should only handle gameplay logic, not scene construction.

## Important Notes

- **CRITICAL: Always use batch_execute** - Plan all MCP operations ahead and execute them in a single batch
  - Example: Creating 5 GameObjects? Use ONE batch_execute with 5 operations, not 5 separate calls
  - Performance improvement: 10-100x faster with batching
  - Review all intended operations first, then batch execute
- The server runs as a separate process managed by Happy Engine
- Click "Disconnect" to stop the server when not needed
- Connection status is shown on each game project card with visual indicators