using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Workspace-owned storage and launch environment. The historical class name is not a security
/// boundary: programs keep the launching user's Windows permissions. Redirected app-data and
/// temporary paths are defaults, not a restriction on the files an agent can use for its task.
/// </summary>
sealed class WorkspaceSandbox : IDisposable
{
    /// <summary>The workspace's default working folder, not its filesystem access boundary.</summary>
    public string Folder { get; }

    /// <summary>Unicode environment block with APPDATA, LOCALAPPDATA and TEMP redirected.</summary>
    public nint Environment { get; }

    bool _disposed;

    WorkspaceSandbox(string folder, nint environment)
    {
        Folder = folder;
        Environment = environment;
    }

    public static WorkspaceSandbox For(string desktopName) =>
        new(EnsureFolder(desktopName), EnvironmentBlock(FolderFor(desktopName)));

    /// <summary>Creates the workspace's default folders. Existing user files and ACLs are retained.</summary>
    public static string EnsureFolder(string desktopName)
    {
        string folder = FolderFor(desktopName);
        foreach (string part in new[] { folder, AppData(folder), LocalAppData(folder), Temp(folder) })
            Directory.CreateDirectory(part);
        return folder;
    }

    public static string FolderFor(string desktopName) => WorkspaceStore.FolderForDesktop(desktopName);

    static string AppData(string folder) => Path.Combine(folder, "appdata");

    static string LocalAppData(string folder) => Path.Combine(folder, "local");

    static string Temp(string folder) => Path.Combine(folder, "temp");

    /// <summary>
    /// Keep the existing app-data and temporary defaults for workspace continuity. Programs can
    /// also use other paths, including Windows known folders, with ordinary user permissions.
    /// </summary>
    static nint EnvironmentBlock(string folder)
    {
        // Windows requires the block sorted, and it is read as one run of KEY=VALUE\0 pairs.
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value && key.Length > 0)
                values[key] = value;

        values["APPDATA"] = AppData(folder);
        values["LOCALAPPDATA"] = LocalAppData(folder);
        values["TEMP"] = Temp(folder);
        values["TMP"] = Temp(folder);
        values["DESKWEAVE_WORKSPACE"] = folder;

        var block = new System.Text.StringBuilder();
        foreach (KeyValuePair<string, string> pair in values)
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    /// <summary>
    /// Gives restricted and Low-integrity child tokens access to this private desktop. Workspace
    /// applications themselves retain the launching user's normal token; Chromium creates its own
    /// restricted renderer/GPU tokens and must be able to attach those children. The Low mandatory
    /// label matches WinSta0 and the owner's Default desktop and does not lower a process token.
    /// Never change WinSta0: an inheritable ACE there would affect every desktop created later.
    /// </summary>
    public static void OpenToLowIntegrity(string desktopName)
    {
        nint desktop = Native.OpenDesktopW(desktopName, 0, false, Native.GenericAll | Native.WriteDac);
        if (desktop == 0)
            throw new InvalidOperationException(
                $"Could not reopen desktop {desktopName} to label it (error {Marshal.GetLastWin32Error()}).");

        try
        {
            GrantLow(desktop);
            LabelLow(desktop);
        }
        finally
        {
            Native.CloseDesktop(desktop);
        }
    }

    static void GrantLow(nint desktop)
    {
        int information = Native.DaclSecurityInformation;
        Native.GetUserObjectSecurity(desktop, ref information, null, 0, out int needed);
        byte[] buffer = new byte[needed];
        information = Native.DaclSecurityInformation;
        if (!Native.GetUserObjectSecurity(desktop, ref information, buffer, needed, out _)) return;

        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var low = new SecurityIdentifier(Native.LowIntegritySid);
        var restricted = new SecurityIdentifier(WellKnownSidType.RestrictedCodeSid, null);
        var packages = new SecurityIdentifier("S-1-15-2-1");
        var restrictedPackages = new SecurityIdentifier("S-1-15-2-2");
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var desktopWindowManager = new SecurityIdentifier($"S-1-5-90-0-{session}");
        var fontDriverHost = new SecurityIdentifier($"S-1-5-96-0-{session}");
        // Not inheritable, and on our own desktop only. The pollution accident came from an
        // inheritable ace on the station; these ACEs die with this desktop. Chromium's sandboxed
        // GPU process uses a restricted token, whose access check must also succeed against the
        // RESTRICTED SID; granting only the integrity SID makes that child exit with ACCESS_DENIED.
        descriptor.DiscretionaryAcl!.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), fontDriverHost, false, null));
        descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), desktopWindowManager, false, null));
        descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), restrictedPackages, false, null));
        descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), packages, false, null));
        descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), restricted, false, null));
        descriptor.DiscretionaryAcl.InsertAce(0, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, unchecked((int)Native.GenericAll), low, false, null));

        byte[] updated = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updated, 0);
        information = Native.DaclSecurityInformation;
        Native.SetUserObjectSecurity(desktop, ref information, updated);
    }

    static void LabelLow(nint desktop)
    {
        const byte SystemMandatoryLabelAce = 0x11;
        const int NoWriteUp = 0x1;

        var low = new SecurityIdentifier(Native.LowIntegritySid);
        byte[] sid = new byte[low.BinaryLength];
        low.GetBinaryForm(sid, 0);
        byte[] opaque = new byte[4 + sid.Length];
        BitConverter.GetBytes(NoWriteUp).CopyTo(opaque, 0);
        sid.CopyTo(opaque, 4);

        var sacl = new RawAcl(2 /* ACL_REVISION */, 1);
        sacl.InsertAce(0, new CustomAce((AceType)SystemMandatoryLabelAce, AceFlags.None, opaque));
        byte[] raw = new byte[sacl.BinaryLength];
        sacl.GetBinaryForm(raw, 0);

        int result = Native.SetSecurityInfo(desktop, Native.SeWindowObject,
            Native.LabelSecurityInformation, 0, 0, 0, raw);
        if (result != 0)
            throw new InvalidOperationException(
                $"Windows would not label the workspace desktop Low integrity (error {result}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Environment != 0) Marshal.FreeHGlobal(Environment);
    }
}
