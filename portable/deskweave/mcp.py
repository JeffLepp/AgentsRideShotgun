"""Small MCP stdio bridge; credentials stay in the private connection file."""
from __future__ import annotations

import json
import secrets
import sys
from urllib.error import HTTPError
from urllib.request import Request, build_opener, ProxyHandler

from . import __version__


def schema(properties=None, required=()):
    return {"type": "object", "properties": properties or {}, "required": list(required), "additionalProperties": False}


LEASE = {"type": "string", "description": "Ticket from workspace_acquire; never reuse after revocation."}
TOOLS = [
    {"name": "workspace_status", "description": "Read actual workspace state and resource counters. Requires owner-enabled agent access.", "inputSchema": schema()},
    {"name": "workspace_acquire", "description": "Acquire an exclusive 45-second renewable lease. Owner control takes precedence.", "inputSchema": schema()},
    {"name": "workspace_release", "description": "Release this agent's lease.", "inputSchema": schema({"lease": LEASE}, ("lease",))},
    {"name": "workspace_start", "description": "Start one owned workspace. Desktop mode starts no browser; browser mode launches a private browser. Same-user processes are not hostile-code confinement.", "inputSchema": schema({"lease": LEASE}, ("lease",))},
    {"name": "workspace_screenshot", "description": "Capture only the owned workspace. Inspect after input to verify results.", "inputSchema": schema()},
    {"name": "workspace_launch", "description": "Open browser or terminal inside the workspace. Graphical terminal requires Linux desktop mode.", "inputSchema": schema({"lease": LEASE, "kind": {"enum": ["browser", "terminal"]}}, ("lease", "kind"))},
    {"name": "workspace_navigate", "description": "Navigate the private browser to an HTTP(S) URL; browser mode only.", "inputSchema": schema({"lease": LEASE, "url": {"type": "string"}}, ("lease", "url"))},
    {"name": "workspace_input", "description": "Send click, bounded scroll steps, a key chord, or text to the owned display. Desktop text currently accepts printable ASCII only. Never blindly retry an input error; inspect first.", "inputSchema": schema({"lease": LEASE, "kind": {"enum": ["click", "scroll", "key", "text"]}, "x": {"type": "integer"}, "y": {"type": "integer"}, "button": {"enum": [1, 2, 3]}, "dx": {"type": "integer", "minimum": -20, "maximum": 20}, "dy": {"type": "integer", "minimum": -20, "maximum": 20}, "key": {"type": "string"}, "text": {"type": "string", "maxLength": 4096}}, ("lease", "kind"))},
    {"name": "workspace_run", "description": "Run an explicit argv under normal OS user permissions, in workspace files, with bounded output and a 60-second maximum. This is NOT a filesystem sandbox. An accepted command may finish after control is revoked; revocation blocks queued/new operations.", "inputSchema": schema({"lease": LEASE, "argv": {"type": "array", "items": {"type": "string"}, "minItems": 1, "maxItems": 64}, "timeout": {"type": "number", "minimum": 0.1, "maximum": 60}}, ("lease", "argv"))},
]


def http_json(url, token, data=None):
    body = None if data is None else json.dumps(data).encode("utf-8")
    request = Request(url, data=body, headers={"Authorization": "Bearer " + token,
                      "Content-Type": "application/json", "X-Deskweave-Request": "1"})
    # A local bridge must never forward tokens through an environment HTTP proxy.
    opener = build_opener(ProxyHandler({}))
    try:
        with opener.open(request, timeout=90) as response:
            return json.load(response)
    except HTTPError as exc:
        raw = exc.read(65536).decode("utf-8", errors="replace")
        try:
            message = json.loads(raw).get("error", raw)
        except ValueError:
            message = raw
        raise RuntimeError(f"Broker refused ({exc.code}): {message}") from None


def handle(message, connection, client):
    if not isinstance(message, dict) or message.get("jsonrpc") != "2.0":
        return {"jsonrpc": "2.0", "id": None, "error": {"code": -32600, "message": "Invalid request"}}
    if "id" not in message:
        return None
    result = {"jsonrpc": "2.0", "id": message["id"]}
    method, params = message.get("method"), message.get("params", {})
    if not isinstance(params, dict):
        return {**result, "error": {"code": -32602, "message": "params must be an object"}}
    supported = TOOLS
    if connection.get("backend") == "browser":
        supported = [item for item in TOOLS if item["name"] != "workspace_run"]
    elif connection.get("backend") == "desktop":
        supported = [item for item in TOOLS if item["name"] != "workspace_navigate"]
    if method == "initialize":
        requested = params.get("protocolVersion")
        version = requested if requested in ("2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25") else "2025-11-25"
        result["result"] = {"protocolVersion": version, "capabilities": {"tools": {}},
                            "serverInfo": {"name": "deskweave-portable", "version": __version__},
                            "instructions": "Ask the owner to enable agent access in the viewer. Acquire a lease before changing the workspace. Owner takeover revokes your lease; do not reacquire repeatedly against owner intent."}
    elif method == "ping":
        result["result"] = {}
    elif method == "tools/list":
        result["result"] = {"tools": supported}
    elif method == "tools/call":
        if params.get("name") not in {item["name"] for item in supported} or not isinstance(params.get("arguments", {}), dict):
            return {**result, "error": {"code": -32602, "message": "Unknown tool or invalid arguments"}}
        try:
            value = http_json(connection["url"] + "/api/agent-call", connection["agentToken"],
                              {"client": client, "name": params["name"], "arguments": params.get("arguments", {})})["result"]
            if isinstance(value, dict) and "png" in value:
                content = [{"type": "image", "data": value["png"], "mimeType": "image/png"}]
            else:
                content = [{"type": "text", "text": json.dumps(value, ensure_ascii=False)}]
            result["result"] = {"content": content}
        except (OSError, ValueError, RuntimeError) as exc:
            result["result"] = {"isError": True, "content": [{"type": "text", "text": str(exc)}]}
    else:
        result["error"] = {"code": -32601, "message": "Method not found"}
    return result


def stdio(connection):
    client = secrets.token_urlsafe(24)
    while True:
        line = sys.stdin.buffer.readline(1024 * 1024 + 1)
        if not line:
            break
        if len(line) > 1024 * 1024:
            print("MCP input exceeded 1 MiB", file=sys.stderr)
            return
        try:
            reply = handle(json.loads(line), connection, client)
        except (ValueError, UnicodeError):
            reply = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}}
        if reply is not None:
            sys.stdout.write(json.dumps(reply, ensure_ascii=True) + "\n")
            sys.stdout.flush()
