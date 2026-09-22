using System.Diagnostics;
using System.IO;
using System.Text;

namespace Deskweave.AgentWorkspaces;

/// <summary>What a command job is doing. The four states the owner asked to be able to tell apart.</summary>
public enum CommandState { Running, Completed, Failed, Cancelled }

/// <summary>
/// One command started in a workspace. It outlives the tool call that started it: the tool's
/// response wait is how long that call is willing to block, never how long the command may run.
/// </summary>
public sealed class CommandJob
{
    internal CommandJob(string id, string command, string shell, double limitSeconds)
    {
        Id = id;
        Command = command;
        Shell = shell;
        LimitSeconds = limitSeconds > 0 ? limitSeconds : null;
        Started = DateTimeOffset.Now;
    }

    public string Id { get; }

    /// <summary>The script as the agent wrote it, kept whole for status and the log.</summary>
    public string Command { get; }

    public string Shell { get; }

    /// <summary>The explicit per-command limit, or null when the command may run as long as it needs.</summary>
    public double? LimitSeconds { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset? Ended { get; internal set; }

    public int Pid { get; internal set; }

    public CommandState State { get; internal set; } = CommandState.Running;

    /// <summary>The process's actual exit code once it has one. Null while it is still running.</summary>
    public int? ExitCode { get; internal set; }

    /// <summary>Why it ended the way it did, in one line. Empty for an ordinary success.</summary>
    public string Reason { get; internal set; } = string.Empty;

    /// <summary>True once output was dropped from the front of the retained window.</summary>
    public bool Trimmed { get; internal set; }

    public bool Running => State == CommandState.Running;

    public double Seconds => ((Ended ?? DateTimeOffset.Now) - Started).TotalSeconds;

    /// <summary>Where the retained output actually lives, for the probe and the tests.</summary>
    internal string OutputPath => Output;

    internal string Output = string.Empty;
    internal string[] Scripts = [];
    internal Process? Process;
    internal readonly TaskCompletionSource<bool> Finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Where the retained window starts, as an absolute byte offset into everything printed.</summary>
    internal long Dropped;

    public string Status => State switch
    {
        CommandState.Running => "running",
        CommandState.Completed => "completed",
        CommandState.Failed => "failed",
        _ => "cancelled",
    };
}

/// <summary>One page of a job's retained output, with the offsets needed to ask for the next one.</summary>
public sealed record CommandOutput(string Text, long Offset, long Next, long Oldest, long Total)
{
    public bool More => Next < Total;
}

/// <summary>
/// The workspace's command jobs. Before this, `run` deleted its script and output in a `finally`
/// the moment its wait expired, so a command that was still working lost the file it was reading
/// and the agent could never learn what it did or what it exited with. A job now survives the tool
/// call, the connection and the model's turn; only the workspace closing ends it.
///
/// What is bounded here is retention and disk, never runtime: there is no implicit execution
/// ceiling, only the limit a caller asks for and the owner's or agent's explicit cancellation.
/// </summary>
public sealed class WorkspaceCommands : IDisposable
{
    /// <summary>Retained output per job. Older output is dropped from the front, never the newest.</summary>
    internal const long RetainBytes = 1L << 20;

    /// <summary>
    /// A single command may not fill the disk. This is a resource bound, not a runtime one: a
    /// command printing this much has run away, and the agent is told exactly why it stopped.
    /// </summary>
    internal const long OutputCapBytes = 128L << 20;

    /// <summary>Finished jobs kept for status. Older ones are reaped with their output.</summary>
    const int KeepFinished = 20;

    const int TickMilliseconds = 2000;

    readonly WorkspaceControl _control;
    readonly Lock _gate = new();
    readonly List<CommandJob> _jobs = [];
    readonly CancellationTokenSource _stop = new();
    bool _disposed;
    long _lastActive;

    public WorkspaceCommands(WorkspaceControl control)
    {
        _control = control;
        // A Deskweave that stopped without closing its workspaces leaves job files behind, and a
        // workspace is meant to be reusable for months. Sweep them on the way in, when nothing can
        // still be holding them, rather than accumulating a folder of dead scripts.
        Sweep(control.Folder);
    }

