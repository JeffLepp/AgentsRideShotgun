using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

// Same-user authenticated router command. Never creates a workspace or activates the hub.
internal static class ControllerLifecycle
{
    internal static async Task<int> Command(string operation, string? source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            using var ticket = JsonDocument.Parse(File.ReadAllText(WorkspaceAccessStore.RouterTicket));
            string pipe = ticket.RootElement.GetProperty("pipe").GetString()!;
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(2000, timeout.Token);
            await WorkspacePipeProtocol.Write(client, ticket.RootElement.GetProperty("capability").GetString(), 256, timeout.Token);
            if (await WorkspacePipeProtocol.Read(client, 256, timeout.Token) != "workspace-pipe/1") return 1;
            await WorkspacePipeProtocol.Write(client, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "deskweave/lifecycle", @params = new { operation, source },
            }), WorkspacePipeProtocol.MaxRequestBytes, timeout.Token);
            string? reply = await WorkspacePipeProtocol.Read(client, WorkspacePipeProtocol.MaxResponseBytes, timeout.Token);
            if (reply is null) throw new IOException("Controller closed before acknowledging the command.");
            using var response = JsonDocument.Parse(reply);
            string status = response.RootElement.GetProperty("result").GetString()!;
            Console.WriteLine(status);
            return status.StartsWith("Accepted:", StringComparison.Ordinal) || operation == "status" ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or TimeoutException or UnauthorizedAccessException
            or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { Console.Error.WriteLine("ARS lifecycle command failed: " + error.Message); return 1; }
    }

    internal static bool WaitForPrevious(string pid, string ticks)
    {
        if (!int.TryParse(pid, out int id) || !long.TryParse(ticks, out long started)) return false;
        try
        {
            using var previous = Process.GetProcessById(id);
            if (previous.StartTime.ToUniversalTime().Ticks == started && !previous.WaitForExit(30000)) return false;
        }
        catch (ArgumentException) { }
        return !File.Exists(WorkspaceAccessStore.QuitMark);
    }

    internal static void ScheduleRestart()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--restart-after");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
        Process.Start(start)?.Dispose();
    }
}
