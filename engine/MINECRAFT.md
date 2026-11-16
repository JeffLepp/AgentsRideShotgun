# Minecraft preview provider (development)

ARS's native `PrintWindow` capture can show a black Minecraft OpenGL client even
when the game is rendering correctly. The opt-in provider uses a separately
installed, game-specific bridge. It does not inject code or launch Minecraft.

Place the bridge's connection JSON in the workspace folder as
`minecraft-bridge.json`. Protocol 1 requires `gamePid`, `port`, and `token`.
Treat this local file as a credential; never include it in logs or screenshots.
Delete it to disable the provider. No settings or other workspaces are changed.

The provider only selects a window enumerated on that private workspace whose
Windows process ID matches the connection. It requests `http://127.0.0.1:PORT/frame`
with bearer authentication, no proxy or redirects. The bridge must return JPEG,
`X-Game-Pid`, and `X-Frame-Age-Ms` measured in the game's monotonic clock. This is
not a general network capture endpoint. A stale or failed configured game feed is
labelled unavailable; it never silently falls back to the owner's screen.

Window, composite desktop and corner captures share this path. The JPEG is
drawn into the game window's client area, preserving workspace coordinates.
HTTP and image decoding run off the desktop pump, with one pending fetch and at
most ten attempts per second. A cold cache initially shows an unavailable label;
the next preview tick uses the frozen image when it arrives. Config revisions
invalidate late results. An existing invalid config remains an explicit capture
failure, including on cold start, without guessing a window PID.

Transport is bounded to 250 ms and 4 MiB. Preview frames older than 1500 ms are
rejected. Preview is separate from the policy's direct, higher-rate observation
stream. This provider supplies images; it does not turn native window-message
input into a Minecraft controller or record the human's raw inputs.

`Deskweave.Probe --minecraft-live-preview OUTPUT` must run through ARS in the
existing private game workspace. It refuses the Default desktop and writes
window/composite PNGs plus the exact loaded engine path for independent readback.
Its success verifies the rebuilt engine, not an unchanged installed controller.
Restarting the controller is required to load an installed engine update.
ARS 0.3.4's supported update/restart commands defer until all workspaces stop;
save and close game/app work before stopping a workspace. Explicit owner Quit
still closes workspace apps after confirmation. See [lifecycle policy](LIFECYCLE.md).

This development integration has been tested with Minecraft 26.3's main game
window. Additional windows from the same game PID, other versions, raw human-input
recording and native-message gameplay control are not certified by these tests.
