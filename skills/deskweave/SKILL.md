---
name: deskweave
description: Use Deskweave's connected MCP tools when testing or inspecting a Windows desktop app or browser without interrupting the owner's desktop.
---

# Deskweave

Use this workflow only when the user wants agent-side Windows GUI or browser testing and the Deskweave MCP server is connected. Deskweave must be installed separately; this plugin adds guidance and does not install the app or register a second MCP server. The Deskweave app connects Claude Code when the owner presses **Start** in setup or connects it in **Settings > Agents**. An existing Claude Code session may need a restart.

1. Check that the `deskweave` MCP tools are available. If they are absent, tell the user to open Deskweave, connect Claude Code in Settings, and start a new session. Do not claim that this plugin alone provides computer use.
2. For a GUI program or test you will inspect, launch it with Deskweave `run` or `open` on the workspace. For a page you will inspect, use Deskweave `browse`. An ordinary shell launch may place windows on the owner's desktop.
3. Inspect with `computer`, `window`, `controls`, `page`, or `look` as appropriate. Verify the result after acting; sent input alone is not proof of success.
4. If the user explicitly asks to open a result on their own desktop, use Deskweave's owner handoff or the agent's normal approved tool, as appropriate. Do not send an unrequested window to the owner.
5. Release workspace control when finished.

The agent desktop shares the owner's Windows account permissions. It is not a sandbox or a separate security boundary. Do not describe all agent shell activity as automatically routed through Deskweave.