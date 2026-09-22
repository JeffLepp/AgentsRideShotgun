using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

// Native WinExe children on alternate desktops only; no browser/model or owner window is needed.
internal static class HandleInheritance
{
    internal static int RunStandalone(string output)
    {
        Directory.CreateDirectory(output);
        using var store = WorkspaceStore.UseRootForTests(Path.Combine(output, "store"));
        using var settings = AppSettingsStore.UseFileForTests(Path.Combine(output, "settings.json"));
        try { Run((passed, claim) => { if (!passed) throw new InvalidOperationException(claim); }, output); return 0; }
        catch { return 1; } // Run preserves the exception and partial receipts in its report.
    }

    internal static void Run(Action<bool, string> check, string output)
    {
        string folder = Path.Combine(output, "handle-inheritance");
        Directory.CreateDirectory(folder);
        var claims = new List<string>();
        var receipts = new List<JsonElement>();
        var processes = new List<Process>();
        string? failure = null;
        string nonce = Guid.NewGuid().ToString("N")[..8];
        AgentDesktop? first = null, second = null;
        using var timeout = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(folder, "timeout.txt"), "Handle inheritance fixture exceeded 60 seconds.");
            Environment.Exit(2); // Only this fixture's job handles close.
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        var elapsed = Stopwatch.StartNew();
        try
        {
            first = AgentDesktop.Create("HandlesA-" + nonce);
            second = AgentDesktop.Create("HandlesB-" + nonce);
            using var a = new Pipes();
            using var b = new Pipes();
            using var decoy = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            nint decoyHandle = decoy.ClientSafePipeHandle.DangerousGetHandle();
            nint[] all = [a.In, a.Out, b.In, b.Out, decoyHandle];
            Claim(all.Distinct().Count() == 5 && all.All(h => ReadHandle(h).Valid && (ReadHandle(h).Flags & 1) != 0),
                "Both launch pairs and the unrelated decoy are simultaneously real inheritable handles");
            string aFile = Path.Combine(folder, "a.json"), bFile = Path.Combine(folder, "b.json");
            string aStop = aFile + ".stop", bStop = bFile + ".stop";
            using var ready = new Barrier(2);
            Task<int> firstLaunch = Task.Run(() =>
            {
                if (!ready.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("First launch barrier");
                return first.Launch(Environment.ProcessPath!, Arguments(aFile, aStop, "stdio", a.In, a.Out,
                    b.In, b.Out, decoyHandle, 37), (a.In, a.Out));
            });
            Task<int> secondLaunch = Task.Run(() =>
            {
                if (!ready.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Second launch barrier");
                return second.LaunchBrowser(Environment.ProcessPath!, Arguments(bFile, bStop, "raw", b.In, b.Out,
                    a.In, a.Out, decoyHandle, 67), (b.In, b.Out));
            });
            // Keep every inheritable client end open until BOTH CreateProcess calls return.
            // Even if Windows serializes scheduling, neither launch can miss the other pair.
            if (!Task.WaitAll([firstLaunch, secondLaunch], TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Overlapping native launches did not finish within 15 seconds.");
            Claim(firstLaunch.Result > 0 && secondLaunch.Result > 0,
                "Overlapping standard-stream and browser-style launches both succeed");
            Process pa = Process.GetProcessById(firstLaunch.Result), pb = Process.GetProcessById(secondLaunch.Result);
            _ = pa.Handle; _ = pb.Handle;
            processes.Add(pa); processes.Add(pb);
            a.CloseClientCopies(); b.CloseClientCopies();
            decoy.DisposeLocalCopyOfClientHandle();
            JsonElement ra = Receipt(aFile), rb = Receipt(bFile);
            receipts.Add(ra); receipts.Add(rb);
            Claim(first.OwnsProcess(pa.Id) && !second.OwnsProcess(pa.Id)
                && second.OwnsProcess(pb.Id) && !first.OwnsProcess(pb.Id),
                "Each exact child belongs only to its intended workspace job");
            Claim(ra.GetProperty("Desktop").GetString() == first.Name && rb.GetProperty("Desktop").GetString() == second.Name,
                "Both children run on their intended native desktops");
            Claim(OwnPipes(ra) && OwnPipes(rb), "Each child inherits both exact requested pipe handles");
            Claim(NoForeignPipes(ra) && NoForeignPipes(rb),
                "Neither child inherits the other launch's handles or the unrelated decoy");
            Claim(ra.GetProperty("StandardInput").GetInt64() == a.In.ToInt64()
                && ra.GetProperty("StandardOutput").GetInt64() == a.Out.ToInt64()
                && ra.GetProperty("StandardError").GetInt64() == a.Out.ToInt64()
                && (ra.GetProperty("StartupFlags").GetInt32() & 0x100) != 0
                && (rb.GetProperty("StartupFlags").GetInt32() & 0x100) == 0,
                "Standard handles remain mapped for ordinary Launch and separate from browser raw pipes");
            Claim(ReadByte(decoy) == -1 && !pa.HasExited && !pb.HasExited,
                "Closing the parent's decoy writer produces EOF while both launched children remain alive");
            a.ToChild.WriteByte(37); b.ToChild.WriteByte(67);
            Claim(ReadByte(a.FromChild) == 38 && ReadByte(b.FromChild) == 68,
                "Both explicitly inherited pipe pairs carry their own request and response");
            File.WriteAllText(aStop, "stop exact first fixture");
            Claim(pa.WaitForExit(5000) && pa.ExitCode == 0 && !pb.HasExited && ReadByte(a.FromChild) == -1,
                "First child's pipe reaches EOF after its exit while the other child remains alive");
            File.WriteAllText(bStop, "stop exact second fixture");
            Claim(pb.WaitForExit(5000) && pb.ExitCode == 0, "The second owned child exits cleanly");

            // General callers may intentionally use one duplex handle for both standard slots.
            // A one-way fixture writer is enough to prove aliasing is preserved without a duplicate list entry.
            using var alias = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            nint shared = alias.ClientSafePipeHandle.DangerousGetHandle();
            string aliasFile = Path.Combine(folder, "alias.json"), aliasStop = aliasFile + ".stop";
            int aliasPid = first.Launch(Environment.ProcessPath!, Arguments(aliasFile, aliasStop, "alias",
                shared, shared, 0, 0, 0, 91), (shared, shared));
            Claim(aliasPid > 0, "An aliased valid In/Out handle is deduplicated rather than rejected");
            using Process aliasProcess = Process.GetProcessById(aliasPid);
            _ = aliasProcess.Handle;
            alias.DisposeLocalCopyOfClientHandle();
            JsonElement same = Receipt(aliasFile); receipts.Add(same);
            Claim(same.GetProperty("StandardInput").GetInt64() == shared.ToInt64()
                && same.GetProperty("StandardOutput").GetInt64() == shared.ToInt64() && ReadByte(alias) == 92,
                "Aliased standard handles retain the exact native value and carry data");
            File.WriteAllText(aliasStop, "stop exact alias fixture");
            Claim(aliasProcess.WaitForExit(5000) && aliasProcess.ExitCode == 0, "The aliased-handle child exits cleanly");

            string invalidFile = Path.Combine(folder, "invalid.json");
            string invalidArgs = Arguments(invalidFile, invalidFile + ".stop", "alias", 0, 0, 0, 0, 0, 1);
            Claim(first.Launch(Environment.ProcessPath!, invalidArgs, (0, a.ToChild.SafePipeHandle.DangerousGetHandle())) == 0,
                "An invalid requested handle fails closed before process creation");
            Claim(first.LaunchBrowser(Environment.ProcessPath!, invalidArgs,
                (a.ToChild.SafePipeHandle.DangerousGetHandle(), b.ToChild.SafePipeHandle.DangerousGetHandle())) == 0
                && !File.Exists(invalidFile), "Non-inheritable requested handles fail closed without broad inheritance fallback");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            foreach (AgentDesktop? desktop in new[] { first, second })
                try { desktop?.Dispose(); }
                catch (Exception ex) { failure ??= "Cleanup: " + ex; }
            foreach (Process process in processes) process.Dispose();
            string engine = typeof(AgentDesktop).Assembly.Location;
            File.WriteAllText(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new
            {
                status = failure is null ? "passed" : "failed", claims, failure, receipts,
                engineSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine))),
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, os = Environment.OSVersion.ToString(),
                modelCalls = 0, ownerWindowsCreated = 0, globalInputEventsSent = 0
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure is not null) throw new InvalidOperationException("Handle inheritance regression: " + failure);

        void Claim(bool passed, string message)
        {
            check(passed, message);
            if (!passed) throw new InvalidOperationException(message);
            claims.Add(message);
        }
    }

    internal static int Child(string[] args)
    {
        string file = args[1], release = args[2], kind = args[3];
        nint input = (nint)long.Parse(args[4], CultureInfo.InvariantCulture);
        nint output = (nint)long.Parse(args[5], CultureInfo.InvariantCulture);
        // Inspect numeric handles before opening any files that could reuse excluded values.
        HandleFact[] own = [ReadHandle(input), ReadHandle(output)];
        HandleFact[] foreign = args.Skip(6).Take(3).Select(value => ReadHandle((nint)long.Parse(value, CultureInfo.InvariantCulture))).ToArray();
        int marker = int.Parse(args[9], CultureInfo.InvariantCulture);
        using var timeout = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        try
        {
            GetStartupInfoW(out RawStartupInfo startup);
            var desktop = new StringBuilder(256);
            if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktop, desktop.Capacity * 2, out _))
                throw new IOException("Cannot read child desktop.");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId, Desktop = desktop.ToString(), Own = own, Foreign = foreign,
                StandardInput = GetStdHandle(-10).ToInt64(), StandardOutput = GetStdHandle(-11).ToInt64(),
                StandardError = GetStdHandle(-12).ToInt64(), StartupFlags = startup.dwFlags
            }));
            byte[] one = [(byte)marker];
            if (kind != "alias" && (!ReadFile(input, one, 1, out uint read, 0) || read != 1 || one[0] != marker))
                throw new IOException("Fixture received the wrong pipe request.");
            one[0]++;
            if (!WriteFile(output, one, 1, out uint written, 0) || written != 1)
                throw new IOException("Fixture could not return its pipe response.");
            long until = Environment.TickCount64 + 20000;
            while (!File.Exists(release) && Environment.TickCount64 < until) Thread.Sleep(25);
            return File.Exists(release) ? 0 : 4;
        }
        catch (Exception ex) { File.WriteAllText(file + ".error", ex.ToString()); return 1; }
    }

    static string Arguments(string receipt, string stop, string kind, nint input, nint output,
        nint otherInput, nint otherOutput, nint decoy, int marker) => "--inheritance-child \"" + receipt + "\" \"" + stop
        + "\" " + kind + " " + string.Join(" ", new long[] { input, output, otherInput, otherOutput, decoy, marker }
            .Select(value => value.ToString(CultureInfo.InvariantCulture)));
    static JsonElement Receipt(string path)
    {
        long until = Environment.TickCount64 + 5000;
        do
        {
            try { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.Clone(); }
            catch (Exception ex) when (ex is IOException or JsonException) { Thread.Sleep(25); }
        } while (Environment.TickCount64 < until);
        throw new TimeoutException("Missing complete child receipt: " + path);
    }
    static int ReadByte(Stream stream)
    {
        byte[] buffer = new byte[1];
        int count = stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        return count == 0 ? -1 : buffer[0];
    }
    static bool OwnPipes(JsonElement receipt) => receipt.GetProperty("Own").EnumerateArray().All(Pipe);
    static bool NoForeignPipes(JsonElement receipt) => receipt.GetProperty("Foreign").EnumerateArray().All(h => !Pipe(h));
    static bool Pipe(JsonElement handle) => handle.GetProperty("Valid").GetBoolean() && handle.GetProperty("Type").GetUInt32() == 3;
    static HandleFact ReadHandle(nint handle)
    {
        bool valid = Native.GetHandleInformation(handle, out uint flags);
        return new(handle.ToInt64(), valid, flags, GetFileType(handle));
    }
    sealed record HandleFact(long Value, bool Valid, uint Flags, uint Type);
    sealed class Pipes : IDisposable
    {
        internal readonly AnonymousPipeServerStream ToChild = new(PipeDirection.Out, HandleInheritability.Inheritable);
        internal readonly AnonymousPipeServerStream FromChild = new(PipeDirection.In, HandleInheritability.Inheritable);
        internal nint In { get; }
        internal nint Out { get; }
        internal Pipes()
        {
            In = ToChild.ClientSafePipeHandle.DangerousGetHandle();
            Out = FromChild.ClientSafePipeHandle.DangerousGetHandle();
        }
        internal void CloseClientCopies() { ToChild.DisposeLocalCopyOfClientHandle(); FromChild.DisposeLocalCopyOfClientHandle(); }
        public void Dispose() { ToChild.Dispose(); FromChild.Dispose(); }
    }
    [DllImport("kernel32.dll")] static extern uint GetFileType(nint handle);
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int standard);
    // GetStartupInfo returns borrowed string pointers. Keep them raw so the marshaller neither
    // copies nor frees process-owned memory when observing this native startup receipt.
    [StructLayout(LayoutKind.Sequential)]
    struct RawStartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern void GetStartupInfoW(out RawStartupInfo startup);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(nint handle, int index,
        StringBuilder text, int bytes, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(nint handle, byte[] bytes, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(nint handle, byte[] bytes, uint count, out uint written, nint overlapped);
}
