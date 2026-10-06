using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// After the owner quits ARS, a new agent session gets an empty ARS: no instructions, no tools,
/// and no failed server in front of the owner. A tool call that still arrives is told to carry on
/// without it. Without the quit mark, a closed ARS is still reported the way it always was.
///
/// The bridge reads a fixture ticket folder, so it never starts or reaches the owner's ARS.
/// </summary>
internal static class BridgeQuit
{
    internal static void Run(Action<bool, string> check, string fixture)
    {
        string folder = Path.Combine(fixture, "bridge-quit");
        Directory.CreateDirectory(folder);
        string ticket = Path.Combine(folder, "router.json");

        (int code, string[] replies, string errors) = Session(ticket);
        check(code == 1 && replies.Length == 0 && errors.Contains("ARS isn't open", StringComparison.Ordinal),
            "Without a quit mark, a closed ARS is reported and the bridge exits");

        File.WriteAllBytes(Path.Combine(folder, "quit"), []);
        (code, replies, errors) = Session(ticket);
        check(code == 0 && replies.Length == 3 && errors.Length == 0, "After a quit, the bridge answers the session itself and exits cleanly");
        using JsonDocument hello = JsonDocument.Parse(replies[0]);
        JsonElement result = hello.RootElement.GetProperty("result");
        check(result.GetProperty("serverInfo").GetProperty("name").GetString() == "ars" && !result.TryGetProperty("instructions", out _),
            "After a quit, a new session hears no ARS instructions");
        using JsonDocument tools = JsonDocument.Parse(replies[1]);
        check(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() == 0,
            "After a quit, a new session lists no ARS tools");
        using JsonDocument call = JsonDocument.Parse(replies[2]);
        JsonElement answer = call.RootElement.GetProperty("result");
        check(answer.GetProperty("isError").GetBoolean()
            && answer.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("own tools", StringComparison.Ordinal),
            "After a quit, a tool call is told to carry on with the agent's own tools");
        Wake(check);
        check(WorkspaceAccessStore.QuitMark == Path.Combine(WorkspaceAccessStore.Root, "quit")
            && Path.GetDirectoryName(WorkspaceAccessStore.QuitMark) == Path.GetDirectoryName(WorkspaceAccessStore.RouterTicket),
            "The app leaves its quit mark beside the router ticket, where the bridge looks");
    }

    /// <summary>A session that began while ARS was quit gets its tools once the owner opens ARS, without a reconnect.</summary>
    static void Wake(Action<bool, string> check)
    {
        WorkspaceRouter.Stop();
        WorkspaceAccessStore.MarkQuit();
        try
        {
            using var session = new LiveRouter.Bridge(WorkspaceAccessStore.RouterTicket);
            object hello = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "probe", version = "1" } };
            JsonElement greeting = session.Request("initialize", hello);
            check(session.Request("tools/list", new { }).GetProperty("result").GetProperty("tools").GetArrayLength() == 0
                && greeting.GetProperty("result").GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean(),
                "A session that starts while ARS is quit lists no tools and says its list can change");
            WorkspaceRouter.Start();
            WorkspaceAccessStore.ClearQuit();
            string? line = session.Next(TimeSpan.FromSeconds(15));
            check(line is not null && line.Contains("notifications/tools/list_changed", StringComparison.Ordinal),
                "Opening ARS again tells that session its tools changed");
            check(session.Request("tools/list", new { }).GetProperty("result").GetProperty("tools").GetArrayLength() > 0,
                "That session then lists ARS's tools without reconnecting");
        }
        finally
        {
            WorkspaceAccessStore.ClearQuit();
            WorkspaceRouter.Stop();
        }
    }

    static (int Code, string[] Replies, string Errors) Session(string ticket)
    {
        var start = new ProcessStartInfo(WorkspaceConnections.Bridge) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--workspace");
        start.ArgumentList.Add(ticket);
        using Process bridge = Process.Start(start) ?? throw new IOException("Bridge failed to start.");
        Task<string> output = bridge.StandardOutput.ReadToEndAsync();
        Task<string> errors = bridge.StandardError.ReadToEndAsync();
        try
        {
            foreach (string line in new[]
            {
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"run","arguments":{"command":"echo hi"}}}""",
            }) bridge.StandardInput.WriteLine(line);
            bridge.StandardInput.Close();
        }
        catch (IOException) { }   // a bridge that already exited closed its end first
        if (!bridge.WaitForExit(15000)) { bridge.Kill(entireProcessTree: true); throw new TimeoutException("The bridge did not exit."); }
        return (bridge.ExitCode, output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            errors.Result.Trim());
    }
}
