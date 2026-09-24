using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// MCP stdout belongs entirely to ARS.WorkspaceBridge. This connector reports
// preflight failures on stderr and proxies the host's stdio streams without JSON changes.
if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
{
    Console.Error.WriteLine("ARS currently supports Windows x64. See the release page for supported systems.");
    return 2;
}

string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
string install = Path.Combine(local, "ARSApp", "current");
string app = Path.Combine(install, "ARS.exe");
string bridge = Path.Combine(install, "Bridge", "ARS.WorkspaceBridge.exe");
if (!File.Exists(app) || !File.Exists(bridge))
{
    Console.Error.WriteLine("ARS is not installed for this Windows user. Install ARS from https://github.com/JeffLepp/Deskweave/releases, open it once, then reconnect this MCP server.");
    return 2;
}

string ticket = Path.Combine(local, "ARS", "agent-workspaces.access", "router.json");
var start = new ProcessStartInfo(bridge)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    // The agent host's working directory is the project context sent by the bridge.
    WorkingDirectory = Environment.CurrentDirectory,
};
start.ArgumentList.Add("--workspace");
start.ArgumentList.Add(ticket);

try
{
    using Process? child = Process.Start(start);
    if (child is null)
    {
        Console.Error.WriteLine("ARS's MCP bridge could not start. Reinstall ARS and reconnect this server.");
        return 1;
    }

    Task input = ForwardInput();
    Task output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    Task errors = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
    await child.WaitForExitAsync();
    await Task.WhenAll(output, errors);
    // If the bridge exited first, the host's stdin may remain open. This process exits with it.
    _ = input;
    return child.ExitCode;

    async Task ForwardInput()
    {
        try { await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream); }
        catch (IOException) { /* The bridge closed before the host's input. */ }
        finally { child.StandardInput.Close(); }
    }
}
catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"ARS's MCP bridge could not start ({error.GetType().Name}). Reinstall ARS and reconnect this server.");
    return 1;
}