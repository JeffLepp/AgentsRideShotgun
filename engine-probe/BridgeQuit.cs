using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// A bridge stays with its client whether ARS is closed or the owner quit it, and says which.
/// After an explicit owner Quit it says the owner turned ARS off rather than inviting the agent
/// to work some other way. The bridge reads a fixture ticket folder, so it never starts or
/// reaches the owner's ARS.
/// </summary>
internal static class BridgeQuit
{
    internal static void Run(Action<bool, string> check, string fixture)
    {
        string folder = Path.Combine(fixture, "bridge-quit");
        Directory.CreateDirectory(folder);
        string ticket = Path.Combine(folder, "router.json");

        (int code, string[] replies, string errors, string? call) = Session(ticket);
        check(code == 0 && replies.Length >= 3 && call == BridgeTexts.Restarting && errors.Contains("ARS isn't open", StringComparison.Ordinal),
            "Without a quit mark, a closed ARS is answered with the retry text and the bridge stays until its client leaves");

        File.WriteAllBytes(Path.Combine(folder, "quit"), []);
        (code, replies, errors, call) = Session(ticket);
        check(code == 0 && replies.Length >= 3 && call == BridgeTexts.TurnedOff
            && !replies.Any(reply => reply.Contains("Carry on with your own tools", StringComparison.Ordinal)),
            "After a quit, a new bridge says the owner turned ARS off, invites no other route, and leaves when its client does");
        check(WorkspaceAccessStore.QuitMark == Path.Combine(WorkspaceAccessStore.Root, "quit")
            && Path.GetDirectoryName(WorkspaceAccessStore.QuitMark) == Path.GetDirectoryName(WorkspaceAccessStore.RouterTicket),
            "The app leaves its quit mark beside the router ticket, where the bridge looks");
    }

    /// <summary>The handshake, the tool list and one tool call; then stdin closes. Returns the exit code,
    /// every stdout line, stderr, and the tool call's isError text (null when it was not an isError reply).</summary>
    static (int Code, string[] Replies, string Errors, string? Call) Session(string ticket)
    {
        var start = new ProcessStartInfo(WorkspaceConnections.Bridge) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(ticket);
        using Process bridge = Process.Start(start) ?? throw new IOException("Bridge failed to start.");
        Task<string> errors = bridge.StandardError.ReadToEndAsync();
        var replies = new List<string>();
        string? call = null;
        try
        {
            foreach (string line in new[]
            {
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run","arguments":{"command":"echo hi"}}}""",
            }) bridge.StandardInput.WriteLine(line);
            bridge.StandardInput.Flush();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (call is null)
            {
                string? line = bridge.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line is null) break;
                replies.Add(line);
                using var reply = JsonDocument.Parse(line);
                if (reply.RootElement.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 3)
                {
                    call = reply.RootElement.TryGetProperty("result", out JsonElement result)
                        && result.TryGetProperty("isError", out JsonElement failed) && failed.GetBoolean()
                        ? result.GetProperty("content")[0].GetProperty("text").GetString() : "";
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        // Still running here is the point: only the client leaving ends it.
        int code = bridge.HasExited ? -2 : 0;
        try { bridge.StandardInput.Close(); } catch (IOException) { }
        if (!bridge.WaitForExit(5000)) { bridge.Kill(entireProcessTree: true); throw new TimeoutException("The bridge did not exit on EOF."); }
        if (code == 0) code = bridge.ExitCode;
        return (code, [.. replies], errors.Result.Trim(), call);
    }
}
