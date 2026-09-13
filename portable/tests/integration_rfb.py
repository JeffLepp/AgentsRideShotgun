"""Opt-in real noVNC UI gate against an explicitly supplied, stopped Linux broker.

Run from portable/: python tests/integration_rfb.py --connection PATH --output PATH
The gate owns its private Chromium viewer and the workspace it explicitly starts.
It stops that workspace; it does not stop or remove the externally supplied broker.
No personal browser, host display, model, or global keyboard input is used.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import io
import json
from pathlib import Path
import sys
import tempfile
import time
import traceback
import uuid

import aiohttp
from PIL import Image, ImageChops

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from deskweave.browser import BrowserWorkspace
from deskweave.cli import load_connection
from integration_browser import Gate, live_identities


OBSERVER = """
window.__rfbGate={errors:[],opens:0,closes:0,messages:0,bytes:0,sentKeys:0,active:0};
addEventListener('error',e=>__rfbGate.errors.push(e.message));
addEventListener('unhandledrejection',e=>__rfbGate.errors.push(String(e.reason)));
const OriginalWebSocket=window.WebSocket;
window.WebSocket=new Proxy(OriginalWebSocket,{construct(target,args){
 const socket=Reflect.construct(target,args,target);
 if(new URL(args[0],location.href).pathname==='/rfb'){let opened=false;
  socket.addEventListener('open',()=>{opened=true;__rfbGate.opens++;__rfbGate.active++});
  socket.addEventListener('close',()=>{__rfbGate.closes++;if(opened)__rfbGate.active--});
  socket.addEventListener('message',e=>{__rfbGate.messages++;__rfbGate.bytes+=e.data.byteLength||e.data.size||0});
  const send=socket.send.bind(socket);
  socket.send=data=>{const v=data instanceof ArrayBuffer?new Uint8Array(data):
    ArrayBuffer.isView(data)?new Uint8Array(data.buffer,data.byteOffset,data.byteLength):null;
    if(v&&(v[0]===4||v[0]===255))__rfbGate.sentKeys++;return send(data);};
 }
 return socket;
}});
"""

CANVAS_READY = """(()=>{const c=document.querySelector('#screen canvas');return c&&c.width>0&&c.height>0&&
 document.querySelector('#viewerPlaceholder').hidden&&__rfbGate.active===1&&__rfbGate.bytes>0})()"""


class RfbGate(Gate):
    def __init__(self, output, connection, runtime_reference=None):
        self.connection = connection
        super().__init__(output)
        self.report["workload"] = "Linux desktop through real noVNC and a separately owned Chromium viewer"
        self.report["runtimeReference"] = runtime_reference
        self.report["limits"] = [
            "The external broker/container is supplied by the operator; this gate does not create or destroy it.",
            "Local source hashes are reference hashes. Served web hashes are fetched from the actual broker.",
            "This is functional evidence through the reported host; it is not a 4 GB laptop or native Mac hardware benchmark.",
            "Linux process cleanup is checked through broker status; the separate viewer is checked by PID and creation time."]
        self.base = connection["url"]
        self.client = None
        self.started_workspace = False
        self.agent_client = "rfb-gate-" + uuid.uuid4().hex
        suffix = uuid.uuid4().hex
        self.markers = {kind: "rfbgate" + kind + suffix for kind in ("dom", "raw", "denied", "compact")}

    def save(self):
        self.report["elapsedSeconds"] = round(time.monotonic() - self.started, 3)
        serialized = json.dumps(self.report, indent=2, ensure_ascii=False)
        for key in ("ownerToken", "agentToken"):
            secret = self.connection.get(key)
            if secret:
                serialized = serialized.replace(secret, "[redacted]")
        (self.output / "report.json").write_text(serialized, encoding="utf-8")

    async def owner(self, path, body=None, expected=200):
        headers = {"Authorization": "Bearer " + self.connection["ownerToken"]}
        if body is not None:
            headers["X-Deskweave-Request"] = "1"
        async with self.client.request("GET" if body is None else "POST", self.base + path,
                                       json=body, headers=headers) as response:
            data = await response.json()
            if response.status != expected:
                raise AssertionError(f"Owner {path} returned {response.status}: {data.get('error', 'request failed')}")
            return data

    async def agent(self, name, arguments=None, expected=200):
        async with self.client.post(self.base + "/api/agent-call",
                                    json={"client": self.agent_client, "name": name, "arguments": arguments or {}},
                                    headers={"Authorization": "Bearer " + self.connection["agentToken"],
                                             "X-Deskweave-Request": "1"}) as response:
            data = await response.json()
            if response.status != expected:
                raise AssertionError(f"Agent {name} returned {response.status}: {data.get('error', 'request failed')}")
            return data.get("result", data)

    async def oracle(self, lease, remove=False):
        names = list(self.markers.values())
        program = ("import json,os;from pathlib import Path;names=" + repr(names) + ";"
                   "print(json.dumps({'files':{n:Path(n).is_file() for n in names},"
                   "'display':os.environ.get('DISPLAY'),'cwd':str(Path.cwd())}));")
        if remove:
            program += "[Path(n).unlink(missing_ok=True) for n in names]"
        result = await self.agent("workspace_run", {"lease": lease, "argv": ["python3", "-c", program], "timeout": 10})
        if result.get("exitCode") != 0 or result.get("timedOut") or result.get("truncated"):
            raise AssertionError("Independent workspace file readback failed: " + str(result.get("stderr", ""))[-800:])
        return json.loads(result["stdout"])

    async def take_control(self, viewer):
        await self.until(viewer, "!document.querySelector('#takeoverButton').disabled")
        await self.click(viewer, "#takeoverButton")
        await self.until(viewer, CANVAS_READY + " && !document.querySelector('#terminalButton').disabled", 30)
        state = await self.owner("/api/status")
        self.check("Take control grants server-confirmed VNC input", state["controller"] == "owner" and state.get("viewerInput") is True)

    async def release_control(self, viewer):
        await self.until(viewer, "!document.querySelector('#releaseButton').hidden && !document.querySelector('#releaseButton').disabled")
        await self.click(viewer, "#releaseButton")
        await self.until(viewer, "document.querySelector('#releaseButton').hidden && " + CANVAS_READY, 30)
        state = await self.owner("/api/status")
        self.check("Release revokes server-side VNC input", state["controller"] is None and state.get("viewerInput") is False)

    async def terminal_point(self, viewer):
        rect = await self.evaluate(viewer, "(()=>{const r=document.querySelector('#screen canvas').getBoundingClientRect();"
                                  "return {x:r.x,y:r.y,w:r.width,h:r.height}})()")
        state = await self.owner("/api/status")
        # Native launch requests xterm at +24+24; this point is inside its client area.
        await asyncio.to_thread(viewer.click, rect["x"] + 110 * rect["w"] / state["width"],
                                rect["y"] + 100 * rect["h"] / state["height"])
        await self.until(viewer, "document.activeElement === document.querySelector('#screen canvas')")

    async def type_terminal(self, viewer, marker):
        await self.terminal_point(viewer)
        # Real CDP KeyboardEvents pass through the noVNC canvas handlers, not its API.
        for character in "touch " + marker:
            await asyncio.to_thread(viewer.key, "Space" if character == " " else character)
        await asyncio.to_thread(viewer.key, "Enter")
        await asyncio.sleep(.4)

    async def create_raw_client(self, viewer):
        await self.evaluate(viewer, """(async()=>{
          const response=await fetch('/api/connect',{method:'POST',credentials:'same-origin',
            headers:{'Content-Type':'application/json','X-Deskweave-Request':'1'},body:'{}'});
          if(!response.ok)throw Error('Probe RFB connection refused');
          const connection=await response.json();
          const RFB=(await import('/vendor/novnc/core/rfb.js')).default;
          const target=document.createElement('div');target.id='rfbGateRaw';
          Object.assign(target.style,{position:'fixed',width:'1024px',height:'640px',top:'0',left:'0',opacity:'0.001',pointerEvents:'none'});
          document.body.appendChild(target);
          const url=new URL('/rfb',location.href);url.protocol=location.protocol==='https:'?'wss:':'ws:';
          const client=new RFB(target,url.href,{credentials:{password:connection.password},shared:true});
          window.__rawRfb=client;window.__rawReady=false;
          client.viewOnly=false;client.focusOnClick=false;client.resizeSession=false;client.scaleViewport=true;
          client.addEventListener('connect',()=>{window.__rawReady=true});
          client.addEventListener('disconnect',()=>{window.__rawReady=false});
        })()""")
        await self.until(viewer, "window.__rawReady && __rfbGate.active===2", 25)

    async def raw_type(self, viewer, marker):
        before = await self.evaluate(viewer, "__rfbGate.sentKeys")
        count = await self.evaluate(viewer, "(()=>{const text=" + json.dumps("touch " + marker) + ";"
                                    "__rawRfb.viewOnly=false;for(const character of text)__rawRfb.sendKey(character.codePointAt(0));"
                                    "__rawRfb.sendKey(0xff0d);return text.length+1})()")
        await asyncio.sleep(.5)
        after = await self.evaluate(viewer, "__rfbGate.sentKeys")
        self.check("Deliberate raw client actually emits RFB key packets", after - before >= count,
                   {"keyPacketWrites": after - before, "charactersAttempted": count})

    async def destroy_raw_client(self, viewer):
        await self.evaluate(viewer, "if(window.__rawRfb){__rawRfb.disconnect();window.__rawRfb=null;}document.querySelector('#rfbGateRaw')?.remove()")
        await self.until(viewer, "__rfbGate.active===1", 10)

    async def compare_canvas(self, viewer):
        await asyncio.sleep(.5)
        encoded = await self.evaluate(viewer, "document.querySelector('#screen canvas').toDataURL('image/png').split(',')[1]")
        async with self.client.get(self.base + "/api/frame", headers={"Authorization": "Bearer " + self.connection["ownerToken"]}) as response:
            self.check("Independent Linux display capture succeeds", response.status == 200 and response.content_type == "image/png")
            independent = await response.read()
        canvas = Image.open(io.BytesIO(base64.b64decode(encoded))).convert("RGB")
        reference = Image.open(io.BytesIO(independent)).convert("RGB")
        self.check("noVNC canvas has the exact native desktop dimensions", canvas.size == reference.size)
        difference = ImageChops.difference(canvas, reference)
        different_pixels = sum(1 for pixel in difference.getdata() if max(pixel) > 5)
        fraction = different_pixels / (canvas.width * canvas.height)
        self.check("Live noVNC pixels match independent display capture", fraction < .02,
                   {"differentPixelFraction": round(fraction, 6), "allowedFraction": .02})
        self.check("Captured desktop contains real terminal detail", len(canvas.getcolors(canvas.width * canvas.height) or []) > 8)
        (self.output / "native-display.png").write_bytes(independent)
        self.report["screenshots"].append({"file": "native-display.png", "sha256": hashlib.sha256(independent).hexdigest()})
        self.save()

    async def run(self):
        source = Path(__file__).resolve().parents[1]
        paths = ["deskweave/native.py", "deskweave/browser.py", "deskweave/server.py", "deskweave/control.py",
                 "web/app.js", "web/app.css", "web/index.html", "web/vendor/novnc/core/rfb.js",
                 "tests/integration_rfb.py", "tests/integration_browser.py"]
        self.report["localSourceHashes"] = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in paths}
        self.report["dependencies"] = {name: importlib.metadata.version(name) for name in ("aiohttp", "psutil", "websocket-client", "Pillow")}
        temporary = tempfile.TemporaryDirectory(prefix="DeskweaveRfbViewer-")
        root = Path(temporary.name)
        viewer = BrowserWorkspace(root / "viewer", width=1280, height=900)
        viewer_identities = []
        remote_cleanup = None
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=90)) as client:
            self.client = client
            try:
                async with client.get(self.base + "/api/status") as response:
                    self.check("Unauthenticated Linux broker status refused", response.status == 401)
                initial = await self.owner("/api/status")
                self.check("Supplied broker is an idle Linux desktop", initial.get("transport") == "rfb" and not initial["running"])
                self.check("Initial agent access and owner control are off", not initial["agentEnabled"] and initial["controller"] is None)
                self.check("Linux backend supports independent command readback", initial.get("commandsSupported") is True)
                self.report["initialBroker"] = {key: initial.get(key) for key in ("version", "hostPlatform", "backend", "transport", "isolation")}
                served = {}
                for name in ("index.html", "app.css", "app.js", "vendor/novnc/core/rfb.js"):
                    async with client.get(self.base + "/" + name) as response:
                        self.check("Broker serves " + name, response.status == 200)
                        served[name] = hashlib.sha256(await response.read()).hexdigest()
                    self.check("Served " + name + " matches local source", served[name] == self.report["localSourceHashes"]["web/" + name])
                self.report["servedWebHashes"] = served

                await asyncio.to_thread(viewer.start)
                self.report["viewerBrowserVersion"] = await asyncio.to_thread(viewer._connection().call, "Browser.getVersion")
                await asyncio.to_thread(viewer._connection().call, "Page.addScriptToEvaluateOnNewDocument", {"source": OBSERVER})
                await asyncio.to_thread(viewer.navigate, self.base + "/#token=" + self.connection["ownerToken"])
                await self.until(viewer, "!document.querySelector('#powerButton').disabled")
                self.check("Authenticated bootstrap removes launcher fragment", await self.evaluate(viewer, "location.hash===''"))
                self.check("Opening viewer starts no desktop", not (await self.owner("/api/status"))["running"])
                await self.screenshot(viewer, "01-stopped.png")
                start = time.monotonic()
                self.started_workspace = True
                await self.click(viewer, "#powerButton")
                await self.until(viewer, CANVAS_READY, 50)
                started_state = await self.owner("/api/status")
                self.report["uiStartToFirstCanvasSeconds"] = round(time.monotonic() - start, 3)
                self.check("Start UI produces a real RFB framebuffer", started_state["running"] and started_state["controller"] is None)
                self.check("Start leaves server VNC input disabled", started_state.get("viewerInput") is False)
                await self.click(viewer, "#detailsButton")
                self.check("Resource drawer distinguishes summed RSS", await self.evaluate(viewer, "document.querySelector('#rssBytes').previousElementSibling.textContent==='Process RSS (sum)'"))
                container_memory = started_state.get("containerMemoryBytes")
                if isinstance(container_memory, (int, float)) and container_memory > 0:
                    self.check("Actual container memory appears separately in the drawer", await self.evaluate(viewer,
                        "!document.querySelector('#containerMemoryRow').hidden&&document.querySelector('#containerMemory').textContent!=='Unavailable'"))
                    limit = (started_state.get("limits") or {}).get("memoryMiB")
                    self.check("Container limit display follows the actual broker", await self.evaluate(viewer,
                        "document.querySelector('#memoryLimit').textContent!=='Unavailable'") == (isinstance(limit, (int, float)) and limit > 0))
                else:
                    self.check("Non-container broker has no invented container memory", await self.evaluate(viewer, "document.querySelector('#containerMemoryRow').hidden"))
                await self.click(viewer, "#agentToggle")
                await self.until(viewer, "document.querySelector('#agentToggle').checked&&!document.querySelector('#agentToggle').disabled")
                await self.click(viewer, "#closeDetailsButton")
                lease = (await self.agent("workspace_acquire"))["lease"]
                baseline = await self.oracle(lease)
                self.check("Unique marker files are absent before UI input", not any(baseline["files"].values()))
                self.report["ownedDisplay"] = baseline["display"]

                await self.take_control(viewer)
                await self.click(viewer, "#terminalButton")
                await self.until(viewer, "!document.querySelector('#terminalButton').disabled", 25)
                await self.type_terminal(viewer, self.markers["dom"])
                state = await self.owner("/api/status")
                self.report["liveResources"] = {key: state.get(key) for key in ("processCount", "rssBytes", "containerMemoryBytes", "limits", "cpuSeconds")}
                await self.compare_canvas(viewer)
                await self.screenshot(viewer, "02-live-terminal.png")
                await self.create_raw_client(viewer)
                await self.raw_type(viewer, self.markers["raw"])
                await self.destroy_raw_client(viewer)
                await self.release_control(viewer)
                lease = (await self.agent("workspace_acquire"))["lease"]
                positive = await self.oracle(lease)
                self.check("Real canvas keyboard input creates independent file marker", positive["files"][self.markers["dom"]])
                self.check("Raw RFB client positive control creates file marker", positive["files"][self.markers["raw"]])
                await self.agent("workspace_release", {"lease": lease})

                await self.create_raw_client(viewer)
                await self.raw_type(viewer, self.markers["denied"])
                await self.destroy_raw_client(viewer)
                lease = (await self.agent("workspace_acquire"))["lease"]
                refused = await self.oracle(lease)
                self.check("Server rejects raw RFB keys after release despite client viewOnly=false", not refused["files"][self.markers["denied"]])
                await self.take_control(viewer)

                for skin, key in (("mac", "m"), ("linux", "l"), ("windows", "w")):
                    await self.click(viewer, "#appearance")
                    await asyncio.to_thread(viewer.key, key)
                    await asyncio.to_thread(viewer.key, "Enter")
                    await self.until(viewer, "document.documentElement.dataset.skin===" + json.dumps(skin))
                    await self.screenshot(viewer, "03-live-" + skin + ".png")
                before_compact = await self.evaluate(viewer, "__rfbGate.opens")
                await self.click(viewer, "#compactButton")
                await self.type_terminal(viewer, self.markers["compact"])
                self.check("Compact layout preserves the same RFB connection", await self.evaluate(viewer, "__rfbGate.opens") == before_compact)
                await self.screenshot(viewer, "04-compact.png")
                await self.click(viewer, "#collapseButton")
                await self.until(viewer, "__rfbGate.active===0", 10)
                messages = await self.evaluate(viewer, "__rfbGate.messages")
                await asyncio.sleep(1)
                self.check("Collapsed viewer stops RFB traffic", await self.evaluate(viewer, "__rfbGate.messages") == messages)
                state = await self.owner("/api/status")
                self.check("Collapse retains existing desktop lifetime", state["running"] and state["startedAt"] == started_state["startedAt"])
                await self.screenshot(viewer, "05-collapsed.png")
                await self.click(viewer, "#collapseButton")
                await self.until(viewer, CANVAS_READY, 30)
                self.check("Expand resumes viewing without granting local input", await self.evaluate(viewer, "document.querySelector('#controllerLabel').textContent==='View only'"))
                await self.click(viewer, "#compactButton")

                original = (await asyncio.to_thread(viewer._connection().call, "Target.getTargetInfo"))["targetInfo"]["targetId"]
                other = (await asyncio.to_thread(viewer._connection().call, "Target.createTarget", {"url": "about:blank", "background": False}))["targetId"]
                try:
                    await self.until(viewer, "document.hidden&&__rfbGate.active===0", 10)
                    messages = await self.evaluate(viewer, "__rfbGate.messages")
                    await asyncio.sleep(1)
                    self.check("Actually hidden browser tab stops RFB traffic", await self.evaluate(viewer, "__rfbGate.messages") == messages)
                finally:
                    await asyncio.to_thread(viewer._connection().call, "Target.activateTarget", {"targetId": original})
                    await asyncio.to_thread(viewer._connection().call, "Target.closeTarget", {"targetId": other})
                await self.until(viewer, "!document.hidden&&" + CANVAS_READY, 30)
                state = await self.owner("/api/status")
                self.check("Tab restoration retains the same desktop lifetime", state["running"] and state["startedAt"] == started_state["startedAt"])
                await self.release_control(viewer)
                lease = (await self.agent("workspace_acquire"))["lease"]
                final = await self.oracle(lease, remove=True)
                self.check("Full and compact input share the same retained display", final["display"] == baseline["display"] and final["files"][self.markers["compact"]])
                self.check("Denied marker remains absent after reconnects", not final["files"][self.markers["denied"]])
                self.check("Gate removes only its unique fixture files", not any((await self.oracle(lease))["files"].values()))
                await self.screenshot(viewer, "06-restored.png")
                await self.until(viewer, "document.querySelector('#controllerLabel').textContent==='Agent control'")
                await self.click(viewer, "#powerButton")
                await self.until(viewer, "document.querySelector('#stopDialog').open")
                self.check("Stop UI identifies active agent control", await self.evaluate(viewer, "document.querySelector('#stopDetail').textContent.includes('agent')"))
                await self.click(viewer, "#stopDialog button[value='stop']")
                await self.until(viewer, "document.querySelector('#powerButton').textContent==='Start workspace'&&!document.querySelector('#powerButton').disabled&&__rfbGate.active===0", 40)
                stopped = await self.owner("/api/status")
                self.check("Stop reports no remaining owned Linux processes", not stopped["running"] and stopped["processCount"] == 0)
                self.check("Stop disables agent access before another action can restart it", stopped["agentEnabled"] is False)
                await self.owner("/api/agent", {"enabled": False})
                await self.screenshot(viewer, "07-stopped.png")
                errors = await self.evaluate(viewer, "__rfbGate.errors")
                self.check("Actual noVNC viewer has no JavaScript errors", not errors, errors)
                current = {name: hashlib.sha256((source / name).read_bytes()).hexdigest() for name in paths}
                self.check("Local referenced sources stayed unchanged during gate", current == self.report["localSourceHashes"])
                self.report["finalBroker"] = {key: stopped.get(key) for key in ("backend", "running", "processCount", "rssBytes", "cpuSeconds")}
            except Exception:
                if viewer.running:
                    try:
                        await self.screenshot(viewer, "failure.png")
                    except Exception:
                        pass
                raise
            finally:
                errors = []
                if self.started_workspace:
                    try:
                        await self.owner("/api/stop", {})
                        await self.owner("/api/agent", {"enabled": False})
                        remote_cleanup = await self.owner("/api/status")
                        if remote_cleanup["running"] or remote_cleanup["processCount"]:
                            errors.append("External broker reports remaining workspace processes")
                    except Exception as error:
                        errors.append(str(error))
                try:
                    viewer_identities = [(p["pid"], p["created"]) for p in viewer.status()["processes"]]
                    await asyncio.to_thread(viewer.stop)
                except Exception as error:
                    errors.append(str(error))
                surviving = live_identities(viewer_identities)
                self.report["cleanup"] = {"errors": errors, "survivingOwnedViewerProcesses": surviving,
                                          "externalWorkspaceStopped": None if remote_cleanup is None else not remote_cleanup["running"],
                                          "externalBrokerStoppedByGate": False}
                self.save()
                if not errors and not surviving:
                    temporary.cleanup()
                else:
                    self.report["retainedViewerRoot"] = str(root)
                    self.save()
                    raise AssertionError("Gate-owned cleanup was incomplete")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--connection", type=Path, required=True, help="Explicit private connection JSON for an idle Linux broker")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime-reference", default=None, help="Optional immutable image/container reference for the supplied broker")
    args = parser.parse_args()
    gate = RfbGate(args.output, load_connection(args.connection), args.runtime_reference)
    try:
        asyncio.run(asyncio.wait_for(gate.run(), timeout=300))
        gate.report["ok"] = True
    except BaseException:
        gate.report["error"] = traceback.format_exc()
    gate.save()
    print(json.dumps({"ok": gate.report["ok"], "claims": len(gate.report["claims"]), "report": str(gate.output / "report.json")}))
    return 0 if gate.report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
