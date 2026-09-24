using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Two problems an agent can hit when driving a WPF app's nav rail on a real
/// workspace desktop:
///
/// - a picture taken while the app was busy for a moment after a click showed whatever was behind
///   it instead, flagged only by a line of text; and
/// - a tight row of nav items could only be clicked by pixel, read off a picture scaled down to
///   1280x720, and a couple of clicks landed on the neighbouring item.
///
/// The fixture is a WPF window with a data-templated nav rail - rows 22 px tall, an icon and a
/// label in each, named by their data the way a view model's ToString names them - in front of a
/// maximized magenta window. Picking "Blip" makes it busy for 0.8 s and "Busy" for 5 s, both
/// queued behind the selection the way a view switch does its work.
///
/// Run: Deskweave.Probe.exe --agent-view "C:\absolute\output"
/// </summary>
internal static class AgentViewProbe
{
    static readonly string[] Items = ["Home", "Missions", "Agents", "Blip", "Logs", "Settings", "Busy", "About"];

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Agent view probe exceeded its 120-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);

        string nonce = Guid.NewGuid().ToString("N")[..8];
        string picks = Path.Combine(output, "picks.txt");
        var checks = new List<object>();
        string? failure = null;
        AgentDesktop? desktop = null;
        WorkspaceControl? control = null;

        void Check(bool passed, string claim, object? detail = null)
        {
            checks.Add(new { claim, passed, detail });
            File.AppendAllText(Path.Combine(output, "progress.log"), (passed ? "PASS " : "FAIL ") + claim + "\n");
            if (!passed) throw new InvalidOperationException("Failed: " + claim);
        }

        try
        {
            desktop = AgentDesktop.Create("view-" + nonce);
            control = new WorkspaceControl(desktop);
            Check(control.AgentTakes(), "agent has control");

            string script = Path.Combine(output, "fixture.ps1");
            File.WriteAllText(script, Fixture);
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string Args(string kind, string title) =>
                $"-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" \"{picks}\" \"{title}\" {kind}";

            desktop.Launch(powershell, Args("backdrop", "backdrop-" + nonce));
            AgentWindow backdrop = WaitForWindow(desktop, "backdrop-" + nonce);
            desktop.Launch(powershell, Args("nav", "nav-" + nonce));
            AgentWindow nav = WaitForWindow(desktop, "nav-" + nonce);
            Thread.Sleep(500);

            // Parsing: control stands in for x and y, never beside them, and never in the browser.
            Check(Parse("""{"actions":[{"type":"click","control":5}]}""").Actions[0].Control == 5,
                "a click takes a control number instead of x and y");
            Check(Throws("""{"actions":[{"type":"click","control":5,"x":1,"y":1}]}"""), "control and x/y together are refused");
            Check(Throws("""{"target":"browser","actions":[{"type":"click","control":5}]}"""), "control is refused for the browser");
            Check(Throws("""{"actions":[{"type":"click"}]}"""), "a click with neither is refused");

            IReadOnlyList<WorkspaceElement> found = control.Elements(nav.Handle);
            WorkspaceElement Item(string name) => found.First(e => e.Type == "ListItem" && e.Name == name);
            Check(Items.All(name => found.Any(e => e.Type == "ListItem" && e.Name == name)), "every nav item is numbered",
                found.Where(e => e.Type == "ListItem").Select(e => e.ToString()).ToArray());

            // The tight row, clicked by number. Each has to select exactly the item named.
            foreach (string name in new[] { "Agents", "Logs", "Missions", "About", "Home" })
            {
                int before = Lines(picks).Length;
                ComputerResult clicked = Act(control, $$"""{"screenshot":false,"actions":[{"type":"click","control":{{Item(name).Id}}}]}""");
                Check(clicked.Receipt.Status == "completed", $"click on control {Item(name).Id} ({name}) completed", clicked.Receipt);
                Check(WaitFor(() => Lines(picks).Length > before) && Lines(picks)[^1] == name,
                    $"clicking {name} by number selected {name}", Lines(picks));
            }

            ComputerResult unknown = Act(control, """{"screenshot":false,"actions":[{"type":"click","control":987654}]}""");
            Check(unknown.Receipt.Status == "rejected" && unknown.Receipt.Reason.Contains("not known"),
                "an unknown number is refused before anything is sent", unknown.Receipt);

            // Covered: the backdrop in front. A click by number must not land on it.
            control.Arrange(backdrop.Handle, WindowArrangement.Front, 0, 0, 0, 0);
            Thread.Sleep(300);
            int beforeCovered = Lines(picks).Length;
            ComputerResult covered = Act(control, $$"""{"screenshot":false,"actions":[{"type":"click","control":{{Item("Settings").Id}}}]}""");
            Check(covered.Receipt.Status == "interrupted" && covered.Receipt.Reason.Contains("covered"),
                "a covered control is refused, with the reason", covered.Receipt);
            Check(Lines(picks).Length == beforeCovered, "nothing was selected through the covering window");
            control.Arrange(nav.Handle, WindowArrangement.Front, 0, 0, 0, 0);
            Thread.Sleep(300);

            // The numbered pictures, for the eye: badges above the short buttons, inside the rows.
            Save(control.Shot(nav.Handle, marks: true)!, Path.Combine(output, "marks-window.png"));
            ComputerResult marked = Act(control, """{"marks":true}""");
            if (marked.Frame is not null) Save(marked.Frame, Path.Combine(output, "marks-screen.png"));

            // A current picture first: the nav window's white body where the sample point is.
            BitmapSource calm = control.Shot()!;
            Save(calm, Path.Combine(output, "calm.png"));
            (int bx, int by) = (nav.X + nav.Width - 60, nav.Y + nav.Height - 60);
            (int cx, int cy) = (nav.X + nav.Width / 2 + 60, nav.Y + nav.Height / 2);
            Check(IsWhite(Pixel(calm, bx, by)), "before: the nav window's body is white at the sample point", Hex(Pixel(calm, bx, by)));

            // Busy for 0.8 s after the click: the picture waits it out and is complete.
            var watch = Stopwatch.StartNew();
            ComputerResult blip = Act(control, $$"""{"actions":[{"type":"click","control":{{Item("Blip").Id}}}]}""");
            long blipMs = watch.ElapsedMilliseconds;
            Save(blip.Frame!, Path.Combine(output, "blip.png"));
            Check(blip.Frame is not null && control.ScreenState.Unresponsive == 0,
                "a short busy spell is waited out and the picture is complete", new { blipMs, control.ScreenState.Note });
            double scale = blip.Frame!.PixelWidth / (double)AgentDesktop.ScreenWidth;
            Check(IsWhite(Pixel(blip.Frame, bx, by, scale)) && Dark(blip.Frame, cx, cy, scale) == 0,
                "that picture shows the window itself, with no busy label", Hex(Pixel(blip.Frame, bx, by, scale)));
            Thread.Sleep(300);

            // Busy for 5 s: longer than the wait, so the window is drawn greyed as it last looked.
            watch.Restart();
            ComputerResult busy = Act(control, $$"""{"actions":[{"type":"click","control":{{Item("Busy").Id}}}]}""");
            long busyMs = watch.ElapsedMilliseconds;
            Save(busy.Frame!, Path.Combine(output, "busy.png"));
            Color seen = Pixel(busy.Frame!, bx, by, scale);
            Check(busy.Frame is not null && control.ScreenState.Unresponsive == 1
                && control.ScreenState.Note.Contains("greyed"), "a long busy spell is reported, and the note says how it is drawn",
                new { busyMs, control.ScreenState.Note });
            Check(!IsMagenta(seen) && IsWhite(seen),
                "the busy window is drawn where it is, not replaced by the window behind it", Hex(seen));
            Check(Dark(busy.Frame!, cx, cy, scale) > 0, "the busy window carries the Not responding label");
            Check(busyMs is >= 2000 and < 4500, "the wait is bounded at about two seconds", busyMs);

            // And the owner's view afterwards: once it answers again, the plain window is back.
            Check(WaitFor(() => desktop.Answers(nav.Handle), TimeSpan.FromSeconds(8)), "the fixture answers again");
            BitmapSource after = control.Shot()!;
            Save(after, Path.Combine(output, "after.png"));
            Check(control.ScreenState.Unresponsive == 0 && Dark(after, cx, cy, 1) == 0, "the label goes once it answers");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { control?.Dispose(); } catch (Exception ex) { failure ??= ex.ToString(); }
            try { desktop?.Dispose(); } catch (Exception ex) { failure ??= ex.ToString(); }
        }

        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow,
            passed = failure is null,
            checks,
            failure,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    static ComputerRequest Parse(string json) => WorkspaceComputer.Parse(JsonDocument.Parse(json).RootElement);

    static bool Throws(string json)
    {
        try { Parse(json); return false; }
        catch (ArgumentException) { return true; }
    }

    static ComputerResult Act(WorkspaceControl control, string json) =>
        control.Computer(Parse(json), CancellationToken.None).GetAwaiter().GetResult();

    static string[] Lines(string path) => File.Exists(path)
        ? File.ReadAllLines(path).Where(line => line.Length > 0).ToArray() : [];

    static AgentWindow WaitForWindow(AgentDesktop desktop, string title)
    {
        AgentWindow? found = null;
        if (!WaitFor(() => (found = desktop.Windows().FirstOrDefault(w => w.Title == title)) is not null, TimeSpan.FromSeconds(25)))
            throw new InvalidOperationException("No window titled " + title);
        return found!;
    }

    static bool WaitFor(Func<bool> predicate, TimeSpan? bound = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (bound ?? TimeSpan.FromSeconds(3))) { if (predicate()) return true; Thread.Sleep(50); }
        return predicate();
    }

    static Color Pixel(BitmapSource frame, int x, int y, double scale = 1)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect((int)(x * scale), (int)(y * scale), 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Near-black pixels in a strip across a point: the label's tag, or nothing.</summary>
    static int Dark(BitmapSource frame, int x, int y, double scale)
    {
        int count = 0;
        for (int dx = -120; dx <= 120; dx += 4)
        {
            Color c = Pixel(frame, x + dx, y, scale);
            if (c.R < 0x50 && c.G < 0x50 && c.B < 0x50) count++;
        }
        return count;
    }

    static bool IsWhite(Color c) => c.R > 0xE0 && c.G > 0xE0 && c.B > 0xE0;
    static bool IsMagenta(Color c) => c.R > 0xC0 && c.G < 0x60 && c.B > 0xC0;
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static void Save(BitmapSource frame, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    const string Fixture = """
param([string]$OutFile, [string]$Title, [string]$Kind)
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase
if ($Kind -eq 'backdrop') {
  $backdrop = New-Object System.Windows.Window
  $backdrop.Title = $Title
  $backdrop.Background = [System.Windows.Media.Brushes]::Magenta
  $backdrop.WindowState = [System.Windows.WindowState]::Maximized
  $backdrop.Show()
  [System.Windows.Threading.Dispatcher]::Run()
  return
}
$xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Title='$Title'
        Left='40' Top='40' Width='640' Height='400' Background='White' WindowStartupLocation='Manual'>
  <Grid>
    <ListBox Name='Nav' Width='160' HorizontalAlignment='Left' BorderThickness='0'>
      <ListBox.ItemContainerStyle>
        <Style TargetType='ListBoxItem'><Setter Property='Height' Value='22'/><Setter Property='Padding' Value='4,0'/></Style>
      </ListBox.ItemContainerStyle>
      <ListBox.ItemTemplate>
        <DataTemplate>
          <StackPanel Orientation='Horizontal'>
            <Ellipse Width='10' Height='10' Fill='SteelBlue' Margin='0,0,6,0'/>
            <TextBlock Text='{Binding}'/>
          </StackPanel>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
    <StackPanel Orientation='Horizontal' VerticalAlignment='Bottom' HorizontalAlignment='Right' Margin='0,0,10,10'>
      <Button Content='Remove selected' Padding='3,1'/><Button Content='Rename...' Padding='3,1'/>
      <Button Content='Clear all' Padding='3,1'/><Button Content='Save' Padding='3,1' Margin='12,0,0,0'/>
    </StackPanel>
  </Grid>
</Window>
"@
$window = [System.Windows.Markup.XamlReader]::Parse($xaml)
$nav = $window.FindName('Nav')
$nav.ItemsSource = @('Home','Missions','Agents','Blip','Logs','Settings','Busy','About')
$nav.Add_SelectionChanged({
  $picked = [string]$nav.SelectedItem
  [System.IO.File]::AppendAllText($OutFile, $picked + "`n")
  $pause = if ($picked -eq 'Busy') { 5000 } elseif ($picked -eq 'Blip') { 800 } else { 0 }
  if ($pause -gt 0) {
    $work = [Action]{ [System.Threading.Thread]::Sleep($pause) }.GetNewClosure()
    $null = $window.Dispatcher.BeginInvoke($work, [System.Windows.Threading.DispatcherPriority]::Background)
  }
})
$window.Show()
[System.Windows.Threading.Dispatcher]::Run()
""";
}
