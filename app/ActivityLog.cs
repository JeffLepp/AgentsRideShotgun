using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

/// <summary>One "What it did" row. <paramref name="code"/> is the part shown in accent mono, such
/// as a command or the address a page was opened at. <paramref name="day"/> is set on the first row
/// of each day so the date reads once as a header and the time column stays short.
/// <paramref name="mark"/> is ✓ or ✗ for a finished command, <paramref name="note"/> its duration,
/// exit code or a ×N repeat count.</summary>
internal sealed class DidRow(string time, string prefix, string code, BitmapSource? thumb,
    string day = "", string mark = "", bool failed = false, string note = "")
{
    public string Time => time;
    public string Prefix => prefix;
    public string Code => code;
    public bool HasCode => code.Length > 0;
    public BitmapSource? Thumb => thumb;
    public bool HasThumb => thumb is not null;
    public string Day => day;
    public bool HasDay => day.Length > 0;
    public string Mark => mark;
    public bool Failed => failed;
    public string Note => note;
}

internal sealed class FileRow(string name, string when, string path = "")
{
    public string Name => name;
    public string When => when;
    public string Path => path;
}

/// <summary>Reads a workspace's evidence log and folder into rows a person can scan: the real
/// command instead of "Ran a command", start and finish as one row, repeats folded.</summary>
internal static class ActivityLog
{
    static readonly HashSet<string> QuietActions = new(StringComparer.Ordinal)
        { "workspace", "control", "policy", "elements", "marks", "wait", "ceiling", "window", "untrusted" };

    static readonly HashSet<string> MachineNames = new(StringComparer.OrdinalIgnoreCase)
        { "workspace.json", "last-frame.png", "last-frame.png.part" };

    sealed class Step
    {
        public DateTimeOffset When;
        public string Prefix = "", Code = "", Mark = "", Note = "";
        public bool Failed;
        public int Repeat = 1;
        public string Frame = "";
    }

    /// <summary>The evidence log, whose size says whether anything new was written.</summary>
    public static string LogPath(string id) => Path.Combine(WorkspaceStore.FolderOf(id), "evidence", "actions.log");

