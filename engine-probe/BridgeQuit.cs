using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Explicit owner Quit ends bridges without reopening the controller. A later MCP
/// connection must be refreshed after ARS is opened again.
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
        check(code == 0 && replies.Length == 0 && errors.Length == 0, "After a quit, a new bridge exits cleanly without reopening ARS or lingering" );
        check(WorkspaceAccessStore.QuitMark == Path.Combine(WorkspaceAccessStore.Root, "quit")
            && Path.GetDirectoryName(WorkspaceAccessStore.QuitMark) == Path.GetDirectoryName(WorkspaceAccessStore.RouterTicket),
            "The app leaves its quit mark beside the router ticket, where the bridge looks");
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
            // This check asks a connected client to receive the startup diagnostic.
            // EOF now intentionally cancels startup promptly; that is covered by BridgeLifetime.
            bridge.StandardInput.Flush();
        }
        catch (IOException) { }   // a bridge that already exited closed its end first
        if (!bridge.WaitForExit(15000)) { bridge.Kill(entireProcessTree: true); throw new TimeoutException("The bridge did not exit."); }
        return (bridge.ExitCode, output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            errors.Result.Trim());
    }
}
