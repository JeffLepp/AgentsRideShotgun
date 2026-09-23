# Deskweave MCP bundle

This Windows x64 connector lets an MCP client use an existing Deskweave installation. It does not install Deskweave or a second copy of its agent desktop.

1. Install the signed Deskweave app from the official GitHub release and open it once. The app installs per user at `%LOCALAPPDATA%\DeskweaveApp`.
2. In Deskweave, press **Start** to connect supported agents, or use **Settings > Agents**. Existing sessions may need to reconnect.
3. If your MCP client supports MCPB, install this bundle. It starts the installed Deskweave bridge with the correct per-user router ticket. For Claude Code and Codex, Deskweave's own connection already supplies these tools; installing this bundle there can create a duplicate connection, so use one connection route per client.

The separate desktop is under your current Windows account and shares its files and permissions. It is not a VM or security sandbox. The connector sends no data to an external service; your chosen agent provider may receive tool results and screenshots. The connector writes only startup errors to stderr, leaving MCP stdout for the bridge.