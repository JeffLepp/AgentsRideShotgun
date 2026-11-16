using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

// The suite's simulated controller UI also stays on an ARS desktop. Never switches
// the input desktop, uses the live controller, or changes its workspace collection.
internal static class IsolatedSuite
{
    internal static int Run(string executable, string mode, string output)
    {
        Directory.CreateDirectory(output);
        // Test host folders belong to the report, not the owner's saved workspaces.
        using var hostStore = WorkspaceStore.UseRootForTests(Path.Combine(output, "isolation-host"));
        string name = "ARS-ProbeHost-" + Guid.NewGuid().ToString("N")[..10];
        Environment.SetEnvironmentVariable("ARS_PROBE_HOST_DESKTOP", name);
        string receipt = Path.Combine(output, "suite-startup.json");
        Environment.SetEnvironmentVariable("ARS_PROBE_DESKTOP_RECEIPT", receipt);
        using var desktop = AgentDesktop.Create(name);
        string arguments = (mode == "full" ? "" : mode + " ") + "\"" + output + "\"";
        int pid = desktop.Launch(executable, arguments);
        if (pid == 0) throw new IOException("Could not launch the suite on its private ARS desktop.");
        using var process = Process.GetProcessById(pid);
        _ = process.Handle; // Retain exact identity so ExitCode is readable after Windows releases the PID.
        bool exited = process.WaitForExit(420000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
        }
        string? startupDesktop = null;
        if (File.Exists(receipt))
        {
            using var observed = JsonDocument.Parse(File.ReadAllText(receipt));
            if (observed.RootElement.GetProperty("pid").GetInt32() == pid)
                startupDesktop = observed.RootElement.GetProperty("desktop").GetString();
        }
        File.WriteAllText(Path.Combine(output, "isolation.json"), JsonSerializer.Serialize(new
        { desktop = name, startupDesktop, pid, executable, mode, exited, exitCode = exited ? process.ExitCode : -1 }));
        return startupDesktop == name && exited ? process.ExitCode : 2;
    }

}
