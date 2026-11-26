# What an agent's input can and cannot do in a workspace

The workspace delivers pointer input as window messages on its own desktop; the real cursor never moves
(`AgentDesktop.Computer.cs`, `AgentDesktop.cs` `Click`). "Probe" names the engine-probe check that covers it.

Reproduced reliably:
- Click in a window's client area, left or right. WPF buttons are pressed through UI Automation, because WPF hit-tests the real pointer (`--click-proof`, `--agent-view`).
- Caption buttons by click: minimize, maximize/restore, close (posted as `WM_SYSCOMMAND`). A click on a title bar brings the window to the front. Not probe-verified.
- Double-click and drag inside one window's client area. Every `drag` and `double_click` on the desktop target reports its window's rect before and after, and whether it moved, resized, was maximized or restored (engine-probe suite, `WindowEffectProbe`). WPF windows may ignore them, for the reason above.
- Vertical scroll in a native window, and both axes in the browser target.
- `window` with action=move, maximize, minimize, restore, front or close.
- Keys: one supported key with CTRL/SHIFT/ALT in the browser target; natively, single keys and CTRL+A in edit controls.

Not reproduced yet:
- Moving or resizing a window by dragging its title bar or an edge (custom-drawn title bars included): the window stays put, and the receipt says so. Use `window action=move`.
- Double-clicking a standard (non-client) title bar: the action is refused rather than maximizing. Use `window action=maximize`.
- OS drag-and-drop between windows or apps, and a drag that leaves the window it started in (refused).
- The Windows taskbar, Start menu and notification area: there is no Windows shell on the workspace. ARS's own taskbar strip is the owner's and does not appear in agent screenshots.
- Native modifier chords other than CTRL+A, Windows-key shortcuts, and horizontal native scroll.
