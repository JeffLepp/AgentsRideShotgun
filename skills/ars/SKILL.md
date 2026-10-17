---
name: ars
description: Use ARS when testing or inspecting a Windows desktop app or browser without interrupting the owner's desktop. Discover its connected MCP tools before launching a GUI test.
---

# ARS

Use this workflow for agent-side Windows GUI or browser testing. ARS must be installed separately; this plugin adds guidance and does not install the app or register a second MCP server. ARS connects supported Claude Code and Codex profiles when the owner presses **Start** in setup or connects them in **Settings > Agents**.

1. Check for `ars` MCP tools, using the host's tool discovery/search if available. A configured bridge and tools exposed to this chat are separate things: the host decides which tools the session can call. If discovery still finds none, explain that this session needs its ARS connection refreshed or a new session; if the agent is not connected yet, use **ARS > Settings > Agents**. Do not register a duplicate server or claim this plugin provides computer use by itself.
2. For a GUI program or test you will inspect, launch it with ARS `run` or `open` on the workspace. For a page you will inspect, use ARS `browse`. An ordinary shell launch may place windows on the owner's desktop.
3. Inspect with `computer`, `window`, `controls`, `page`, or `look` as appropriate. Verify the result after acting; sent input alone is not proof of success.
4. If the user explicitly asks to open a result on their own desktop, use ARS's owner handoff or the agent's normal approved tool, as appropriate. Do not send an unrequested window to the owner.
5. Release workspace control when finished.

The agent desktop shares the owner's Windows account permissions. It is not a sandbox or a separate security boundary. Do not describe all agent shell activity as automatically routed through ARS.

ARS starts in the background and selects a workspace on the first workspace tool call. It cannot move a program already launched with another tool into that workspace. If `browse` reports a timeout or unconfirmed navigation, inspect with `page` or `computer` before retrying: the page may already be there. Use `computer` with `target=browser` for page coordinates and screenshots.
