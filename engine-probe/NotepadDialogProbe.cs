using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Deskweave.AgentWorkspaces;

/// <summary>
/// Notepad's "Do you want to save changes?" prompt, answered by control number the way an agent
/// does it: type, close, press "Don't Save".
/// Also writes what UI Automation says about the button, so the refusal can be explained.
///
/// Run: Deskweave.Probe.exe --notepad-dialog "C:\absolute\output"
/// </summary>
internal static class NotepadDialogProbe
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenDesktopW(string desktop, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Notepad dialog probe exceeded its 90-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);

        string nonce = Guid.NewGuid().ToString("N")[..8];
        var notes = new List<object>();
        string? failure = null;
        bool pressed = false, closed = false;
        AgentDesktop? desktop = null;
        WorkspaceControl? control = null;
        try
        {
            desktop = AgentDesktop.Create("notepad-" + nonce);
            control = new WorkspaceControl(desktop);
            if (!control.AgentTakes()) throw new InvalidOperationException("no control");
            int pid = desktop.Launch(Path.Combine(Environment.SystemDirectory, "notepad.exe"), null);
            AgentWindow editor = Wait(desktop, w => w.Title.EndsWith("Notepad") && !w.Title.StartsWith('*') && w.Title != "Notepad");
            TypedText typed = desktop.Type("unsaved words", editor.Handle);
            notes.Add(new { typed = typed.ToString() });
            control.Arrange(editor.Handle, WindowArrangement.Close);
            AgentWindow prompt = Wait(desktop, w => w.Title == "Notepad");
            IReadOnlyList<WorkspaceElement> found = control.Elements(prompt.Handle);
            notes.Add(new { controls = found.Select(e => e.ToString()).ToArray() });
            WorkspaceElement dontSave = found.First(e => e.Name == "Don't Save");

            // What UI Automation itself says about that button, read on a thread bound to the desktop.
            var detail = new Thread(() =>
            {
                try
                {
                    SetThreadDesktop(OpenDesktopW(desktop.Name, 0, false, 0x10000000));
                    AutomationElement root = AutomationElement.FromHandle(prompt.Handle);
                    AutomationElement? button = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "Don't Save"));
                    if (button is null) { notes.Add(new { uia = "not found" }); return; }
                    var now = button.Current;
                    notes.Add(new
                    {
                        uia = new
                        {
                            now.ClassName, now.FrameworkId, now.NativeWindowHandle, type = now.ControlType.ProgrammaticName,
                            patterns = button.GetSupportedPatterns().Select(p => p.ProgrammaticName).ToArray(),
                            box = now.BoundingRectangle.ToString(),
                        },
                    });
                }
                catch (Exception ex) { notes.Add(new { uia = ex.ToString() }); }
            });
            detail.SetApartmentState(ApartmentState.MTA);
            detail.Start();
            detail.Join();

            pressed = control.Press(dontSave.Id);
            closed = WaitFor(() => !desktop.Windows().Any(w => w.Title.Contains("Notepad")), TimeSpan.FromSeconds(5));
            notes.Add(new { pressed, closed, windows = desktop.Windows().Select(w => w.Title).ToArray() });
            string log = Path.Combine(desktop.Folder ?? "", "evidence", "actions.log");
            if (File.Exists(log)) notes.Add(new { evidence = File.ReadAllLines(log).TakeLast(6).ToArray() });
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { control?.Dispose(); } catch { }
            try { desktop?.Dispose(); } catch { }
        }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            passed = failure is null && pressed && closed, notes, failure,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null && pressed && closed ? 0 : 1;
    }

    static AgentWindow Wait(AgentDesktop desktop, Func<AgentWindow, bool> match)
    {
        AgentWindow? found = null;
        if (!WaitFor(() => (found = desktop.Windows().FirstOrDefault(match)) is not null, TimeSpan.FromSeconds(20)))
            throw new InvalidOperationException("window did not appear: " + string.Join(", ", desktop.Windows().Select(w => w.Title)));
        return found!;
    }

    static bool WaitFor(Func<bool> predicate, TimeSpan bound)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < bound) { if (predicate()) return true; Thread.Sleep(100); }
        return predicate();
    }
}
