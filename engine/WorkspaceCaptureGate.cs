namespace Deskweave.AgentWorkspaces;

/// <summary>Viewers share the actual queued capture, even after an earlier caller stops waiting.
/// A caller's timeout never releases the flight; only completion of the capture does.</summary>
internal sealed class WorkspaceCaptureGate
{
    readonly object _gate = new();
    TaskCompletionSource<DesktopFrame>? _pending;
    int _width, _height;

    internal DesktopFrame Take(int width, int height, TimeSpan bound, Func<Task<DesktopFrame>> begin)
    {
        TaskCompletionSource<DesktopFrame> pending;
        bool capture;
        lock (_gate)
        {
            capture = _pending is null;
            if (capture)
            {
                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                (_width, _height) = (width, height);
            }
            else if (width != _width || height != _height) return new(null, 0, 0, true);
            pending = _pending!;
        }
        if (capture)
        {
            // Enqueue outside the lock. The reserved flight already covers callers arriving while
            // submission is in progress, and no caller has to wait for the desktop pump here.
            try
            {
                _ = begin().ContinueWith(finished => Complete(pending, finished), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception error) { Complete(pending, Task.FromException<DesktopFrame>(error)); }
        }
        try { return pending.Task.Wait(bound) ? pending.Task.GetAwaiter().GetResult() : new(null, 0, 0, true); }
        catch (AggregateException) { return pending.Task.GetAwaiter().GetResult(); }
    }

    void Complete(TaskCompletionSource<DesktopFrame> pending, Task<DesktopFrame> finished)
    {
        lock (_gate)
        {
            if (finished.IsCanceled) pending.TrySetCanceled();
            else if (finished.Exception is { } error)
            {
                pending.TrySetException(error.InnerExceptions);
                // Every caller may already have timed out. Observe the fault without changing
                // what a caller still waiting receives, or leaving an unobserved worker failure.
                _ = pending.Task.Exception;
            }
            else pending.TrySetResult(finished.Result);
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }
}
