using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Whether a workspace browser that starts again on the same profile brings back the tabs of its
/// last run. Without this, a workspace browser can gather tabs over days of sessions, many of
/// them blank. Chrome is started on a hidden workspace desktop, with two tabs, killed the
/// way a sleeping workspace kills it, then started again with the given flags; its DevTools list
/// says what came back.
///
/// Run: Deskweave.Probe.exe --chrome-restore "C:\absolute\output"
/// </summary>
internal static class ChromeRestoreProbe
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Chrome restore probe exceeded its 120-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
        var results = new List<object>();
        string? failure = null;
        try
        {
            string chrome = WorkspaceBrowser.ChromePath;
            foreach ((string name, string flags) in new[]
            {
                ("restore-last-session=false", "--restore-last-session=false"),
                ("no restore flag", ""),
            })
            {
                string profile = Path.Combine(output, "profile-" + results.Count);
                string common = $"--no-first-run --no-default-browser-check --hide-crash-restore-bubble "
                    + $"--disable-session-crashed-bubble --remote-debugging-port=9337 --user-data-dir=\"{profile}\" ";
                string[] before, after;
                // Each run on its own workspace desktop, ended by disposing it: the job object kills
                // the whole browser, exactly as a workspace going to sleep does.
                using (AgentDesktop one = AgentDesktop.Create("restore-" + Guid.NewGuid().ToString("N")[..8]))
                {
                    one.Launch(chrome, common + "http://127.0.0.1:5391/?one file:///C:/Windows/win.ini");
                    Thread.Sleep(9000); // long enough for Chrome to write its session
                    before = Pages();
                }
                Thread.Sleep(1500);
                using (AgentDesktop two = AgentDesktop.Create("restore-" + Guid.NewGuid().ToString("N")[..8]))
                {
                    two.Launch(chrome, common + flags + " about:blank");
                    Thread.Sleep(6000);
                    after = Pages();
                }
                Thread.Sleep(1500);
                results.Add(new { name, before, after });
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }

        // The cleanup itself: a started browser with extra tabs keeps only the one it is on.
        try
        {
            using AgentDesktop desktop = AgentDesktop.Create("tabs-" + Guid.NewGuid().ToString("N")[..8]);
            WorkspaceBrowser? browser = WorkspaceBrowser.Start(desktop, "about:blank").GetAwaiter().GetResult();
            if (browser is null) throw new InvalidOperationException("browser did not start");
            using (browser)
            {
                browser.NewTab("data:text/html,extra-one").GetAwaiter().GetResult();
                browser.NewTab("data:text/html,extra-two").GetAwaiter().GetResult();
                int before = browser.Tabs().GetAwaiter().GetResult().Count;
                browser.CloseLeftovers(CancellationToken.None).GetAwaiter().GetResult();
                Thread.Sleep(500);
                int after = browser.Tabs().GetAwaiter().GetResult().Count;
                results.Add(new { name = "close leftovers", before, after, passed = before == 3 && after == 1 });
            }
        }
        catch (Exception ex) { failure ??= ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { results, failure },
            new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    static string[] Pages()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using JsonDocument list = JsonDocument.Parse(http.GetStringAsync("http://127.0.0.1:9337/json/list").GetAwaiter().GetResult());
            return [.. list.RootElement.EnumerateArray()
                .Where(t => t.GetProperty("type").GetString() == "page")
                .Select(t => t.GetProperty("url").GetString() ?? "")];
        }
        catch (Exception ex) { return ["unreachable: " + ex.GetType().Name]; }
    }
}

/// <summary>
/// The selectors page read lists: each must match exactly one element, and clicking it must hit
/// that element - on a page built to make that hard (repeated links and buttons, no ids, quotes).
///
/// Run: Deskweave.Probe.exe --page-read "C:\absolute\output"
/// </summary>
internal static class PageReadProbe
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string? failure = null;
        var lines = new List<string>();
        string log = "";
        try
        {
            using AgentDesktop desktop = AgentDesktop.Create("read-" + Guid.NewGuid().ToString("N")[..8]);
            string page = Path.Combine(desktop.Folder!, "awkward.html");
            File.WriteAllText(page, """
                <!doctype html><title>awkward</title>
                <nav><a href="#a">One</a> <a href="#a">One again</a> <a href="#b">Two</a></nav>
                <div><button>Save</button><button>Save</button></div>
                <ul><li><button>Delete</button></li><li><button>Delete</button></li></ul>
                <section id="form"><input placeholder="Name"><input placeholder="Name"></section>
                <button id="go">Go</button> <button aria-label='Say "hi"'>x</button>
                <p id="log"></p>
                <script>
                  let n = 0;
                  document.addEventListener('click', e => {
                    const t = e.target.closest('a,button,input');
                    if (!t) return;
                    n++;
                    document.getElementById('log').textContent += '|' + n + ':' + (t.getAttribute('aria-label') || t.textContent || t.placeholder);
                    if (t.tagName === 'A') e.preventDefault();
                  }, true);
                </script>
                """);
            WorkspaceBrowser? browser = WorkspaceBrowser.Start(desktop, new Uri(page).AbsoluteUri).GetAwaiter().GetResult();
            if (browser is null) throw new InvalidOperationException("browser did not start");
            using (browser)
            {
                string read = browser.Read().GetAwaiter().GetResult();
                lines = [.. read[(read.IndexOf("-- controls --") + 15)..].Split('\n', StringSplitOptions.RemoveEmptyEntries)];
                foreach (string line in lines)
                {
                    string selector = line[..line.IndexOf("  \"")];
                    if (!browser.Click(selector).GetAwaiter().GetResult()) throw new InvalidOperationException("no match: " + selector);
                    Thread.Sleep(150);
                }
                string after = browser.Read().GetAwaiter().GetResult();
                log = after.Split('\n').FirstOrDefault(l => l.StartsWith('|')) ?? "";
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        string expected = "|1:One|2:One again|3:Two|4:Save|5:Save|6:Delete|7:Delete|8:Name|9:Name|10:Go|11:Say \"hi\"";
        bool passed = failure is null && log == expected;
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed, lines, log, expected, failure },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
