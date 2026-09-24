using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// Windows has exactly one clipboard per window station, and a window station needs administrator
/// to create. So the host gives every workspace what looks like a clipboard of its own instead: it
/// keeps a saved copy per workspace, swaps that copy in for as long as a paste takes, and files
/// every copy made on a workspace desktop under that workspace before handing the owner's clipboard
/// back.
///
/// Everything here is the plain Win32 clipboard, deliberately. Measured: the managed
/// clipboard goes through OLE, and two desktops using OLE lock each other out with
/// CLIPBRD_E_CANT_OPEN - the second workspace to copy anything simply fails, whatever the timing.
/// The raw calls hold the clipboard for microseconds and work from every desktop at once.
///
/// The owner's clipboard is never left holding an agent's copy, and two agents never see each
/// other's. Known limits: two workspaces copying within the same settle window can be attributed in
/// the wrong order, because Windows reports the change after the fact; and a copy made with no
/// window to name its desktop is treated as the owner's (see Settled).
/// </summary>
public sealed class ClipboardBroker : IDisposable
{
    /// <summary>Anything larger than this is a video frame, not a clipboard. Skipped, not copied.</summary>
    const int LargestFormat = 32 * 1024 * 1024;

    /// <summary>
    /// The formats worth carrying between clipboards. Deliberately not "everything on offer":
    /// an application that offers a format it renders on demand is asked to render it while we hold
    /// the clipboard open, and an application that is slow to answer - or has no message pump yet -
    /// holds the clipboard shut for everyone else. Measured: that is what made a second workspace's
    /// copy fail. These are plain memory blocks that are already there.
    /// </summary>
    static readonly uint[] Carried =
    [
        13 /* CF_UNICODETEXT */, 1 /* CF_TEXT */, 7 /* CF_OEMTEXT */, 15 /* CF_HDROP */,
        8 /* CF_DIB */, 17 /* CF_DIBV5 */,
        Native.RegisterClipboardFormatW("HTML Format"),
        Native.RegisterClipboardFormatW("Rich Text Format"),
        Native.RegisterClipboardFormatW("FileNameW")
    ];

    static ClipboardBroker? _shared;

    public static ClipboardBroker Shared => _shared ??= new ClipboardBroker();

    readonly Dictionary<string, Dictionary<uint, byte[]>?> _saved = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _workspaces = new(StringComparer.OrdinalIgnoreCase);
    readonly Thread _thread;
    Dispatcher? _dispatcher;
    HwndSource? _window;
    Dictionary<uint, byte[]>? _owner;
    DispatcherTimer? _settle;
    string? _changedOn;
    // Every swap we make raises a clipboard change of our own. Counting them is what stops the
    // listener filing our own hand-back as if the owner had copied something.
    int _selfChanges;
    bool _disposed;

    /// <summary>How many times the broker has swapped the clipboard. A runaway count is a bug.</summary>
    public int Swaps { get; private set; }

    /// <summary>How many times it could not get the clipboard at all.</summary>
    public int Refused { get; private set; }

    ClipboardBroker()
    {
        using var ready = new ManualResetEventSlim();
        // The broker owns one thread rather than borrowing the host's UI thread. It needs
        // a window and a message pump, and a wedged application must not be able to freeze the app.
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _window = new HwndSource(new HwndSourceParameters("DeskweaveClipboardBroker")
            {
                ParentWindow = Native.HwndMessage,
                HwndSourceHook = OnMessage
            });
            Native.AddClipboardFormatListener(_window.Handle);
            // Windows announces the change in the middle of the copy, and touching the clipboard
            // before the copier has finished is what makes an application fail. So the broker notes
            // who changed it and does its reading and writing once the dust has settled.
            _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal,
                (_, _) => Settled(), Dispatcher.CurrentDispatcher);
            _settle.Stop();
            _owner = Snapshot();
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "deskweave-clipboard-broker" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Starts giving this desktop a clipboard of its own.</summary>
    public void Register(string desktop) => On(() =>
    {
        _workspaces.Add(desktop);
        _saved.TryAdd(desktop, null);
    });

    /// <summary>Stops, and forgets what that workspace had copied.</summary>
    public void Unregister(string desktop) => On(() =>
    {
        _workspaces.Remove(desktop);
        _saved.Remove(desktop);
    });

    /// <summary>
    /// Runs one action with the workspace's own clipboard loaded, then puts the owner's back. The
    /// action must be synchronous - a paste that has not landed yet is a paste that gets the wrong
    /// clipboard.
    /// </summary>
    public void WithClipboardOf(string desktop, Action action)
    {
        On(() =>
        {
            // A pending hand-back would fire in the middle of the paste and swap the wrong text in.
            if (_settle?.IsEnabled == true) Settled();
            // A swap that did not happen leaves the owner's clipboard in place, and pasting that into
            // an agent's window is exactly what the broker exists to prevent.
            if (!Restore(_saved.GetValueOrDefault(desktop)))
                throw new InvalidOperationException("The workspace clipboard could not be loaded, so nothing was pasted.");
        });
        try
        {
            action();
        }
        finally
        {
            On(HandBack);
        }
    }

