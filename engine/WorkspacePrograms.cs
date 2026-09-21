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
        if (name.Length == 0) return (program, arguments);
        if (name.IndexOfAny(['\\', '/']) >= 0) return (name, arguments);
        if (File.Exists(name)) return (Path.GetFullPath(name), arguments);
        // Pin the same file that classification inspected. CreateProcess's own search order
        // differs from PATH and must not substitute a system activation stub after the check.
        if (PathFile(name, ".exe") is { } executable) return (executable, arguments);
        if (AppPath(name) is { } registered) return (registered, arguments);
        if (Find(name, startMenus) is { } link)
            return (link.Target, link.Arguments.Length == 0 ? arguments
                : string.IsNullOrEmpty(arguments) ? link.Arguments : link.Arguments + " " + arguments);
        return (program, arguments);
    }

    /// <summary>
    /// True when only the Windows shell can start this: a Store app's execution alias - the
    /// zero-byte APPEXECLINK reparse points under WindowsApps - anything inside the packaged app
    /// folders, a known Windows activation stub, Explorer, or an explicit shell: target such as
    /// shell:AppsFolder. The shell runs on the
    /// owner's own desktop and nowhere else, so from a workspace CreateProcess either refuses
    /// these outright or starts a stub that hands the request over and exits, which in here is
    /// indistinguishable from a crash. Deciding it before the launch is what lets `open` say which
    /// it is and offer the owner's desktop instead of reporting a program as broken.
    /// </summary>
    internal static bool ShellOnly(string program)
    {
        string name = program.Trim().Trim('"');
        if (name.Length == 0) return false;
        if (name.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        string? file = name.IndexOfAny(['\\', '/']) >= 0 || File.Exists(name)
            ? name : PathFile(name, ".exe") ?? AppPath(name) ?? Find(name, StartMenus())?.Target;
        if (file is null) return false;
        if (file.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;
        if (SystemShellRelay(file, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.OSVersion.Version)) return true;
        try
        {
            var info = new FileInfo(file);
            return info.Exists && (info.Length == 0 || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// These genuine system executables can activate a packaged app or an existing Explorer on
    /// the interactive desktop. Checking their exact location and OS generation preserves classic
    /// Windows 10 Notepad/Paint and unrelated applications with the same filenames. Kept pure so
    /// both supported Windows generations can be checked without launching a relay on the owner.
    /// </summary>
    internal static bool SystemShellRelay(string file, string windowsDirectory, Version windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        try
        {
            string full = Path.GetFullPath(file);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
            string windows = Path.GetFullPath(windowsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool At(string directory, string executable) =>
                full.Equals(Path.Combine(directory, executable), StringComparison.OrdinalIgnoreCase);
            if (At(windows, "explorer.exe")) return true;

            bool windows10 = windowsVersion >= new Version(10, 0, 10240);
            bool windows11 = windowsVersion >= new Version(10, 0, 22000);
            if (windows11 && At(windows, "notepad.exe")) return true;
            foreach (string folder in new[] { "System32", "SysWOW64", "Sysnative" })
            {
                string system = Path.Combine(windows, folder);
                if (windows10 && At(system, "calc.exe")) return true;
                if (windows11 && (At(system, "notepad.exe") || At(system, "mspaint.exe"))) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    /// <summary>
    /// The file a bare command name resolves to on PATH, as a full path: each folder PATH names in
    /// turn, then System32 and the Windows directory, trying the given extensions in order within
    /// each folder, the way Windows reads PATHEXT. A name that already has an extension is looked
    /// for as it stands. Null when nothing there answers to that name.
    ///
    /// <see cref="Resolve"/> only needs to know whether CreateProcess will find something itself;
    /// <see cref="WorkspaceConnections"/> has to hand the resolved path to Process.Start, so it
    /// asks here rather than keeping a second copy of this walk.
    /// </summary>
    internal static string? PathFile(string name, params string[] extensions)
    {
        string[] files = Path.HasExtension(name) ? [name] : [.. extensions.Select(e => name + e)];
        foreach (string folder in Folders())
            foreach (string file in files)
            {
                // Never a bare name back: a PATH entry can be relative, and the caller starts a
                // process with whatever this returns.
                try { if (File.Exists(Path.Combine(folder, file))) return Path.GetFullPath(Path.Combine(folder, file)); }
                catch (ArgumentException) { }
            }
        return null;

        static IEnumerable<string> Folders()
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries))
                yield return folder.Trim();
            yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }
    }

    /// <summary>HKCU then HKLM App Paths, the registry Win+R and ShellExecute consult for a bare name.</summary>
    internal static string? AppPath(string name)
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
