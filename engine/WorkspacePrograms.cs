using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Turns what an agent calls a program into something CreateProcess can start. A person opens a
/// program by pressing Start and typing its name; an agent given only "the plain name of an
/// installed program" could open notepad and nothing that was not on PATH. Measured 2026-09-06:
/// asked to open HiveMind, the agent spent its first turns hunting for the executable and settled
/// on reading a Start Menu shortcut by hand.
///
/// Three lookups, in the order Windows itself uses: PATH, the App Paths registry key that Win+R
/// reads, then the Start Menu's shortcuts. Anything with a directory in it, or already a file, is
/// passed through untouched.
/// </summary>
internal static class WorkspacePrograms
{
    internal sealed record Shortcut(string Name, string Target, string Arguments);

    internal static (string Program, string? Arguments) Resolve(string program, string? arguments) =>
        Resolve(program, arguments, StartMenus());

    internal static (string Program, string? Arguments) Resolve(string program, string? arguments,
        IEnumerable<string> startMenus)
    {
        string name = program.Trim().Trim('"');
        if (name.Length == 0 || name.IndexOfAny(['\\', '/']) >= 0 || File.Exists(name) || OnPath(name))
            return (program, arguments);
        if (AppPath(name) is { } registered) return (registered, arguments);
        if (Find(name, startMenus) is { } link)
            return (link.Target, link.Arguments.Length == 0 ? arguments
                : string.IsNullOrEmpty(arguments) ? link.Arguments : link.Arguments + " " + arguments);
        return (program, arguments);
    }

    /// <summary>CreateProcess searches PATH for an .exe itself; this only asks whether it will find one.</summary>
    static bool OnPath(string name)
    {
        string file = Path.HasExtension(name) ? name : name + ".exe";
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(folder.Trim(), file))) return true; }
            catch (ArgumentException) { }
        }
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system, file))
            || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), file));
    }

    /// <summary>HKCU then HKLM App Paths, the registry Win+R and ShellExecute consult for a bare name.</summary>
    static string? AppPath(string name)
    {
        string key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\"
            + (Path.HasExtension(name) ? name : name + ".exe");
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? entry = root.OpenSubKey(key);
                if (entry?.GetValue(null) is string path && File.Exists(path = path.Trim().Trim('"')))
                    return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                or UnauthorizedAccessException) { }
        }
        return null;
    }

    internal static IEnumerable<string> StartMenus() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    /// <summary>
    /// The shortcut a person would pick from the Start Menu for that name: an exact match first,
    /// then one whose name starts with it, then one that merely contains it; the shortest name wins
    /// a tie, so "code" is Visual Studio Code rather than its Insiders build. Uninstallers are
    /// never offered, and a shortcut that points at nothing that exists is skipped.
    /// </summary>
    internal static Shortcut? Find(string name, IEnumerable<string> startMenus)
    {
        Shortcut? best = null;
        int bestRank = int.MaxValue;
        foreach (Shortcut shortcut in Shortcuts(startMenus))
        {
            if (shortcut.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
            int rank = shortcut.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? 0
                : shortcut.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? 1
                : shortcut.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            if (rank == 3) continue;
            if (rank < bestRank || rank == bestRank && shortcut.Name.Length < best!.Name.Length)
            {
                best = shortcut;
                bestRank = rank;
            }
        }
        return best;
    }

    // Reading every Start Menu shell link over COM measured 7.4 s on this PC, and `open` blocks the
    // agent's turn while it happens. The menu changes when something is installed, which is not
    // during a mission, so one scan is kept and reused.
    static readonly Lock Cached = new();
    static IReadOnlyList<Shortcut>? _cached;
    static string _cachedFor = string.Empty;
    static long _cachedAt;

    /// <summary>Every app a person could pick from the Start Menu, by name, once each, uninstallers
    /// left out. The same cached scan `open` uses; the first call can take seconds, so call it off
    /// the UI thread.</summary>
    internal static IReadOnlyList<Shortcut> Apps() =>
    [
        .. Shortcuts(StartMenus())
            .Where(s => !s.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) && s.Name.Length > 0)
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>Forgets the Start Menu scan, so the next lookup reads the shortcuts again.</summary>
    internal static void Forget()
    {
        lock (Cached) { _cached = null; _cachedFor = string.Empty; }
    }

    /// <summary>
    /// Reads the Start Menu now, so the first `open` by name does not pay for it. Called off the
    /// workspace start; failure is silent, because the lookup itself falls back to reading on demand.
    /// </summary>
    internal static void Warm()
    {
        try { Shortcuts(StartMenus()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static IEnumerable<Shortcut> Shortcuts(IEnumerable<string> startMenus)
    {
        string key = string.Join("|", startMenus);
        lock (Cached)
            if (_cached is not null && _cachedFor == key
                && Environment.TickCount64 - _cachedAt < 300_000)
                return _cached;

        IReadOnlyList<Shortcut> read = ReadAll(startMenus);
        lock (Cached)
        {
            _cached = read;
            _cachedFor = key;
            _cachedAt = Environment.TickCount64;
        }
        return read;
    }

    static IReadOnlyList<Shortcut> ReadAll(IEnumerable<string> startMenus)
    {
        var files = new List<string>();
        foreach (string menu in startMenus)
        {
            if (menu.Length == 0 || !Directory.Exists(menu)) continue;
            try { files.AddRange(Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Shell links are apartment-threaded COM objects; reading a few hundred of them on one
        // short STA thread is milliseconds and never touches the caller's apartment.
        var found = new List<Shortcut>();
        var reader = new Thread(() =>
        {
            foreach (string file in files)
                if (Read(file) is { } shortcut) found.Add(shortcut);
        }) { IsBackground = true, Name = "start-menu" };
        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        reader.Join(TimeSpan.FromSeconds(15));
        return found;
    }

    internal static Shortcut? Read(string file)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(file, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, 0, 0);
            var arguments = new StringBuilder(1024);
            link.GetArguments(arguments, arguments.Capacity);
            string exe = target.ToString();
            // Advertised (MSI) shortcuts and app aliases carry no path; the shell resolves those
            // through its own machinery and CreateProcess cannot.
            if (exe.Length == 0 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
                return null;
            return new Shortcut(Path.GetFileNameWithoutExtension(file), exe, arguments.ToString().Trim());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException
            or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Writes a shortcut. Only the tests use it, to build a Start Menu of their own.</summary>
    internal static void Write(string file, string target, string arguments)
    {
        Exception? failed = null;
        var writer = new Thread(() =>
        {
            try
            {
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(target);
                link.SetArguments(arguments);
                ((IPersistFile)link).Save(file, true);
            }
            catch (Exception ex) { failed = ex; }
        });
        writer.SetApartmentState(ApartmentState.STA);
        writer.Start();
        writer.Join();
        if (failed is not null) throw failed;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
