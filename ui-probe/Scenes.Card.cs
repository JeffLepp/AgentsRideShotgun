using System.Globalization;
using System.IO;
using System.Windows;
using Deskweave.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Settings inside the hub's card (owner's pick, 2026-09-22): its home and each page, at
/// the stack's own 340 x 560. No reference image; these are for review.</summary>
static class CardScenes
{
    static async Task<MainWindow> Stack(SceneContext scene, HubEntry[]? recent = null, double height = 560)
    {
        var window = scene.Own(new MainWindow());
        window.Left = SceneContext.OffScreen.X;
        window.Top = SceneContext.OffScreen.Y;
        window.Show();
        await scene.Settle();
        window.Hub.LoadFixture(
        [
            new HubEntry("shop") { Name = "shop", Working = true, AgentText = "Claude Code", Preview = scene.Site("shop") },
        ],
        recent ??
        [
            new HubEntry("api") { Name = "api", Age = "yesterday", SidebarAge = "Yesterday", Preview = scene.Site("term") },
            new HubEntry("scratch") { Name = "Scratch", Age = "Mon", SidebarAge = "Monday", Preview = scene.Site("term") },
        ]);
        window.ShowStack();
        window.Width = 340;
        window.Height = height;
        await scene.Settle();
        return window;
    }

    static async Task<FrameworkElement> Page(SceneContext scene, string? category)
    {
        MainWindow window = await Stack(scene);
        window.OpenSettings();
        await scene.Settle();
        if (category is not null) { window.CardSettings!.Show(category); await scene.Settle(); }
        return window;
    }

    /// <summary>The activity reader against a real log file: agent actions count, bookkeeping does
    /// not, a line still being written waits, and only new lines are read the second time.</summary>
    internal static Task Gate()
    {
        StoredWorkspace workspace = WorkspaceStore.Create("Activity check");
        try
        {
            string folder = Path.Combine(WorkspaceStore.FolderOf(workspace.Id), "evidence");
            Directory.CreateDirectory(folder);
            string log = Path.Combine(folder, "actions.log");
            static string Line(DateTime at, string action, string detail, string outcome) =>
                at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "\t" + action + "\t" + detail + "\t" + outcome + "\n";
            DateTime now = DateTime.Now;
            File.WriteAllText(log,
                Line(now.AddDays(-2), "computer.click", "control 4", "pressed")
                + Line(now.AddMinutes(-5), "workspace", "x", "control plane up")
                + Line(now.AddMinutes(-4), "control", "agent", "took control")
                + Line(now.AddMinutes(-3), "run", "job-1 npm test", "started, pid 1")
                + Line(now.AddMinutes(-2), "run", "job-1", "completed, exit code 0")
                + Line(now.AddSeconds(-5), "computer.type", "control 7", "input delivered")
                + "2026-01-01T00:00:00Z\tcomputer.click"); // still being written
            WorkspaceActivity first = HubActivity.Scan([workspace.Id])[workspace.Id];
            Program.Check(first.Today == 2 && first.Week[4] == 1 && first.Week.Sum() == 3 && first.Commands == 1
                && first.LastDoing == "Typing",
                "Today, the week's bars and the live line count the agent's actions and skip bookkeeping and half-written lines");
            File.AppendAllText(log, "\t-\tx\n" + Line(DateTime.Now, "computer.scroll", "down", "input delivered"));
            WorkspaceActivity second = HubActivity.Scan([workspace.Id])[workspace.Id];
            Program.Check(second.Today == 3 && second.LastDoing == "Scrolling",
                "A second look reads only the new lines, finishing the half-written one");
        }
        finally { WorkspaceStore.Delete(workspace.Id); }
        return Task.CompletedTask;
    }

    /// <summary>The stack with a week of made-up activity: the Today strip, the live line and the
    /// bars on Recent.</summary>
    [Scene("card-stack-live", "")]
    static async Task<FrameworkElement> Live(SceneContext scene)
    {
        MainWindow window = await Stack(scene);
        window.Hub.ApplyActivity(new Dictionary<string, WorkspaceActivity>
        {
            ["shop"] = new([4, 12, 9, 20, 15, 30, 41], 6, DateTime.Now.AddSeconds(-3), "Clicking"),
            ["api"] = new([2, 0, 7, 11, 3, 5, 0], 0, DateTime.Now.AddDays(-1), "Running a command"),
            ["scratch"] = new([0, 0, 0, 0, 0, 0, 0], 0, null, null),
        });
        await scene.Settle();
        return window;
    }

    /// <summary>The README picture: the live stack with a fuller Recent list, a week of bars on each.</summary>
    [Scene("card-stack-readme", "")]
    static async Task<FrameworkElement> Readme(SceneContext scene)
    {
        (string id, string name, string age, string day, int[] week)[] recent =
        [
            ("api", "api", "yesterday", "Yesterday", [2, 0, 7, 11, 3, 5, 0]),
            ("docs", "docs-site", "yesterday", "Yesterday", [0, 4, 2, 0, 6, 9, 0]),
            ("dashboard", "dashboard", "Mon", "Monday", [3, 8, 5, 12, 7, 0, 0]),
            ("mobile", "mobile-app", "Mon", "Monday", [0, 0, 3, 1, 9, 0, 0]),
            ("landing", "landing-page", "Sun", "Sunday", [5, 2, 0, 8, 0, 0, 0]),
            ("scratch", "Scratch", "Sat", "Saturday", [1, 0, 2, 0, 0, 0, 0]),
        ];
        MainWindow window = await Stack(scene,
            [.. recent.Select(r => new HubEntry(r.id) { Name = r.name, Age = r.age, SidebarAge = r.day, Preview = scene.Site("term") })],
            680);
        var activity = recent.ToDictionary(r => r.id, r => new WorkspaceActivity(r.week, 0,
            DateTime.Now.AddDays(-(6 - Array.FindLastIndex(r.week, n => n > 0))), null));
        activity["shop"] = new([4, 12, 9, 20, 15, 30, 41], 6, DateTime.Now.AddSeconds(-3), "Clicking");
        window.Hub.ApplyActivity(activity);
        await scene.Settle();
        return window;
    }

    [Scene("card-home", "")]
    static Task<FrameworkElement> Home(SceneContext scene) => Page(scene, null);

    [Scene("card-general", "")]
    static Task<FrameworkElement> General(SceneContext scene) => Page(scene, "general");

    [Scene("card-agents", "")]
    static Task<FrameworkElement> Agents(SceneContext scene) => Page(scene, "agents");

    [Scene("card-history", "")]
    static Task<FrameworkElement> History(SceneContext scene) => Page(scene, "history");
}
