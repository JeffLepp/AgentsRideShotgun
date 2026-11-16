using System.IO;
using System.Security.Cryptography;
using System.Text;
using Deskweave.Product;

namespace Deskweave.AgentWorkspaces;

/// <summary>Immutable bridge payloads outside Velopack's replaceable current directory.</summary>
internal static class WorkspaceBridgeHost
{
    static readonly Lock InstallGate = new();
    static string? _installed;

    static string Installed()
    {
        lock (InstallGate)
            return _installed ??= Materialize(Path.GetDirectoryName(WorkspaceConnections.PackagedBridge)!,
                ProductContext.Local("bridge-hosts"));
    }

    internal static string PathForConnection => File.Exists(Path.Combine(
        Path.GetDirectoryName(typeof(WorkspaceMcp).Assembly.Location)!, "sq.version"))
        ? Installed() : WorkspaceConnections.PackagedBridge;

    internal static string Materialize(string source, string root)
    {
        // Hash the entire self-contained runtime too: two SDK versions must not share a host.
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var manifest = new StringBuilder();
        foreach (string file in files)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Bridge links are not supported.");
            using var stream = File.OpenRead(file);
            manifest.Append(Path.GetRelativePath(source, file)).Append(':').Append(Convert.ToHexString(SHA256.HashData(stream))).Append('\n');
        }
        string controller = Path.GetFullPath(Path.Combine(source, "..", "ARS.exe"));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString() + controller)));
        string target = Path.Combine(root, identity);
        Directory.CreateDirectory(root);
        using var serial = new Mutex(false, @"Local\ARS.BridgeHost." + identity);
        try { serial.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            if (!Directory.Exists(target))
            {
                string stage = target + "." + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(stage);
                foreach (string file in files)
                {
                    string destination = Path.Combine(stage, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                File.WriteAllText(Path.Combine(stage, "payload.sha256"), manifest.ToString());
                File.WriteAllText(Path.Combine(stage, "controller-path.txt"), controller);
                Directory.Move(stage, target);
            }
            // Independently read back before advertising a runnable host. Do not repair a live host.
            foreach (string line in manifest.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.LastIndexOf(':');
                using var stream = File.OpenRead(Path.Combine(target, line[..colon]));
                if (Convert.ToHexString(SHA256.HashData(stream)) != line[(colon + 1)..]) throw new IOException("Bridge host payload differs.");
            }
            return Path.Combine(target, "ARS.WorkspaceBridge.exe");
        }
        finally { serial.ReleaseMutex(); }
    }

    internal static bool Owns(string command)
    {
        string root = Path.GetFullPath(ProductContext.Local("bridge-hosts")) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(command).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
