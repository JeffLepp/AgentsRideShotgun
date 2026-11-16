# Optional Windows-MCP development tool

Windows-MCP is a separate desktop automation server, not an ARS dependency.
Version 0.8.9 was evaluated on November 16, 2026 from its published PyPI wheel
(SHA-256 `e90e5b02b3e545991f203ce93cefb436a326861657968591653fa254105dc85d`).

The isolated `uv` tool installation requires Python 3.14:

```powershell
uv tool install --python 3.14 windows-mcp==0.8.9
windows-mcp --help
```

Installation alone does not start a server, register an MCP entry, or install
its login task. The CLI and all 21 tool definitions loaded during this check;
desktop input and screenshots were not exercised. No performance or agent
quality comparison was made.

The stock server initializes desktop input hooks, a control indicator, and an
accessibility watchdog when it starts. Its desktop tools operate on the session
where the server runs. Registering it beside ARS gives an agent another route
for clicking, typing, capture and application control that does not have ARS's
workspace routing. Server prefixes distinguish tool names but do not prevent
the model from choosing that route. The independent Claude2 review reached the
same routing recommendation.

Keep it in a separate, explicit development profile when evaluating it. Do not
add it to ARS's automatic provider configuration, installer or background-only
agent profiles. It is not a viewer for ARS's private desktops. Keep it stopped
during ARS focus and isolation checks. If a separate evaluation needs a running
server, `ANONYMIZED_TELEMETRY=false` opts out of its default analytics.

To remove the optional tool installation:

```powershell
uv tool uninstall windows-mcp
```

See the [upstream project](https://github.com/CursorTouch/Windows-MCP) for current
behavior and configuration. These observations apply to the pinned version;
future versions need a fresh compatibility check.
