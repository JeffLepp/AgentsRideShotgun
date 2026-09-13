# Third-party components

This private source kit includes unmodified noVNC 1.7.0 core/vendor modules from
https://github.com/novnc/noVNC/releases/tag/v1.7.0. The upstream archive URL, SHA-256 and
each included file hash are recorded in `scripts/novnc-lock.json`. Upstream copyright,
authors, MPL-2.0, BSD and bundled pako MIT notices are preserved under `web/vendor/novnc/`.
The license index is `web/vendor/novnc/LICENSE.txt`. No upstream application art or font
is used. `scripts/vendor_novnc.py` reproduces the static subset; Node and websockify are
not runtime requirements.

Python dependencies are installed from PyPI using the explicit versions in requirements.txt:
aiohttp (Apache-2.0/selected MIT portions), psutil (BSD-3-Clause), Pillow (HPND), and
websocket-client (Apache-2.0), together with their dependencies. Installed distributions
carry their own license metadata. The source kit does not redistribute the Python wheels.

The optional Debian container installs Debian packages with their copyright/license files
in `/usr/share/doc/`. These include TigerVNC (GPL-2.0), Openbox (GPL-2.0-or-later), xdotool
(BSD-style), xterm (X/MIT), Chromium (BSD and third-party notices), and their dependencies.
The image recipe pins the Debian base digest; apt repository contents can change, so record
the built image ID and package versions when testing. No claim of a reproducible apt snapshot
is made. This kit is private test distribution; public packaging needs a separate complete
distribution and source-offer review of the exact artifacts being shipped.
