using System.Text.Json.Nodes;

namespace Deskweave.AgentWorkspaces;

public sealed partial class WorkspaceBrowser
{
    string _ownerMouseSession = "", _ownerMouseButton = "left";
    double _ownerMouseX, _ownerMouseY;
    int _ownerMouseCount;
    CancellationToken _ownerMouseLease;

    /// <summary>The owner's gesture on the mirrored page. Every event stays in CDP; it never
    /// moves their real pointer or activates a browser window. A release uses the session that
    /// received its press, even if a popup has since become ARS's current tab.</summary>
    internal async Task<bool> OwnerPointer(string type, int x, int y, bool rightButton,
        bool secondClick, CancellationToken lease, CancellationToken cancel)
    {
        if (type == "mouseReleased")
        {
            if (_ownerMouseLease == lease) { _ownerMouseX = x; _ownerMouseY = y; }
            return await OwnerEndGesture(lease).ConfigureAwait(false);
        }
        if (_ownerMouseSession.Length == 0) await Follow(cancel).ConfigureAwait(false);
        string session = _ownerMouseSession.Length > 0 ? _ownerMouseSession : _session;
        if (session.Length == 0 || cancel.IsCancellationRequested) return false;
        if (type == "mousePressed")
        {
            if (_ownerMouseSession.Length > 0) await OwnerEndGesture().ConfigureAwait(false);
            _ownerMouseSession = session;
            _ownerMouseButton = rightButton ? "right" : "left";
            _ownerMouseCount = secondClick ? 2 : 1;
            _ownerMouseLease = lease;
        }
        _ownerMouseX = x; _ownerMouseY = y;
        string button = _ownerMouseSession.Length > 0 ? _ownerMouseButton : "none";
        bool delivered = await OwnerMouse(type, x, y, button,
            _ownerMouseSession.Length > 0 ? (button == "right" ? 2 : 1) : 0,
            type == "mousePressed" ? _ownerMouseCount : 0, session, cancel).ConfigureAwait(false);
        // Chromium may have received a press even when its acknowledgement was lost. Clean up
        // that gesture without replaying input or leaving a button held on an unobserved page.
        if (type == "mousePressed" && !delivered) await OwnerEndGesture().ConfigureAwait(false);
        return delivered;
    }

    internal async Task<bool> OwnerEndGesture(CancellationToken? expectedLease = null)
    {
        string session = _ownerMouseSession;
        if (session.Length == 0 || expectedLease is { } lease && lease != _ownerMouseLease) return true;
        _ownerMouseSession = "";
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        return await OwnerMouse("mouseReleased", _ownerMouseX, _ownerMouseY, _ownerMouseButton,
            0, _ownerMouseCount, session, cleanup.Token).ConfigureAwait(false);
    }

    async Task<bool> OwnerMouse(string type, double x, double y, string button, int buttons,
        int count, string session, CancellationToken cancel)
        => await Call("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button,
            ["buttons"] = buttons, ["clickCount"] = count,
        }, cancel, session).ConfigureAwait(false) is not null;
}
