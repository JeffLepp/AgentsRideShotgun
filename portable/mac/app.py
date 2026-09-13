"""Deskweave.app: the local broker in a thread, its viewer in a native window.

Given arguments it is the ordinary CLI, so an agent's MCP command can be
`/Applications/Deskweave.app/Contents/MacOS/Deskweave mcp`.
"""
import asyncio
import html
import os
import sys
import threading
import time

from deskweave import cli
from deskweave.mcp import http_json


def main():
    argv = [arg for arg in sys.argv[1:] if not arg.startswith("-psn_")]  # Finder can add a process serial number
    if argv:
        return cli.main(argv)
    import webview

    # ponytail: scale 2 assumes a Retina screen; a non-Retina display pays 4x frame pixels for nothing.
    args = cli.build_parser().parse_args(["serve", "--backend", "browser", "--scale", "2"])
    connection = args.data_dir / "connection.json"
    failure = []

    def serve():
        try:
            asyncio.run(cli.serve(args))
        except Exception as exc:  # a .app has no terminal, so the window shows it
            failure.append(str(exc) or type(exc).__name__)

    broker = threading.Thread(target=serve, name="broker", daemon=True)
    broker.start()
    data = None
    while data is None and broker.is_alive():
        time.sleep(0.05)
        try:
            candidate = cli.load_connection(connection)
            data = candidate if candidate.get("pid") == os.getpid() else None  # not a file left by a crash
        except (OSError, ValueError):
            pass

    if data is None:
        message = html.escape(failure[0] if failure else "The workspace service stopped before it was ready.")
        webview.create_window("Deskweave", width=520, height=220, html=(
            '<body style="font:14px -apple-system,system-ui,sans-serif;padding:8px 24px">'
            f"<h3>Deskweave could not start</h3><p>{message}</p></body>"))
        webview.start()
        return 1

    def shutdown():
        # Menu and Dock Quit exit the process right after this handler returns, so the
        # broker has to finish here or its browser is orphaned. Accepted actions may take 60 s.
        if not broker.is_alive():
            return
        try:
            http_json(data["url"] + "/api/shutdown", data["ownerToken"], {})
        except Exception:
            return  # ponytail: broker unreachable, so nothing to wait for; its daemon thread dies with us
        broker.join(70)

    window = webview.create_window("Deskweave", f"{data['url']}/#token={data['ownerToken']}",
                                   width=1200, height=820, min_size=(760, 520))
    window.events.closing += shutdown
    webview.start()
    shutdown()  # Cmd+Q inside the page stops the app loop without a closing event
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