    /// <summary>Puts the owner's clipboard back, retrying, and says so when it could not.</summary>
    void HandBack()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Restore(_owner)) return;
            Thread.Sleep(100);
        }
        Trace.WriteLine("ARS could not hand the clipboard back to the owner.");
    }

    /// <summary>What this workspace last copied, as text. For proofs and for the boss chat.</summary>
    public string? TextOf(string desktop)
    {
        string? text = null;
        On(() => text = TextIn(_saved.GetValueOrDefault(desktop)));
        return text;
    }

    /// <summary>The owner's clipboard as the broker believes it to be. For proofs.</summary>
    public string? OwnerText()
    {
        string? text = null;
        On(() => text = TextIn(_owner));
        return text;
    }

    static string? TextIn(Dictionary<uint, byte[]>? blob)
    {
        if (blob is null || !blob.TryGetValue(Native.CfUnicodeText, out byte[]? bytes)) return null;
        string text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    nint OnMessage(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message != (int)Native.WmClipboardUpdate) return 0;
        if (_selfChanges > 0) { _selfChanges--; return 0; }

        // Attribution has to be read now: it is a window handle lookup, not a clipboard call, and
        // the owning window can be gone by the time the dust settles.
        _changedOn = DesktopOfClipboardOwner();
        _settle?.Stop();
        _settle?.Start();
        return 0;
    }

    void Settled()
    {
        _settle?.Stop();
        // Nobody owns the clipboard when the copier opened it with a null window, which leaves
        // nothing to ask. Such a copy stays the owner's. Guessing towards a workspace would take
        // the owner's copy out of their clipboard and hand it to an agent; guessing this way the
        // cost is an agent app that copies anonymously landing its text in the owner's clipboard,
        // which they can see and overwrite. Leaking their data to an agent is the worse failure.
        if (_changedOn is not null && _workspaces.Contains(_changedOn))
        {
            // An agent copied something. It belongs to that workspace and to nobody else.
            _saved[_changedOn] = Snapshot();
            HandBack();
        }
        else
        {
            _owner = Snapshot();
        }
        _changedOn = null;
    }

    /// <summary>
    /// Which desktop the window that owns the clipboard lives on. Threads in one window station can
    /// be asked about each other's desktops, which is what makes this attribution possible at all.
    /// </summary>
    static string? DesktopOfClipboardOwner()
    {
        nint owner = Native.GetClipboardOwner();
        if (owner == 0) return null;
        int thread = Native.GetWindowThreadProcessId(owner, out _);
        if (thread == 0) return null;
        nint desktop = Native.GetThreadDesktop(thread);
        if (desktop == 0) return null;
        var name = new StringBuilder(256);
        return Native.GetUserObjectInformationW(desktop, Native.UoiName, name, name.Capacity * 2, out _)
            ? name.ToString()
            : null;
    }

    Dictionary<uint, byte[]>? Snapshot()
    {
        if (!Open()) return null;
        try
        {
            var blob = new Dictionary<uint, byte[]>();
            foreach (uint format in Carried)
            {
                if (format == 0 || !Native.IsClipboardFormatAvailable(format)) continue;
                nint handle = Native.GetClipboardData(format);
                if (handle == 0) continue;
                nuint size = Native.GlobalSize(handle);
                if (size == 0 || size > LargestFormat) continue;
                nint memory = Native.GlobalLock(handle);
                if (memory == 0) continue;
                byte[] bytes = new byte[(int)size];
                Marshal.Copy(memory, bytes, 0, bytes.Length);
                Native.GlobalUnlock(handle);
                blob[format] = bytes;
            }
            return blob;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>
    /// Replaces the clipboard with this copy. False when the clipboard still holds what it held
    /// before, because it could not be opened or emptied.
    /// </summary>
    bool Restore(Dictionary<uint, byte[]>? blob)
    {
        if (!Open()) return false;
        try
        {
            if (!Native.EmptyClipboard()) return false;
            _selfChanges++;
            Swaps++;
            if (blob is null) return true;
            foreach (KeyValuePair<uint, byte[]> format in blob)
            {
                nint block = Native.GlobalAlloc(Native.GlobalMoveable, (nuint)format.Value.Length);
                if (block == 0) continue;
                nint memory = Native.GlobalLock(block);
                if (memory == 0) { Native.GlobalFree(block); continue; }
                Marshal.Copy(format.Value, 0, memory, format.Value.Length);
                Native.GlobalUnlock(block);
                // Windows owns the block once SetClipboardData accepts it, and frees it itself.
                if (Native.SetClipboardData(format.Key, block) == 0) Native.GlobalFree(block);
            }
            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>
    /// Opens the clipboard, retrying briefly. Somebody else holding it for a moment is ordinary -
    /// the reason the managed clipboard throws is that it gives up and calls that an error.
    /// </summary>
    bool Open()
    {
        nint window = _window?.Handle ?? 0;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (Native.OpenClipboard(window)) return true;
            Thread.Sleep(15);
        }
        Refused++;
        return false;
    }

    void On(Action action)
    {
        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher is null || _disposed) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispatcher? dispatcher = _dispatcher;
        dispatcher?.Invoke(() =>
        {
            if (_window is not null) Native.RemoveClipboardFormatListener(_window.Handle);
            _window?.Dispose();
        });
        dispatcher?.InvokeShutdown();
        if (ReferenceEquals(_shared, this)) _shared = null;
    }
}