    /// <summary>The newest <paramref name="max"/> steps, oldest first. Safe off the UI thread.
    /// <paramref name="thumbs"/> decodes the kept rows' screenshots; the stack's card shows none.</summary>
    public static List<DidRow> Read(string id, int max = 40, bool thumbs = true)
    {
        string evidence = Path.Combine(WorkspaceStore.FolderOf(id), "evidence");
        string log = LogPath(id);
        string[] lines;
        try { lines = File.Exists(log) ? File.ReadAllLines(log) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        // Far enough back that a long command started among many quiet steps still finds its start.
        return Parse(lines.TakeLast(4000), thumbs ? frame => LoadImage(Path.Combine(evidence, frame)) : null, max);
    }

    internal static List<DidRow> Parse(IEnumerable<string> lines, Func<string, BitmapSource?>? frame, int max)
    {
        var steps = new List<Step>();
        var runs = new Dictionary<string, Step>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset when)) continue;
            string action = parts[1], detail = parts[2], outcome = parts[3];
            if (action.StartsWith("computer.", StringComparison.Ordinal) || QuietActions.Contains(action)) continue;
            if (action == "run")
            {
                // "run <job> <command> started…" then later "run <job> completed, exit code 0, 1.2 s".
                int space = detail.IndexOf(' ');
                if (space > 0 && outcome.StartsWith("started", StringComparison.Ordinal))
                {
                    var started = new Step { When = when, Code = detail[(space + 1)..] };
                    runs[detail[..space]] = started;
                    steps.Add(started);
                }
                else if (space < 0)
                {
                    // Its start is older than the lines read: still show that a command finished, and how.
                    if (!runs.Remove(detail, out Step? ran)) steps.Add(ran = new Step { When = when, Prefix = "Ran a command" });
                    Finish(ran, outcome);
                }
                continue;
            }
            (string prefix, string code) = Describe(action, detail, outcome);
            steps.Add(new Step { When = when, Prefix = prefix, Code = code, Frame = parts.Length > 4 ? parts[4] : "" });
        }
        // Back-to-back identical steps read as one with a count, so eight page loads are one row.
        // Only when nothing that differs would be hidden: same result, no screenshot of their own.
        var folded = new List<Step>();
        foreach (Step step in steps)
        {
            if (folded.Count > 0 && folded[^1] is { } last && last.Prefix == step.Prefix && last.Code == step.Code
                && last.Mark == step.Mark && last.Note == step.Note && last.Frame.Length == 0 && step.Frame.Length == 0)
            {
                last.Repeat++;
                last.When = step.When;
                continue;
            }
            folded.Add(step);
        }
        var rows = new List<DidRow>();
        DateTime? day = null;
        foreach (Step step in folded.TakeLast(max))
        {
            DateTimeOffset local = step.When.ToLocalTime();
            string header = local.Date == day ? "" : DayLabel(local.Date);
            day = local.Date;
            string note = step.Repeat > 1 ? (step.Note.Length > 0 ? step.Note + " ×" : "×") + step.Repeat : step.Note;
            // Decoded only for the rows kept, never for the thousands scanned.
            BitmapSource? thumb = frame is not null && step.Frame.Length > 0 ? frame(step.Frame) : null;
            rows.Add(new DidRow(local.ToString("h:mm tt", CultureInfo.CurrentCulture), step.Prefix, step.Code, thumb,
                header, step.Mark, step.Failed, note));
        }
        return rows;
    }

    static readonly Regex Seconds = new(@"([\d.]+) s$", RegexOptions.CultureInvariant);
    static readonly Regex ExitCode = new(@"exit code (-?\d+)", RegexOptions.CultureInvariant);

    static void Finish(Step run, string outcome)
    {
        run.Failed = !outcome.StartsWith("completed", StringComparison.Ordinal)
            || ExitCode.Match(outcome) is { Success: true } exit && exit.Groups[1].Value != "0";
        run.Mark = run.Failed ? "✗" : "✓";
        if (run.Failed && ExitCode.Match(outcome) is { Success: true } code) run.Note = "exit " + code.Groups[1].Value;
        else if (run.Failed) run.Note = outcome.Split(',')[0];
        else if (Seconds.Match(outcome) is { Success: true } took
            && double.TryParse(took.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            run.Note = s < 1 ? "<1s" : s < 90 ? Math.Round(s) + "s" : Math.Round(s / 60) + "m";
    }

    static (string Prefix, string Code) Describe(string action, string detail, string outcome) => action switch
    {
        "page" => ("Opened ", detail),
        "computer" => (detail.Length > 0 && detail != "desktop" ? "Used the computer (" + detail + ")" : "Used the computer", ""),
        "open" => ("Opened ", detail),
        "write" => ("Typed into the screen", ""),
        "save" => ("Saved " + detail, ""),
        "file" => ("Read " + detail, ""),
        "browser" => ("Opened the browser", ""),
        "owner" => ("You said: " + detail, ""),
        "mission" => (detail == "Done" ? "Done: " + outcome : detail, ""),
        _ when action.Length > 0 => (char.ToUpperInvariant(action[0]) + action[1..], ""),
        _ => (action, ""),
    };

    static string DayLabel(DateTime date) =>
        date == DateTime.Today ? "Today" : date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : date.ToString(date.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture);

    /// <summary>The workspace folder's own files, newest first.</summary>
    public static List<FileRow> Files(string id, int max = 30)
    {
        string folder = WorkspaceStore.FolderOf(id);
        if (!Directory.Exists(folder)) return [];
        try
        {
            return new DirectoryInfo(folder).GetFiles().Where(f => !MachineNames.Contains(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(max)
                .Select(f => new FileRow(f.Name, f.LastWriteTime.ToString(f.LastWriteTime.Date == DateTime.Today ? "h:mm tt" : "MMM d",
                    CultureInfo.CurrentCulture), f.FullName)).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>What a list was read from (log size, or the folder's file count and newest write), so
    /// an open list can reread only when it changed.</summary>
    public static (long, DateTime) Signature(string id, bool logs)
    {
        try
        {
            if (logs) { var log = new FileInfo(LogPath(id)); return (log.Exists ? log.Length : -1, default); }
            // The files' own times too: editing a file need not touch the folder's.
            var found = new DirectoryInfo(WorkspaceStore.FolderOf(id)).GetFiles();
            return (found.Length, found.Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, default); }
    }

    /// <summary>Opens a file or folder with its Windows default; false when Windows refuses.</summary>
    public static bool Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); return true; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    static BitmapSource? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or FormatException) { return null; }
    }
}
