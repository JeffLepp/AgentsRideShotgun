using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

internal sealed record ComputerResult(ComputerReceipt Receipt, string Target, int Width, int Height, BitmapSource? Frame);

public sealed partial class WorkspaceControl
{
    readonly SemaphoreSlim _computerActions = new(1, 1);

    internal async Task<ComputerResult> Computer(ComputerRequest request, CancellationToken cancel)
    {
        long ticket = Ticket;
        using var input = InputScope(ticket, cancel);
        bool ownsLease() => ticket != 0 && Ticket == ticket;
        // Two coordinate spaces. `source` is what the desktop and the browser actually use; `image`
        // is the picture the model is sent and therefore the space its coordinates arrive in. They
        // are the same thing until the picture is capped, and `scale` is the factor between them.
        int sourceWidth = 0, sourceHeight = 0, width = 0, height = 0;
        double scale = 1;
        ComputerResult Rejected(string reason) => new(new("rejected", 0, reason, []), request.Target, width, height, null);
        try { await _computerActions.WaitAsync(input.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(new("paused", 0, "No current input lease.", []), request.Target, 0, 0, null); }
        try
        {
            WorkspaceBrowser? browser = request.Target == "browser" ? _browser : null;
            if (request.Target == "browser")
            {
                if (browser is null) return Rejected("No workspace browser is attached. Use browse first.");
                (sourceWidth, sourceHeight) = await browser.Viewport(input.Token).ConfigureAwait(false);
                if (sourceWidth <= 0 || sourceHeight <= 0) return Rejected("The browser viewport is unavailable.");
            }
            else
            {
                sourceWidth = Native.GetSystemMetrics(Native.SmCxScreen);
                sourceHeight = Native.GetSystemMetrics(Native.SmCyScreen);
            }
            // An action group is capped at 1280x720: measured 2,691 visual tokens for a 1920x1080
            // screen against 1,196 for the same screen at the cap, and Anthropic's computer-use
            // guidance recommends this size for desktop work rather than the full screen.
            (width, height, scale) = WorkspaceMarks.Fit(sourceWidth, sourceHeight,
                WorkspaceMarks.ActionWidth, WorkspaceMarks.ActionHeight);

            foreach (ComputerAction action in request.Actions)
            {
                if (WorkspaceComputer.Bounds(action, width, height) is { } why) return Rejected(why);
                if (request.Target == "desktop" && action.ScrollX != 0)
                    return Rejected("Native horizontal scrolling is not supported. Browser target supports both axes.");
                if (action.Control > 0 && _tree.Known(action.Control) is null)
                    return Rejected($"Control {action.Control} is not known. Number the controls again with controls or marks.");
            }
            string? missed = null;
            ComputerReceipt receipt = await WorkspaceComputer.Execute(request.Actions, ownsLease, async action =>
            {
                if (!ownsLease() || input.IsCancellationRequested) return false;
                if (action.Control > 0) return AtControl(action, ticket, ref missed);
                // Back into the coordinates the desktop and the browser use, immediately before the
                // action goes out - never in the receipt, which stays in the model's own space.
                ComputerAction placed = WorkspaceMarks.ToSource(action, scale);
                // Pin the browser instance for the whole group. Never redirect queued input to a replacement.
                bool sent = browser is not null
                    ? ReferenceEquals(browser, _browser) && await browser.ComputerInput(placed, input.Token).ConfigureAwait(false)
                    : _desktop.ComputerInput(placed, ticket);
                _evidence.Note("computer." + action.Type, request.Target, sent ? "input delivered; verify outcome" : "not confirmed; do not replay");
                return sent;
            }, input.Token).ConfigureAwait(false);
            if (missed is not null && receipt.Status == "interrupted")
                receipt = receipt with { Reason = missed + " " + receipt.Reason };

            BitmapSource? frame = null;
            if (request.Screenshot && ownsLease() && !input.IsCancellationRequested)
            {
                // An app is often busy for a beat right after it is clicked. Waiting that beat out
                // here is free; a picture of its last frame costs the agent another look.
                if (browser is null)
                    await Settle(0, input.Token, afterInput: receipt.Completed.Any(done => done is not ("wait" or "screenshot")))
                        .ConfigureAwait(false);
                // One evidence frame and one model image per group. Screenshots are optional for text-only work.
                try
                {
                    frame = browser is not null ? await browser.Screenshot(input.Token).ConfigureAwait(false) : Screen();
                    // Down to the size it is sent at before the marks go on, not after. Drawing the
                    // boxes builds a fresh bitmap the size of whatever it is drawing on, so marking
                    // a whole 1920x1080 screen and shrinking it afterwards pays for a full-screen
                    // render target nothing ever sees: measured 62-67 ms against 35-42 ms for the
                    // same three-window desktop, and the numbers come out legible instead of
                    // smeared, because the badge is no longer drawn at full size and then shrunk.
                    if (frame is not null) frame = WorkspaceMarks.Resize(frame, width, height);
                    if (frame is not null && request.Marks && browser is null)
                    {
                        (IReadOnlyList<WorkspaceElement> found, System.Windows.Point origin) = MarksFor(0);
                        // The same factor `ToSource` maps the model's coordinates back through, so a
                        // box sits exactly where a press on it will land.
                        frame = WorkspaceMarks.Draw(frame, found, origin, scale);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { receipt = receipt with { Reason = receipt.Reason + " Screenshot unavailable: " + ex.GetType().Name + ". Do not replay input." }; }
            }
            if (request.Screenshot && frame is null)
                receipt = receipt with { Reason = receipt.Reason + " No image returned. Observe again before further coordinate input." };
            _evidence.Note("computer", request.Target, $"{receipt.Status}; {receipt.Next} input actions; {receipt.Reason}", frame);
            return new(receipt, request.Target, frame?.PixelWidth ?? width, frame?.PixelHeight ?? height, frame);
        }
        finally { _computerActions.Release(); }
    }

    /// <summary>
    /// A pointer action aimed at a numbered control, at wherever that control is now. A plain left
    /// click on a WPF control presses that exact control through the tree: WPF cannot be clicked by
    /// message on a workspace desktop (see AgentDesktop.Click), and pressing by point there resolves
    /// whatever sits at the centre - a label inside a button, an icon inside a list item - rather
    /// than the control the number names. Anything the tree cannot press, and every other kind of
    /// pointer action, goes to the control's centre the ordinary way.
    /// </summary>
    bool AtControl(ComputerAction action, long ticket, ref string? missed)
    {
        (int x, int y, bool wpf, string? why) = _tree.Where(action.Control);
        if (why is not null)
        {
            missed = why;
            _evidence.Note("computer." + action.Type, "control " + action.Control, "refused; " + why);
            return false;
        }
        if (wpf && action is { Type: "click", Button: "left" })
        {
            WorkspaceTree.TreeAction pressed = _tree.TryPress(action.Control, () => Ticket == ticket);
            if (pressed == WorkspaceTree.TreeAction.Applied)
            {
                _evidence.Note("computer.click", "control " + action.Control, "pressed through the tree; verify outcome");
                return true;
            }
            if (pressed == WorkspaceTree.TreeAction.Refused)
            {
                missed = $"Control {action.Control} changed before it could be pressed. Number the controls again.";
                _evidence.Note("computer.click", "control " + action.Control, "refused; " + missed);
                return false;
            }
        }
        bool sent = _desktop.ComputerInput(action with { X = x, Y = y }, ticket);
        _evidence.Note("computer." + action.Type, $"control {action.Control} at ({x},{y})",
            sent ? "input delivered; verify outcome" : "not confirmed; do not replay");
        return sent;
    }
}
