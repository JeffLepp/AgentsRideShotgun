using System.Windows.Input;
using System.Windows.Interop;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// One global hotkey, on a message-only window of this module's own. Windows gives a combination to
/// exactly one registrant, so a combination another program already holds is refused rather than
/// taken from it - and the panel says so instead of leaving the owner pressing a dead key.
/// </summary>
internal sealed class WorkspacePeekHotkey : IDisposable
{
    // Any small number, unique within this window. The window is ours, so nothing else uses it.
    const int Id = 0x4157;

    readonly HwndSource _source;
    bool _held;
    bool _disposed;

    internal WorkspacePeekHotkey()
    {
        // A zero-sized, styleless window, never shown. Not a message-only one: WM_HOTKEY is posted
        // to the registering window, and this is the shape the rest of HiveMind already registers
        // its hotkeys with.
        _source = new HwndSource(new HwndSourceParameters("DeskweaveWorkspacePeekHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(Message);
    }

    /// <summary>Raised on the UI thread when the owner presses it.</summary>
    internal event Action? Pressed;

    /// <summary>
    /// Takes the combination, letting go of whatever was held before. False when the text is not a
    /// hotkey or Windows refused it, which is nearly always another program holding it already.
    /// </summary>
    internal bool Hold(string? text)
    {
        Release();
        if (_disposed || !WorkspaceHotkey.Parse(text, out ModifierKeys modifiers, out Key key)) return false;

        uint flags = Native.ModNoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= Native.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= Native.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= Native.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= Native.ModWin;

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;
        _held = Native.RegisterHotKey(_source.Handle, Id, flags, virtualKey);
        return _held;
    }

    internal void Release()
    {
        if (!_held) return;
        Native.UnregisterHotKey(_source.Handle, Id);
        _held = false;
    }

    nint Message(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message != Native.WmHotKey || wparam != Id) return 0;
        handled = true;
        Pressed?.Invoke();
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _source.RemoveHook(Message);
        _source.Dispose();
    }
}
