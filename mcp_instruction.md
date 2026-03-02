# MCP Server Connection for Unity Agent

This project uses MCP (Model Context Protocol) for Unity-specific operations.

## Server Details

- **Server Name**: mcp-for-unity-server
- **Connection URL**: http://127.0.0.1:8080/mcp
- **Transport**: HTTP

## Requirements

1. **Unity Editor** must be running with the MCP plugin installed and enabled
2. **Port 8080** must be available (not used by other applications)

## Start Command

The MCP server is automatically started by Happy Engine when you click "Connect to MCP" on a game project card. The default command is:

```
C:\Users\fredr\.local\bin\uvx.exe --from "mcpforunityserver==9.4.7" mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools
```

## Usage

When the MCP connection is active (green "Connected" status), Claude Code can:
- Create and modify Unity prefabs
- Manipulate scenes and GameObjects
- Take screenshots of the Unity Editor or Game view
- Perform other Unity Editor operations that cannot be done through file edits alone

## Important Notes

- **Batch MCP commands** whenever possible to improve performance and reduce token usage
- The server runs as a separate process managed by Happy Engine
- Click "Disconnect" to stop the server when not needed
- Connection status is shown on each game project card with visual indicators