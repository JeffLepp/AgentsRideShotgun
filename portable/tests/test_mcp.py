import json
import unittest
from unittest.mock import patch

from deskweave.mcp import handle, TOOLS


class McpTests(unittest.TestCase):
    connection = {"url": "http://127.0.0.1:1234", "agentToken": "secret"}

    def message(self, method, params=None):
        return {"jsonrpc": "2.0", "id": 1, "method": method, "params": params or {}}

    def test_negotiates_supported_legacy_versions(self):
        result = handle(self.message("initialize", {"protocolVersion": "2025-03-26"}), self.connection, "client")
        self.assertEqual(result["result"]["protocolVersion"], "2025-03-26")
        self.assertEqual(result["result"]["capabilities"], {"tools": {}})
        self.assertNotIn("secret", json.dumps(result))

    def test_notifications_have_no_reply(self):
        self.assertIsNone(handle({"jsonrpc": "2.0", "method": "notifications/initialized"}, self.connection, "client"))

    def test_browser_does_not_advertise_commands(self):
        connection = {**self.connection, "backend": "browser"}
        result = handle(self.message("tools/list"), connection, "client")
        self.assertNotIn("workspace_run", {t["name"] for t in result["result"]["tools"]})
        result = handle(self.message("tools/call", {"name": "workspace_run"}), connection, "client")
        self.assertEqual(result["error"]["code"], -32602)

    def test_tool_list_explains_permission_boundary(self):
        result = handle(self.message("tools/list"), self.connection, "client")["result"]
        run = next(t for t in result["tools"] if t["name"] == "workspace_run")
        self.assertIn("NOT a filesystem sandbox", run["description"])
        self.assertIn("lease", run["inputSchema"]["required"])
        self.assertEqual(len({t["name"] for t in TOOLS}), len(TOOLS))

    @patch("deskweave.mcp.http_json", return_value={"result": {"png": "YWJj"}})
    def test_screenshot_is_mcp_image(self, http):
        result = handle(self.message("tools/call", {"name": "workspace_screenshot"}), self.connection, "client")
        self.assertEqual(result["result"]["content"], [{"type": "image", "data": "YWJj", "mimeType": "image/png"}])
        self.assertEqual(http.call_args.args[2]["client"], "client")

    @patch("deskweave.mcp.http_json", side_effect=RuntimeError("Control was revoked"))
    def test_tool_refusal_is_explicit_not_protocol_failure(self, http):
        result = handle(self.message("tools/call", {"name": "workspace_input", "arguments": {"lease": "old"}}), self.connection, "client")
        self.assertTrue(result["result"]["isError"])
        self.assertIn("revoked", result["result"]["content"][0]["text"])

    @patch("deskweave.mcp.http_json")
    def test_unknown_tool_never_reaches_broker(self, http):
        result = handle(self.message("tools/call", {"name": "owner_takeover"}), self.connection, "client")
        self.assertEqual(result["error"]["code"], -32602)
        http.assert_not_called()

    def test_invalid_messages_are_protocol_errors(self):
        self.assertEqual(handle([], self.connection, "client")["error"]["code"], -32600)
        self.assertEqual(handle(self.message("unknown"), self.connection, "client")["error"]["code"], -32601)


if __name__ == "__main__":
    unittest.main()
