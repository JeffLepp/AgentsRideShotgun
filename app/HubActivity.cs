using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

/// <summary>What one workspace's agents did lately, read from its action log.</summary>
internal sealed record WorkspaceActivity(IReadOnlyList<int> Week, int Commands, DateTime? LastAt, string? LastDoing)
{
    public static readonly WorkspaceActivity None = new(new int[7], 0, null, null);

    /// <summary>Actions today, the last of <see cref="Week"/>.</summary>
    public int Today => Week[^1];
}

/// <summary>
/// The hub's live line, Today strip and activity bars, all read from the
/// action log every workspace already keeps (engine/WorkspaceEvidence.cs, evidence/actions.log:
/// UTC time, action, detail, outcome, tab separated). Each log is read once and then only its new
/// lines, so a glance every couple of seconds costs a stat call per workspace. Nothing new is
/// written anywhere.
/// </summary>
internal static class HubActivity
{
    sealed class Tally
    {
        public long Read;
        public readonly Dictionary<DateOnly, int> Days = [];
        public readonly Dictionary<DateOnly, int> Runs = [];
        public DateTime? LastAt;
        public string? LastDoing;
    }

    static readonly Dictionary<string, Tally> Tallies = new(StringComparer.OrdinalIgnoreCase);
    static readonly Lock Gate = new();

    static string LogOf(string id) => Path.Combine(WorkspaceStore.FolderOf(id), "evidence", "actions.log");

    /// <summary>Reads what is new in each workspace's log. Safe on any thread; call off the UI thread.</summary>
    internal static Dictionary<string, WorkspaceActivity> Scan(IEnumerable<string> ids)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var result = new Dictionary<string, WorkspaceActivity>(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            foreach (string id in ids)
            {
                if (!Tallies.TryGetValue(id, out Tally? tally)) Tallies[id] = tally = new Tally();
                Catch(LogOf(id), ref tally);
                Tallies[id] = tally;
                var week = new int[7];
                for (int i = 0; i < 7; i++)
                {
                    DateOnly day = today.AddDays(i - 6);
                    week[i] = tally.Days.GetValueOrDefault(day);
                }
                result[id] = new WorkspaceActivity(week, tally.Runs.GetValueOrDefault(today), tally.LastAt, tally.LastDoing);
            }
        }
        return result;
    }

    static void Catch(string path, ref Tally tally)
    {
        long length;
        try { length = new FileInfo(path).Exists ? new FileInfo(path).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (length < tally.Read) tally = new Tally(); // replaced or cleared: start over
        if (length == tally.Read) return;
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = tally.Read;
            var bytes = new byte[length - tally.Read];
            int got = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            // Only whole lines; a line still being written is picked up next time.
            int end = Array.LastIndexOf(bytes, (byte)'\n', got - 1) + 1;
            if (end <= 0) return;
            text = Encoding.UTF8.GetString(bytes, 0, end);
            tally.Read += end;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 4 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at)) continue;
            string? doing = Doing(parts[1], parts[2], parts[3]);
            if (doing is null) continue;
            DateTime local = at.ToLocalTime();
            DateOnly day = DateOnly.FromDateTime(local);
            tally.Days[day] = tally.Days.GetValueOrDefault(day) + 1;
            if (parts[1] == "run") tally.Runs[day] = tally.Runs.GetValueOrDefault(day) + 1;
            tally.LastAt = local;
            tally.LastDoing = doing;
        }
        // Only the last week is ever shown.
        DateOnly oldest = DateOnly.FromDateTime(DateTime.Now).AddDays(-7);
        foreach (DateOnly old in tally.Days.Keys.Where(d => d < oldest).ToArray()) { tally.Days.Remove(old); tally.Runs.Remove(old); }
    }

    /// <summary>What an agent's action looks like to the owner, or null for bookkeeping the agent did
    /// not ask for (a screen starting, control changing hands, a window pulled over, a finished run).</summary>
    internal static string? Doing(string action, string detail, string outcome)
    {
        if (action.StartsWith("computer.", StringComparison.Ordinal))
        {
            string kind = action["computer.".Length..];
            return kind switch
            {
                _ when kind.Contains("click", StringComparison.OrdinalIgnoreCase) => "Clicking",
                _ when kind.Contains("type", StringComparison.OrdinalIgnoreCase) => "Typing",
                _ when kind.Contains("key", StringComparison.OrdinalIgnoreCase) => "Pressing keys",
                _ when kind.Contains("scroll", StringComparison.OrdinalIgnoreCase) => "Scrolling",
                _ when kind.Contains("drag", StringComparison.OrdinalIgnoreCase) => "Dragging",
                _ => "Using the screen",
            };
        }
        return action switch
        {
            "computer" or "batch" => "Using the screen",
            "run" => outcome.StartsWith("started", StringComparison.Ordinal) ? "Running a command" : null,
            "browser" => detail == "warm-up" ? null : "Browsing",
            "page" or "tabs" => "Reading a page",
            "open" => "Opening an app",
            "file" or "save" => "Handling a file",
            "shot" or "marks" or "elements" => "Looking at the screen",
            _ => null,
        };
    }
}

/// <summary>One activity bar's height: its share of the row's busiest day, over 12 DIP, never
/// shorter than 2 so a quiet day still reads as a day.</summary>
internal sealed class WeekBarHeight : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Math.Max(2, Math.Round((value is double share ? share : 0) * 12));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
