"""Opt-in real installed-Chromium + broker + visible viewer regression gate.

Run from portable/: python tests/integration_browser.py --output artifacts/browser-ui
Uses two owned headless profiles and a loopback fixture; never a personal browser.
"""
from __future__ import annotations

import argparse
import asyncio
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys
import tempfile
import time
import traceback

import aiohttp
from aiohttp import web
import psutil

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from deskweave.browser import BrowserWorkspace
from deskweave.server import Broker, make_app


FIXTURE = """<!doctype html><meta charset=utf-8><title>Deskweave local fixture</title>
<style>*{box-sizing:border-box}body{margin:0;background:#f4f5f7;color:#293644;font:20px system-ui;height:1800px}
header{padding:24px 30px;background:#354557;color:white}main{padding:32px}h1{font-size:32px;margin:0 0 12px}
input{position:absolute;left:32px;top:170px;width:350px;height:44px;font:20px system-ui;padding:8px;border:2px solid #7b8896}
button{position:absolute;left:32px;top:230px;height:44px;width:150px;background:#354b64;color:white;border:0;font:20px system-ui}
output{position:absolute;left:32px;top:304px;color:#293644}aside{position:absolute;left:32px;top:380px;font-size:15px;color:#586474}</style>
<header>LOCAL FIXTURE / NO ACCOUNT</header><main><h1>Make room for your work.</h1><p>Type a note and apply it.</p></main>
<input id=editor aria-label="Fixture note"><button id=apply>Apply note</button><output id=result>Waiting for your note</output>
<aside>Controlled through the Deskweave viewer. This fixture stays on loopback.</aside>
<script>apply.onclick=()=>{result.textContent=editor.value;localStorage.setItem('note',editor.value);document.body.style.background='#e9edf2'};</script>"""


