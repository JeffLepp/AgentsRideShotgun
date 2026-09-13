using System.Runtime.InteropServices;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Windows mixes audio per session, not per desktop, so an agent's sound would come out of the
/// owner's speakers. A workspace needs no audio at all, so every session belonging to one of its
/// processes is muted. Mute is per session and sticks, so this only has to catch a session once.
/// </summary>
static class WorkspaceAudio
{
    /// <summary>Mutes every audio session owned by a process the workspace owns. Returns how many.</summary>
    public static int Mute(Predicate<int> ownedByWorkspace)
    {
        int muted = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // Render only: nothing in a workspace should be playing, and a microphone is an input
            // the owner may still be using himself.
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IMMDevice device) != 0)
                return 0;

            var managerId = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerId, 23 /* CLSCTX_ALL */, 0, out object raw) != 0) return 0;
            var manager = (IAudioSessionManager2)raw;
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return 0;

            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl control) != 0) continue;
                if (control is not IAudioSessionControl2 session) continue;
                if (session.GetProcessId(out int processId) != 0 || !ownedByWorkspace(processId)) continue;
                if (control is not ISimpleAudioVolume volume) continue;
                Guid none = Guid.Empty;
                volume.GetMute(out bool already);
                if (already) continue;
                if (volume.SetMute(true, ref none) == 0) muted++;
            }
        }
        catch (COMException)
        {
            // No audio endpoint at all, which is a machine with nothing to leak onto.
        }
        return muted;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int NotImplementedEnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid interfaceId, int context, nint parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int NotImplementedGetAudioSessionControl(nint sessionId, int flags, out nint control);

        int NotImplementedGetSimpleAudioVolume(nint sessionId, int flags, out nint volume);

        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);

        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        int NotImplementedGetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl, then the four IAudioSessionControl2 methods. Only the last one
        // matters here, but every slot before it has to exist or the vtable is wrong.
        int GetState(out int state);

        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);

        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);

        int GetGroupingParam(out Guid grouping);

        int SetGroupingParam(ref Guid grouping, ref Guid context);

        int RegisterAudioSessionNotification(nint notification);

        int UnregisterAudioSessionNotification(nint notification);

        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

        int GetProcessId(out int processId);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid context);

        int GetMasterVolume(out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
