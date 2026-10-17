namespace Deskweave.AgentWorkspaces;

public sealed partial class WorkspaceControl
{
    readonly SemaphoreSlim _ownerBrowserInput = new(1, 1);

    /// <summary>Captured when the owner acts, before the UI queues the event. A release followed
    /// by another takeover cannot make old queued input valid again.</summary>
    internal CancellationToken OwnerInputToken
    {
        get { lock (_gate) return !_disposed && Driving == Driver.Owner
            ? _inputStop.Token : new CancellationToken(canceled: true); }
    }

    internal Task<bool> OwnerBrowserPointer(string type, int x, int y, bool rightButton,
        bool secondClick, CancellationToken lease)
        => type == "mouseReleased" ? EndOwnerBrowserGesture(lease, x, y)
            : OwnerBrowserInput((browser, cancel) => browser.OwnerPointer(type, x, y,
                rightButton, secondClick, lease, cancel), lease);

    internal Task<bool> OwnerBrowserAction(ComputerAction action, CancellationToken lease)
        => OwnerBrowserInput((browser, cancel) => browser.ComputerInput(action, cancel), lease);

    async Task<bool> OwnerBrowserInput(Func<WorkspaceBrowser, CancellationToken, Task<bool>> input,
        CancellationToken lease)
    {
        WorkspaceBrowser browser;
        CancellationTokenSource scope;
        lock (_gate)
        {
            if (_disposed || Driving != Driver.Owner || lease.IsCancellationRequested
                || lease != _inputStop.Token || _browser is not { UsesOwnerSession: true, HasContent: true } live)
                return false;
            browser = live;
            scope = CancellationTokenSource.CreateLinkedTokenSource(lease);
        }
        using (scope)
        {
            scope.CancelAfter(TimeSpan.FromSeconds(2));
            bool entered = false;
            try
            {
                await _ownerBrowserInput.WaitAsync(scope.Token).ConfigureAwait(false);
                entered = true;
                // The selected screen can change between a screenshot and the owner's click. Dropping
                // input in that case is safe; routing it to another surface would not be.
                if (!UsesBrowserViewport || scope.IsCancellationRequested) return false;
                return await input(browser, scope.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
                or ObjectDisposedException) { return false; }
            finally { if (entered) _ownerBrowserInput.Release(); }
        }
    }

    /// <summary>Only a button release, allowed after the owner lets go. Pending presses carry
    /// the old token and are discarded; this cannot grant access to any other browser page.</summary>
    internal async Task<bool> EndOwnerBrowserGesture(CancellationToken lease, int? x = null, int? y = null)
    {
        if (_browser is not { UsesOwnerSession: true } browser) return true;
        await _ownerBrowserInput.WaitAsync().ConfigureAwait(false);
        try { return x is { } atX && y is { } atY
            ? await browser.OwnerPointer("mouseReleased", atX, atY, false, false, lease, CancellationToken.None).ConfigureAwait(false)
            : await browser.OwnerEndGesture(lease).ConfigureAwait(false); }
        finally { _ownerBrowserInput.Release(); }
    }
}
