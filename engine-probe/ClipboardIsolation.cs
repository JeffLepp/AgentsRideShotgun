using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// The clipboard broker's owner side. A copy that names no window (OpenClipboard(NULL), as some
/// owner apps and scripts do) must stay in the owner's clipboard and never be filed under a
/// workspace, and a paste whose workspace clipboard could not be loaded must not run at all.
/// The owner's real clipboard is saved first and put back afterwards.
///
/// Run: Deskweave.Probe.exe --clipboard-isolation "C:\absolute\output"
/// </summary>
internal static class ClipboardIsolation
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var claims = new List<string>();
        string? failure = null;
        List<(uint, byte[])>? saved = Save();
        string workspace = "clip-" + Guid.NewGuid().ToString("N")[..8];
        ClipboardBroker broker = ClipboardBroker.Shared;
        try
        {
            if (saved is null) throw new InvalidOperationException("Could not read the owner's clipboard to save it.");
            broker.Register(workspace);

            // An anonymous copy while exactly one workspace runs: the old guess filed it there.
            string marker = "deskweave-owner-anon-" + Guid.NewGuid().ToString("N")[..8];
            Check(WriteText(marker), "Wrote an anonymous copy to the clipboard");
            Thread.Sleep(700);
            Check(broker.TextOf(workspace) != marker, "An anonymous copy is not filed under the running workspace");
            Check(broker.OwnerText() == marker, "An anonymous copy is the owner's own");
            Check(ReadText() == marker, "An anonymous copy stays in the owner's real clipboard");

            // Somebody else holding the clipboard for longer than the broker retries: the swap
            // cannot happen, so the paste must not run with the owner's clipboard still loaded.
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                bool open = OpenClipboard(0);
                held.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                if (open) CloseClipboard();
            });
            holder.Start();
            held.Wait();
            bool ran = false;
            string? error = null;
            try { broker.WithClipboardOf(workspace, () => ran = true); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            release.Set();
            holder.Join();
            Check(!ran && error is not null, "A paste whose workspace clipboard could not be loaded does not run: " + error);
            Check(ReadText() == marker, "The owner's clipboard is untouched by the refused paste");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            broker.Unregister(workspace);
            if (saved is not null && !Put(saved)) failure ??= "Could not put the owner's clipboard back.";
            Thread.Sleep(400);
        }
        File.WriteAllText(Path.Combine(output, "clipboard-isolation.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), claims, failure, ownerFormatsRestored = saved?.Count,
            globalInputEventsSent = 0, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException(claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    static bool Open()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    // Formats whose data is a GDI or metafile handle rather than a memory block. Not carried, and
    // not produced by the text an owner usually has; skipping them keeps the save a plain copy.
    static bool HandleFormat(uint format) =>
        format is 2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8E || format is >= 0x300 and <= 0x3FF;

    static List<(uint, byte[])>? Save()
    {
        if (!Open()) return null;
        try
        {
            var formats = new List<(uint, byte[])>();
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (HandleFormat(format)) continue;
                nint handle = GetClipboardData(format);
                if (handle == 0) continue;
                nuint size = GlobalSize(handle);
                nint memory = size == 0 ? 0 : GlobalLock(handle);
                if (memory == 0) continue;
                byte[] bytes = new byte[(int)size];
                Marshal.Copy(memory, bytes, 0, bytes.Length);
                GlobalUnlock(handle);
                formats.Add((format, bytes));
            }
            return formats;
        }
        finally { CloseClipboard(); }
    }

    static bool Put(List<(uint Format, byte[] Bytes)> formats)
    {
        if (!Open()) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            foreach ((uint format, byte[] bytes) in formats)
            {
                nint block = GlobalAlloc(0x0002, (nuint)Math.Max(bytes.Length, 1));
                if (block == 0) continue;
                nint memory = GlobalLock(block);
                if (memory == 0) { GlobalFree(block); continue; }
                Marshal.Copy(bytes, 0, memory, bytes.Length);
                GlobalUnlock(block);
                if (SetClipboardData(format, block) == 0) GlobalFree(block);
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    static bool WriteText(string text) => Put([(13, Encoding.Unicode.GetBytes(text + "\0"))]);

    static string? ReadText()
    {
        List<(uint Format, byte[] Bytes)>? formats = Save();
        byte[]? bytes = formats?.FirstOrDefault(x => x.Format == 13).Bytes;
        if (bytes is null) return null;
        string text = Encoding.Unicode.GetString(bytes);
        int end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }
}