    /// <summary>
    /// Removes job scripts and retained output left in a workspace folder. Safe only when no job
    /// from that session is still running, which is why it is called on the way in and after a
    /// workspace's desktop has been torn down, never in the middle of a session.
    /// </summary>
    internal static void Sweep(string folder)
    {
        if (folder.Length == 0) return;
        try
        {
            foreach (string pattern in new[] { "job-*.cmd", "job-*.ps1", "job-*.txt" })
                foreach (string leftover in Directory.EnumerateFiles(folder, pattern))
                    Delete(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    /// <summary>Every job this workspace has run in this session, oldest first.</summary>
    public IReadOnlyList<CommandJob> Jobs { get { lock (_gate) return [.. _jobs]; } }

    // A command outlives the agent's lease. Idle cleanup must retain it until it actually ends.
    internal (bool Running, long LastActive) Activity
    {
        get { lock (_gate) return (_jobs.Any(job => job.Ended is null), _lastActive); }
    }

    public CommandJob? Find(string id)
    {
        if (id.Trim().Length == 0) return null;
        lock (_gate) return _jobs.FirstOrDefault(j => string.Equals(j.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts a command as a tracked job. Returns null with a reason when the workspace would not
    /// start it at all; a command that starts and then fails is a job with an exit code, not a null.
    /// </summary>
    public CommandJob? Start(string command, bool powershell, double limitSeconds, out string? error, string home = "")
    {
        error = null;
        if (command.Trim().Length == 0) { error = "no command given"; return null; }
        if (_disposed) { error = "this workspace is closing"; return null; }
        string folder = _control.Folder;
        if (folder.Length == 0) { error = "this workspace has no folder to work in"; return null; }

        var job = new CommandJob("job-" + Guid.NewGuid().ToString("N")[..8], command,
            powershell ? "powershell" : "cmd", limitSeconds);
        string stem = Path.Combine(folder, job.Id);
        string script = stem + ".cmd";
        string output = stem + ".txt";
        string? scriptPs = powershell ? stem + ".ps1" : null;
        job.Output = output;
        job.Scripts = scriptPs is null ? [script] : [script, scriptPs];

        try
        {
            // A PowerShell script goes into its own file and the batch file merely starts it. Measured
            // 2026-09-06: an agent asked to embed a PowerShell one-liner in a cmd line lost a turn to
            // every level of quoting, then gave up and typed the script into Notepad instead.
            if (scriptPs is not null)
                File.WriteAllText(scriptPs, Crlf(command) + "\r\n");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                // UTF-8 for the whole job, so a build's output is not mangled on the way to the agent.
                "chcp 65001>nul",
                "cd /d " + Quote(home.Length > 0 ? home : folder),
                scriptPs is null ? command
                    : "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(scriptPs),
                string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "could not write the script: " + ex.Message;
            Clean(job);
            return null;
        }

        // Disable Command Processor AutoRun and quote the whole /s command expression. Capture the
        // shell as well as the script, so a failure before the script starts is still readable.
        // < NUL is the one thing keeping a command that asks a question from waiting for an answer
        // forever now that nothing kills it on a timer.
        int pid = _control.Open("cmd.exe",
            "/d /s /c \"" + Quote(script) + " > " + Quote(output) + " 2>&1 < NUL\"", quiet: true);
        if (pid == 0)
        {
            Clean(job);
            error = "the workspace would not start a command shell";
            return null;
        }
        job.Pid = pid;

        // The handle is opened immediately and held for the life of the job. That is what makes the
        // exit code real: Windows keeps the process record, and its PID cannot be reused underneath.
        try { job.Process = Process.GetProcessById(pid); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It exited between CreateProcess and here. Without a handle there is no exit code to
            // report, and saying nothing would be worse than saying that plainly.
            job.State = CommandState.Failed;
            job.Reason = "the command shell exited before its exit code could be read";
            job.Ended = DateTimeOffset.Now;
            job.Finished.TrySetResult(true);
        }

        lock (_gate)
        {
            _jobs.Add(job);
            _lastActive = Environment.TickCount64;
            Reap();
        }
        _control.Evidence.Note("run", job.Id + " " + Short(command), "started, pid " + pid
            + (job.LimitSeconds is { } limit ? $", limit {limit:F0} s" : ", no runtime limit"));
        if (job.Process is not null) _ = Task.Run(() => Watch(job));
        return job;
    }

    /// <summary>
    /// Waits for one job for as long as this tool call is willing to block. False means it is still
    /// running, which is an outcome rather than a failure.
    /// </summary>
    public async Task<bool> Wait(CommandJob job, TimeSpan wait, CancellationToken cancel)
    {
        if (job.Finished.Task.IsCompleted) return true;
        if (wait <= TimeSpan.Zero) return false;
        using var timer = new CancellationTokenSource(wait);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancel);
        Task delay = Task.Delay(Timeout.InfiniteTimeSpan, both.Token);
        await Task.WhenAny(job.Finished.Task, delay).ConfigureAwait(false);
        return job.Finished.Task.IsCompleted;
    }

    /// <summary>Cancels one running job and everything it started. Deliberate, never automatic.</summary>
    public bool Cancel(string id, string who, out string? error)
    {
        error = null;
        CommandJob? job = Find(id);
        if (job is null) { error = "no such job: " + id; return false; }
        if (!job.Running) { error = "that job already " + job.Status; return false; }
        Stop(job, CommandState.Cancelled, "cancelled by " + who);
        return true;
    }

    /// <summary>
    /// A page of retained output. Offsets are absolute over everything the command printed, so an
    /// agent that has read to <see cref="CommandOutput.Next"/> can ask again later and get only the
    /// new text, and one that reads a trimmed job is told where the retained window actually starts.
    /// </summary>
    public CommandOutput Read(CommandJob job, long offset, int max)
    {
        if (max <= 0) max = 8000;
        long dropped = job.Dropped;
        long total = dropped;
        try
        {
            if (!File.Exists(job.Output)) return new CommandOutput(string.Empty, dropped, dropped, dropped, dropped);
            using var file = new FileStream(job.Output, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            total = dropped + file.Length;
            long from = Math.Clamp(offset <= 0 ? dropped : offset, dropped, total);
            file.Seek(from - dropped, SeekOrigin.Begin);
            byte[] buffer = new byte[(int)Math.Min(max, total - from)];
            int read = file.Read(buffer, 0, buffer.Length);
            return new CommandOutput(new UTF8Encoding(false).GetString(buffer, 0, read), from, from + read, dropped, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CommandOutput(string.Empty, dropped, dropped, dropped, total);
        }
    }

    // --- the watcher -----------------------------------------------------------------------------

    /// <summary>
    /// One task per running job. It is what turns a launched process into a job: it holds the
    /// handle, applies an explicit limit if one was asked for, keeps disk bounded, and records the
    /// real exit code the moment there is one.
    /// </summary>
    async Task Watch(CommandJob job)
    {
        Process process = job.Process!;
        long deadline = job.LimitSeconds is { } limit
            ? Environment.TickCount64 + (long)(limit * 1000) : long.MaxValue;
        try
        {
            Task exited = process.WaitForExitAsync(_stop.Token);
            while (!exited.IsCompleted)
            {
                Task tick = Task.Delay(TickMilliseconds, _stop.Token);
                if (await Task.WhenAny(exited, tick).ConfigureAwait(false) == exited) break;
                if (Environment.TickCount64 >= deadline)
                {
                    Stop(job, CommandState.Failed, $"the {job.LimitSeconds:F0} s limit this command was given expired");
                    break;
                }
                if (Size(job.Output) > OutputCapBytes)
                {
                    Stop(job, CommandState.Failed,
                        $"stopped after printing more than {OutputCapBytes >> 20} MB; a command producing that much output has run away");
                    break;
                }
            }
            await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The workspace is closing. Dispose has already written the outcome down.
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Settle(job, null, CommandState.Failed, "the command shell could not be watched: " + ex.Message);
            return;
        }

        int? code = null;
        try { code = process.ExitCode; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Settle(job, code, code == 0 ? CommandState.Completed : CommandState.Failed,
            code is null ? "the exit code could not be read" : code == 0 ? string.Empty : "exit code " + code);
    }

    /// <summary>
    /// Writes the final outcome once. A job stopped on purpose keeps the reason it was stopped for;
    /// the exit code Windows gives a killed process is recorded but does not rewrite that story.
    /// </summary>
    void Settle(CommandJob job, int? code, CommandState state, string reason)
    {
        lock (_gate)
        {
            if (job.Ended is not null) return;
            // Only a command that ended on its own has an exit code worth reporting. The value
            // Windows hands back for a process we killed is the kill, not the program's answer, and
            // an agent reading "cancelled, exit code -1" reasonably concludes the command failed.
            if (job.Running) { job.State = state; job.Reason = reason; job.ExitCode = code; }
            job.Ended = DateTimeOffset.Now;
            _lastActive = Environment.TickCount64;
        }
        Trim(job);
        foreach (string leftover in job.Scripts) Delete(leftover);
        job.Process?.Dispose();
        job.Process = null;
        job.Finished.TrySetResult(true);
        _control.Evidence.Note("run", job.Id, job.Status
            + (job.ExitCode is { } actual ? ", exit code " + actual : "")
            + (job.Reason.Length > 0 && job.Reason != "exit code " + job.ExitCode ? ", " + job.Reason : "")
            + $", {job.Seconds:F1} s");
        lock (_gate) Reap();
    }

    /// <summary>Kills the shell and everything it started, and records why before Windows answers.</summary>
    void Stop(CommandJob job, CommandState state, string reason)
    {
        lock (_gate)
        {
            if (!job.Running) return;
            job.State = state;
            job.Reason = reason;
        }
        try { job.Process?.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or AggregateException)
        {
            // Already gone, or a descendant Windows would not let us reach. The state above stands.
        }
        // A job whose watcher has already stopped, or that never had a handle, still has to settle.
        if (job.Process is null) Settle(job, job.ExitCode, state, reason);
    }

    /// <summary>
    /// Drops the oldest output once the file passes the retention window. Only ever done after the
    /// writer has exited, so nothing is racing the rewrite.
    /// </summary>
    void Trim(CommandJob job)
    {
        try
        {
            var file = new FileInfo(job.Output);
            if (!file.Exists || file.Length <= RetainBytes) return;
            long drop = file.Length - RetainBytes;
            byte[] keep = new byte[RetainBytes];
            using (var source = new FileStream(job.Output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                source.Seek(drop, SeekOrigin.Begin);
                int read = source.Read(keep, 0, keep.Length);
                if (read < keep.Length) Array.Resize(ref keep, read);
            }
            File.WriteAllBytes(job.Output, keep);
            job.Dropped = drop;
            job.Trimmed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Keeps the newest finished jobs and deletes the output of the ones falling off.</summary>
    void Reap()
    {
        int finished = _jobs.Count(j => j.Ended is not null);
        for (int i = 0; i < _jobs.Count && finished > KeepFinished; i++)
        {
            if (_jobs[i].Ended is null) continue;
            Clean(_jobs[i]);
            _jobs.RemoveAt(i--);
            finished--;
        }
    }

    void Clean(CommandJob job)
    {
        foreach (string leftover in job.Scripts) Delete(leftover);
        Delete(job.Output);
    }

    /// <summary>
    /// Deletes a job file, allowing for the moment after a kill when a child that inherited the
    /// redirected handle has been signalled but has not finished releasing it yet. Without the
    /// retry a cancelled job reliably left its output behind in the workspace folder.
    /// </summary>
    static void Delete(string path)
    {
        if (path.Length == 0) return;
        for (int attempt = 0; ; attempt++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 19) return;
                Thread.Sleep(100);
            }
        }
    }

    static long Size(string path)
    {
        try { var file = new FileInfo(path); return file.Exists ? file.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    static string Quote(string path) => "\"" + path + "\"";

    /// <summary>
    /// A long command cut in the middle, not at the end: what it runs is usually last, after a
    /// `cd /d "..."` into a long folder, and two jobs cut at 120 characters read the same
    /// (2026-09-22: "python groceries.py" was lost off the end of both listed jobs).
    /// </summary>
    internal static string Short(string command)
    {
        string one = Crlf(command).Replace("\r\n", " ; ").Trim();
        return one.Length > 120 ? one[..40] + " ... " + one[^75..] : one;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (CommandJob job in Jobs)
        {
            if (job.Running)
            {
                // The desktop's job object is about to kill these anyway. Say so before it does, so
                // the record never shows a command that simply stopped being mentioned.
                lock (_gate) { job.State = CommandState.Cancelled; job.Reason = "the workspace closed"; job.Ended = DateTimeOffset.Now; }
                try
                {
                    job.Process?.Kill(entireProcessTree: true);
                    // Wait for the shell to actually go before deleting under it. Kill only signals,
                    // and its redirected output file is still open until the last child has exited.
                    job.Process?.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                    or System.ComponentModel.Win32Exception or AggregateException) { }
                job.Finished.TrySetResult(true);
            }
            job.Process?.Dispose();
            job.Process = null;
            Clean(job);
        }
        _stop.Cancel();
        _stop.Dispose();
    }
}
