namespace HiveMind.AgentWorkspaces;

/// <summary>Concurrent viewers share one bounded capture instead of competing for the first frame.
/// There is no capture queue, background timer or retained image here.</summary>
internal sealed class WorkspaceCaptureGate
{
    readonly object _gate = new();
    TaskCompletionSource<DesktopFrame>? _pending;
    int _width, _height;

    internal DesktopFrame Take(int width, int height, TimeSpan bound, Func<DesktopFrame> draw)
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
        if (!capture)
            return pending.Task.Wait(bound) ? pending.Task.Result : new(null, 0, 0, true);
        DesktopFrame result = new(null, 0, 0, true);
        try { return result = draw(); }
        finally
        {
            lock (_gate)
            {
                pending.TrySetResult(result);
                _pending = null;
            }
        }
    }
}
