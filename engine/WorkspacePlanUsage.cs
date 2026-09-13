using System.IO;

namespace HiveMind.AgentWorkspaces;

/// <summary>What the owner's Claude plan has been spent, in the unit his plan is actually
/// measured in.</summary>
/// <param name="Plan">The plan the provider named, e.g. Pro. Empty when it did not say.</param>
/// <param name="WeeklyPercent">Percent of the weekly limit used, or null when unreported.</param>
/// <param name="FiveHourPercent">Percent of the five-hour limit used, or null when unreported.</param>
/// <param name="WeeklyResetsAt">When the weekly window rolls over, if the provider said.</param>
/// <param name="Stale">True when this reading is old enough that the tracker no longer calls it
/// current. It is still shown, because an old real number beats an invented fresh one, but it is
/// labelled.</param>
public sealed record WorkspacePlanReading(
    string Plan,
    double? WeeklyPercent,
    double? FiveHourPercent,
    DateTimeOffset? WeeklyResetsAt,
    bool Stale)
{
    /// <summary>One line for the settings panel. Null when the provider reported no percentage at
    /// all, because "0%" and "we do not know" are not the same thing and only one of them is true.</summary>
    public string? Line()
    {
        if (WeeklyPercent is null && FiveHourPercent is null) return null;
        var line = new System.Text.StringBuilder(Plan.Length > 0 ? Plan : "Claude plan");
        if (FiveHourPercent is { } session) line.Append(" - ").Append(Whole(session)).Append("% of this session's limit");
        if (WeeklyPercent is { } week)
        {
            line.Append(FiveHourPercent is null ? " - " : ", ").Append(Whole(week)).Append("% of the week's");
            if (WeeklyResetsAt is { } resets) line.Append(", resets ").Append(resets.LocalDateTime.ToString("d MMM HH:mm"));
        }
        if (Stale) line.Append(" (last reading)");
        return line.ToString();
    }

    static string Whole(double percent) => Math.Clamp(percent, 0, 100).ToString("F0",
        System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The owner's plan usage as a percentage of his own limits, for the workspaces that run on his
/// subscription.
///
/// A subscription is not billed per run, so a dollar figure beside a subscription mission is the
/// wrong unit twice: no money is being spent, and the number it comes from is an API price list
/// this owner is not on. What his plan actually meters is a percentage of a five-hour and a weekly
/// limit, so that is what a subscription workspace shows. The dollar estimate stays for CLI/API
/// credentials, where it is the real unit and the ceiling is real money.
///
/// This reads only Deskweave's own optional usage snapshot and never calls Anthropic itself.
/// This standalone build has no usage polling surface, so the normal result is unavailable.
/// It never reaches into HiveMind's registry or caches to fill that gap.
/// </summary>
static class WorkspacePlanUsage
{
    /// <summary>
    /// The reading for the Claude home this workspace's boss agent actually runs against, or null
    /// when there is none. Null is a real answer: no tracker snapshot yet, a Claude home the owner
    /// has not added to the tracker, or a provider that reported no percentage. The caller says so
    /// rather than showing a number that belongs to a different account.
    /// </summary>
    public static WorkspacePlanReading? Read(DateTimeOffset now)
    {
        try
        {
            string home = ClaudeHome();
            var registry = new UsageProfileRegistry();
            // Matched by home, never by "the first Claude profile". A PC can have several, and the
            // one this workspace bills against is the one whose root the CLI will read.
            UsageProfile? profile = registry.Profiles.FirstOrDefault(one =>
                one.Provider == UsageProvider.Claude
                && string.Equals(UsageProfile.NormalizeRoot(one.Root), home, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return null;

            if (!new UsageSnapshotStore().Load([profile]).TryGetValue(profile.Id, out UsageAccountSnapshot? snapshot))
                return null;
            if (snapshot.ErrorCategory != UsageErrorCategory.None) return null;

            return new WorkspacePlanReading(
                snapshot.Plan ?? string.Empty,
                snapshot.Weekly?.UsedPercent,
                snapshot.FiveHour?.UsedPercent,
                snapshot.Weekly?.ResetsAt,
                snapshot.FreshnessAt(now) != UsageFreshness.Fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
        {
            // A number nobody can read is worth less than saying nothing. The mission does not
            // depend on this and must not fail for it.
            return null;
        }
    }

    /// <summary>
    /// Where the boss agent's Claude CLI keeps its account. `CLAUDE_CONFIG_DIR` moves it, and the
    /// workspace runs the CLI with the owner's own environment, so the override is his and applies.
    /// </summary>
    static string ClaudeHome() => UsageProfile.NormalizeRoot(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } moved
            ? moved
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
}