class Gate:
    def __init__(self, output: Path):
        self.output = output.resolve()
        self.output.mkdir(parents=True, exist_ok=True)
        self.report = {"startedAt": datetime.now(timezone.utc).isoformat(), "platform": sys.platform,
                       "claims": [], "screenshots": [], "ok": False,
                       "limits": ["Windows host evidence only; native Mac/Linux hardware remains untested.",
                                  "The test viewer uses a second browser. Memory is not a 4 GB laptop benchmark."]}
        self.started = time.monotonic()

    def save(self):
        self.report["elapsedSeconds"] = round(time.monotonic() - self.started, 3)
        (self.output / "report.json").write_text(json.dumps(self.report, indent=2, ensure_ascii=False), encoding="utf-8")

    def check(self, name, condition, detail=None):
        self.report["claims"].append({"name": name, "passed": bool(condition), **({"detail": detail} if detail is not None else {})})
        self.save()
        if not condition:
            raise AssertionError(name + (": " + str(detail) if detail is not None else ""))

    async def evaluate(self, workspace, expression):
        result = await asyncio.to_thread(workspace._connection().call, "Runtime.evaluate",
                                         {"expression": expression, "returnByValue": True, "awaitPromise": True})
        if "exceptionDetails" in result:
            raise AssertionError(result["exceptionDetails"].get("text", "JavaScript evaluation failed"))
        return result.get("result", {}).get("value")

    async def until(self, workspace, expression, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if await self.evaluate(workspace, expression):
                return
            await asyncio.sleep(0.08)
        visible = await self.evaluate(workspace, "document.body.innerText.slice(0, 3500)")
        raise AssertionError(f"Visible state did not arrive: {expression}\n{visible}")

    async def click(self, viewer, selector):
        rect = await self.evaluate(viewer, "(()=>{const e=document.querySelector(" + json.dumps(selector) +
                                  ");if(!e||e.disabled)return null;const r=e.getBoundingClientRect();"
                                  "return r.width&&r.height?{x:r.x+r.width/2,y:r.y+r.height/2}:null})()")
        if rect is None:
            raise AssertionError("The real UI control is unavailable: " + selector)
        await asyncio.to_thread(viewer.click, rect["x"], rect["y"])

    async def point(self, viewer, x, y):
        rect = await self.evaluate(viewer, "(()=>{const r=document.querySelector('#frameSurface').getBoundingClientRect();"
                                  "return {x:r.x,y:r.y,w:r.width,h:r.height}})()")
        scale = min(rect["w"] / 1024, rect["h"] / 640)
        await asyncio.to_thread(viewer.click, rect["x"] + (rect["w"] - 1024 * scale) / 2 + x * scale,
                                rect["y"] + (rect["h"] - 640 * scale) / 2 + y * scale)

    async def screenshot(self, viewer, name):
        data = await asyncio.to_thread(viewer.screenshot)
        (self.output / name).write_bytes(data)
        self.report["screenshots"].append({"file": name, "sha256": hashlib.sha256(data).hexdigest()})
        self.save()

    async def run(self):
        source = Path(__file__).resolve().parents[1]
        paths = ["deskweave/browser.py", "deskweave/server.py", "deskweave/control.py", "web/app.js", "web/app.css", "web/index.html"]
        self.report["sourceHashes"] = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in paths}
        self.report["dependencies"] = {name: importlib.metadata.version(name) for name in ("aiohttp", "psutil", "websocket-client")}
        temporary = tempfile.TemporaryDirectory(prefix="DeskweaveUI-")
        root = Path(temporary.name)
        workspace = BrowserWorkspace(root / "workspace")
        viewer = BrowserWorkspace(root / "viewer", width=1280, height=900)
        broker = Broker(workspace)
        runner = web.AppRunner(make_app(broker), access_log=None)
        fixture_runner = web.AppRunner(web.Application(), access_log=None)
        async def fixture_page(request):
            return web.Response(text=FIXTURE, content_type="text/html")
        fixture_runner.app.router.add_get("/fixture", fixture_page)
        owned = []
        try:
            await runner.setup()
            site = web.TCPSite(runner, "127.0.0.1", 0)
            await site.start()
            base = "http://127.0.0.1:" + str(site._server.sockets[0].getsockname()[1])
            await fixture_runner.setup()
            fixture_site = web.TCPSite(fixture_runner, "127.0.0.1", 0)
            await fixture_site.start()
            fixture_url = "http://127.0.0.1:" + str(fixture_site._server.sockets[0].getsockname()[1]) + "/fixture"

            async with aiohttp.ClientSession() as client:
                async def owner(path, body):
                    async with client.post(base + path, json=body, headers={"Authorization": "Bearer " + broker.owner_token,
                                           "X-Deskweave-Request": "1"}) as response:
                        return response.status, await response.json()

                async def agent(name, arguments=None):
                    async with client.post(base + "/api/agent-call", json={"client": "local-fixture", "name": name,
                                           "arguments": arguments or {}}, headers={"Authorization": "Bearer " + broker.agent_token,
                                           "X-Deskweave-Request": "1"}) as response:
                        return response.status, await response.json()

                async with client.get(base + "/api/status") as response:
                    self.check("Unauthenticated status refused", response.status == 401)
                async with client.post(base + "/api/start", json={}, headers={"Authorization": "Bearer " + broker.owner_token,
                                       "X-Deskweave-Request": "1", "Origin": "http://unrelated.test"}) as response:
                    self.check("Foreign origin cannot start workspace", response.status == 403)
                self.check("Broker startup creates no workspace processes", workspace.status()["processCount"] == 0)
                await asyncio.to_thread(viewer.start)
                self.report["browserVersion"] = await asyncio.to_thread(viewer._connection().call, "Browser.getVersion")
                await asyncio.to_thread(viewer._connection().call, "Page.addScriptToEvaluateOnNewDocument", {"source":
                    "window.__probe={errors:[],frames:0,inputs:0};addEventListener('error',e=>__probe.errors.push(e.message));"
                    "addEventListener('unhandledrejection',e=>__probe.errors.push(String(e.reason)));"
                    "const originalFetch=window.fetch;window.fetch=(url,...rest)=>{"
                    "if(String(url).startsWith('/api/frame'))__probe.frames++;"
                    "if(String(url).startsWith('/api/input'))__probe.inputs++;return originalFetch(url,...rest)};"})
                await asyncio.to_thread(viewer.navigate, base + "/#token=" + broker.owner_token)
                await self.until(viewer, "!document.querySelector('#powerButton').disabled")
                self.check("Bootstrap token removed from visible URL", await self.evaluate(viewer, "location.hash === ''"))
                self.check("Viewer opening leaves workspace stopped", not workspace.running)
                self.check("Stopped UI names browser workload", await self.evaluate(viewer, "document.querySelector('#workspaceTitle').textContent === 'Browser workspace'"))
                await self.click(viewer, "#detailsButton")
                for skin, key in (("mac", "m"), ("linux", "l"), ("windows", "w")):
                    await self.click(viewer, "#appearance")
                    await asyncio.to_thread(viewer.key, key)
                    await asyncio.to_thread(viewer.key, "Enter")
                    await self.until(viewer, "document.documentElement.dataset.skin === " + json.dumps(skin))
                    await self.screenshot(viewer, f"01-stopped-{skin}.png")
                await self.click(viewer, "#closeDetailsButton")

                start = time.monotonic()
                await self.click(viewer, "#powerButton")
                await self.until(viewer, "document.querySelector('#powerButton').textContent === 'Stop workspace' && document.querySelector('#frameImage').naturalWidth === 1024", 35)
                self.report["uiStartToFirstFrameSeconds"] = round(time.monotonic() - start, 3)
                self.check("Start UI produces actual browser frame", workspace.running)
                self.check("Starting retains view-only control", broker.control.controller is None)
                self.check("Browser mode hides native terminal", await self.evaluate(viewer, "document.querySelector('#terminalButton').disabled"))
                status_code, _ = await owner("/api/input", {"kind": "text", "text": "unowned"})
                self.check("Owner HTTP input refused until takeover", status_code == 409)
                await self.click(viewer, "#takeoverButton")
                await self.until(viewer, "!document.querySelector('#browserUrl').disabled")
                self.check("Take control grants owner lease", broker.control.controller == "owner")
                await self.click(viewer, "#browserUrl")
                await asyncio.to_thread(viewer.key, "Ctrl+A" if sys.platform != "darwin" else "Meta+A")
                await asyncio.to_thread(viewer.type_text, fixture_url)
                await self.click(viewer, "#navigateButton")
                await self.until(workspace, "document.title === 'Deskweave local fixture'")
                await self.until(viewer, "!document.querySelector('#navigateButton').disabled && document.querySelector('#frameImage').naturalWidth === 1024")
                self.check("Navigation form opens actual local page", workspace.status()["url"] == fixture_url)

                await self.point(viewer, 90, 190)
                await self.until(viewer, "document.activeElement.id === 'textInput'")
                await asyncio.to_thread(viewer.type_text, "Deskweave café 漢字 🙂")
                await self.until(workspace, "editor.value === 'Deskweave café 漢字 🙂'")
                self.check("Unicode travels through visible viewer to page", True)
                await asyncio.to_thread(viewer.key, "Ctrl+A" if sys.platform != "darwin" else "Meta+A")
                await asyncio.to_thread(viewer.type_text, "A clean place to work ✓")
                await self.until(workspace, "editor.value === 'A clean place to work ✓'")
                await self.point(viewer, 90, 252)
                await self.until(workspace, "result.textContent === 'A clean place to work ✓'")
                self.check("Viewer click and key chord reach fixture readback", True)
                await asyncio.sleep(0.65)
                await self.screenshot(viewer, "02-live-windows.png")

                input_count = await self.evaluate(viewer, "__probe.inputs")
                await self.click(viewer, "#browserUrl")
                await asyncio.to_thread(viewer.key, "ArrowLeft")
                await asyncio.sleep(0.2)
                self.check("Address field navigation does not send remote keys", await self.evaluate(viewer, "__probe.inputs") == input_count)
                await self.point(viewer, 90, 190)
                await asyncio.sleep(0.2)
                input_count = await self.evaluate(viewer, "__probe.inputs")
                await self.evaluate(workspace, "window.fixtureKeys=[];addEventListener('keydown',event=>fixtureKeys.push(event.key))")
                await asyncio.to_thread(viewer.key, "Escape")
                await self.until(viewer, "document.activeElement.id === 'detailsButton'")
                await self.until(workspace, "fixtureKeys.includes('Escape')")
                self.check("Escape reaches page and returns focus to viewer controls", await self.evaluate(viewer, "__probe.inputs") == input_count + 1)

                for skin, key in (("mac", "m"), ("linux", "l"), ("windows", "w")):
                    await self.click(viewer, "#appearance")
                    await asyncio.to_thread(viewer.key, key)
                    await asyncio.to_thread(viewer.key, "Enter")
                    await self.until(viewer, "document.documentElement.dataset.skin === " + json.dumps(skin))
                    self.check(f"Appearance selector renders {skin} skin", True)
                    await self.screenshot(viewer, f"03-live-{skin}-skin.png")
                await self.click(viewer, "#compactButton")
                self.check("Compact button changes actual layout", await self.evaluate(viewer, "document.querySelector('#shell').classList.contains('is-compact')"))
                await self.point(viewer, 90, 190)
                await self.until(viewer, "document.activeElement.id === 'textInput'")
                await asyncio.to_thread(viewer.key, "Ctrl+A" if sys.platform != "darwin" else "Meta+A")
                await asyncio.to_thread(viewer.type_text, "Compact click confirmed")
                await self.until(workspace, "editor.value === 'Compact click confirmed'")
                self.check("Compact letterbox maps input to exact page control", True)
                await self.screenshot(viewer, "04-compact.png")
                await self.click(viewer, "#collapseButton")
                await asyncio.sleep(0.7)
                count = await self.evaluate(viewer, "__probe.frames")
                await asyncio.sleep(1.2)
                self.check("Collapsed viewer stops frame requests", await self.evaluate(viewer, "__probe.frames") == count)
                self.check("Collapsed viewer retains runtime", workspace.running)
                await self.screenshot(viewer, "05-collapsed.png")
                await self.click(viewer, "#collapseButton")
                await self.until(viewer, "document.querySelector('#frameImage').naturalWidth === 1024")
                self.check("Expand reconnects to same runtime", workspace.running)
                await self.click(viewer, "#compactButton")

                original_target = (await asyncio.to_thread(viewer._connection().call, "Target.getTargetInfo"))["targetInfo"]["targetId"]
                other_target = (await asyncio.to_thread(viewer._connection().call, "Target.createTarget",
                                                       {"url": "about:blank", "background": False}))["targetId"]
                try:
                    await self.until(viewer, "document.hidden", 5)
                    await asyncio.sleep(0.7)
                    count = await self.evaluate(viewer, "__probe.frames")
                    await asyncio.sleep(1.2)
                    self.check("Actually hidden browser tab stops frame requests", await self.evaluate(viewer, "__probe.frames") == count)
                finally:
                    await asyncio.to_thread(viewer._connection().call, "Target.activateTarget", {"targetId": original_target})
                    await asyncio.to_thread(viewer._connection().call, "Target.closeTarget", {"targetId": other_target})
                await self.until(viewer, "!document.hidden && document.querySelector('#frameImage').naturalWidth === 1024")
                self.check("Tab reactivation resumes existing runtime", workspace.running)

                await asyncio.to_thread(viewer._connection().call, "Emulation.setDeviceMetricsOverride",
                                        {"width": 900, "height": 650, "deviceScaleFactor": 1, "mobile": False})
                viewer.width, viewer.height = 900, 650
                await self.click(viewer, "#takeoverButton")
                await self.until(viewer, "!document.querySelector('#textInput').disabled")
                await self.point(viewer, 90, 190)
                await asyncio.to_thread(viewer.key, "Ctrl+A" if sys.platform != "darwin" else "Meta+A")
                await asyncio.to_thread(viewer.type_text, "Narrow viewport confirmed")
                await self.until(workspace, "editor.value === 'Narrow viewport confirmed'")
                self.check("Narrow 900px viewer maps input to page", True)
                await self.screenshot(viewer, "05-narrow-900.png")
                await asyncio.to_thread(viewer._connection().call, "Emulation.setDeviceMetricsOverride",
                                        {"width": 1280, "height": 900, "deviceScaleFactor": 1, "mobile": False})
                viewer.width, viewer.height = 1280, 900

                await self.click(viewer, "#detailsButton")
                await self.until(viewer, "!document.querySelector('#detailsDrawer').hidden")
                await self.click(viewer, "#agentToggle")
                await self.until(viewer, "document.querySelector('#agentToggle').checked && !document.querySelector('#agentToggle').disabled")
                await self.click(viewer, "#drawerRelease")
                await self.until(viewer, "document.querySelector('#controllerDetail').textContent === 'No controller'")
                status_code, result = await agent("workspace_acquire")
                self.check("Explicit agent switch and release enable lease acquisition", status_code == 200, result if status_code != 200 else None)
                lease = result["result"]["lease"]
                status_code, result = await agent("workspace_run", {"lease": lease, "argv": [sys.executable, "-c", "raise SystemExit('must not execute')"]})
                self.check("Browser backend refuses native commands even with lease", status_code == 409)
                status_code, result = await agent("workspace_input", {"lease": lease, "kind": "click", "x": 90, "y": 190})
                self.check("Leased agent browser input is accepted", status_code == 200)
                status_code, result = await agent("workspace_input", {"lease": lease, "kind": "text", "text": " agent"})
                await self.until(workspace, "editor.value.includes(' agent')")
                self.check("Leased agent input reaches actual page", status_code == 200)
                status_code, result = await agent("workspace_navigate", {"lease": lease, "url": fixture_url + "?agent=1"})
                await self.until(viewer, "document.querySelector('#browserUrl').value.endsWith('?agent=1')")
                self.check("Agent navigation updates actual viewer address", status_code == 200)
                await self.click(viewer, "#drawerTakeover")
                await self.until(viewer, "document.querySelector('#controllerDetail').textContent === 'Owner'")
                status_code, _ = await agent("workspace_input", {"lease": lease, "kind": "text", "text": "revoked"})
                self.check("Owner takeover revokes previous agent lease", status_code == 409)
                (workspace.files / "saved.txt").write_text("retained", encoding="utf-8")
                self.report["workloadStatsBeforeStop"] = {key: workspace.status()[key] for key in ("processCount", "rssBytes", "cpuSeconds", "metricsComplete")}
                await self.screenshot(viewer, "06-details.png")
                await self.click(viewer, "#closeDetailsButton")

                before_stop = [(p["pid"], p["created"]) for p in workspace.status()["processes"]]
                await self.click(viewer, "#powerButton")
                await self.until(viewer, "document.querySelector('#stopDialog').open")
                self.check("Stop requires visible save-work confirmation", workspace.running)
                await self.click(viewer, "#stopDialog button[value='stop']")
                await self.until(viewer, "document.querySelector('#powerButton').textContent === 'Start workspace' && !document.querySelector('#powerButton').disabled", 30)
                self.check("Stop UI ends owned browser processes", not workspace.running and not live_identities(before_stop))
                self.check("Stop retains existing workspace file", (workspace.files / "saved.txt").read_text() == "retained")
                await self.click(viewer, "#powerButton")
                await self.until(viewer, "document.querySelector('#frameImage').naturalWidth === 1024", 35)
                await self.click(viewer, "#takeoverButton")
                await self.until(viewer, "!document.querySelector('#navigateButton').disabled")
                await self.click(viewer, "#browserUrl")
                await asyncio.to_thread(viewer.key, "Ctrl+A" if sys.platform != "darwin" else "Meta+A")
                await asyncio.to_thread(viewer.type_text, fixture_url)
                await self.click(viewer, "#navigateButton")
                await self.until(workspace, "document.title === 'Deskweave local fixture'")
                self.check("Restart retains browser storage", await self.evaluate(workspace, "localStorage.getItem('note')") == "A clean place to work ✓")
                self.check("No JavaScript errors in actual viewer", not await self.evaluate(viewer, "__probe.errors"), await self.evaluate(viewer, "__probe.errors"))
                current_hashes = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in paths}
                self.check("Tested browser and UI sources stayed unchanged", current_hashes == self.report["sourceHashes"])
        except Exception:
            if viewer.running:
                try:
                    await self.screenshot(viewer, "failure.png")
                except Exception:
                    pass
            raise
        finally:
            for runtime in (workspace, viewer):
                owned.extend((p["pid"], p["created"]) for p in runtime.status()["processes"])
            errors = []
            for action in (runner.cleanup, fixture_runner.cleanup, lambda: asyncio.to_thread(viewer.stop)):
                try:
                    await action()
                except Exception as exc:
                    errors.append(str(exc))
            self.report["cleanup"] = {"errors": errors, "survivingOwnedProcesses": live_identities(owned)}
            self.save()
            if not errors and not self.report["cleanup"]["survivingOwnedProcesses"]:
                temporary.cleanup()
            else:
                self.report["retainedFixtureRoot"] = str(root)
                self.save()
            if errors or self.report["cleanup"]["survivingOwnedProcesses"]:
                raise AssertionError("Owned process cleanup was incomplete")


def live_identities(identities):
    alive = []
    for pid, created in identities:
        try:
            process = psutil.Process(pid)
            if process.create_time() == created and process.is_running() and process.status() != psutil.STATUS_ZOMBIE:
                alive.append(pid)
        except psutil.NoSuchProcess:
            pass
    return sorted(set(alive))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    gate = Gate(args.output)
    try:
        asyncio.run(asyncio.wait_for(gate.run(), timeout=210))
        gate.report["ok"] = True
    except BaseException:
        gate.report["error"] = traceback.format_exc()
    gate.save()
    print(json.dumps({"ok": gate.report["ok"], "claims": len(gate.report["claims"]), "report": str(gate.output / "report.json")}))
    return 0 if gate.report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
