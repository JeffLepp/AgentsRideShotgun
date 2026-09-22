using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Deskweave.AgentWorkspaces;

/// <summary>
/// The strip along the bottom of an agent's screen when Settings > General > Agent screens is Full
/// desktop: a launcher for any Start-menu app, a button per open window (the front one marked), and
/// the time. Drawn by Deskweave rather than a second Windows shell: Windows runs one shell per
/// session, and a second one on another desktop loses its Start menu and tray. Viewer-only, so an
/// agent's screenshots never show it, and it refreshes only while it is on screen. Whatever the owner
/// does here takes the wheel the way a click on the screen does; the agent waits, then carries on.
/// </summary>
public sealed class WorkspaceTaskbar : Border
{
    readonly Func<WorkspaceRuntime?> _runtime;
    readonly Action _ownerActed;
    readonly StackPanel _windows = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _clock = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
    readonly Button _launcher;
    readonly Popup _menu;
    readonly TextBox _search = new() { FontSize = 12.5, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1) };
    readonly ListBox _apps = new() { MaxHeight = 280, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _loading = new() { FontSize = 12, Margin = new Thickness(6, 8, 6, 4), Text = "Finding apps..." };
    readonly TextBlock _launchMessage = new()
    {
        FontSize = 12, Margin = new Thickness(6, 8, 6, 4),
        TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
    };
    readonly DispatcherTimer _tick;
    IReadOnlyList<WorkspacePrograms.Shortcut> _all = [];
    string _shown = string.Empty;
    bool _refreshing;
    bool _launching;

    /// <param name="runtime">The workspace this strip belongs to, or null while it is stopped.</param>
    /// <param name="ownerActed">Called when the owner uses the strip, so the view takes the wheel.</param>
    /// <param name="height">40 on the workspace page, 30 in the corner window.</param>
    public WorkspaceTaskbar(Func<WorkspaceRuntime?> runtime, Action ownerActed, double height)
    {
        _runtime = runtime;
        _ownerActed = ownerActed;
        Height = height;
        VerticalAlignment = VerticalAlignment.Bottom;
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BackgroundProperty, "GlassBrush");
        SetResourceReference(BorderBrushProperty, "HairlineBrush");
        AutomationProperties.SetName(this, "Workspace taskbar");

        _launcher = Flat(Apps(height), "Open an app in this workspace");
        _launcher.Margin = new Thickness(6, 0, 4, 0);
        _launcher.Click += (_, _) => OpenMenu();
        _clock.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_launcher);
        var windows = new ScrollViewer
        {
            Content = _windows, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        Grid.SetColumn(windows, 1);
        row.Children.Add(windows);
        Grid.SetColumn(_clock, 2);
        row.Children.Add(_clock);
        Child = row;

        _search.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        _search.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        _search.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
        AutomationProperties.SetName(_search, "Search apps");
        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += SearchKey;
        _apps.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        _apps.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        _apps.FontSize = 12.5;
        AutomationProperties.SetName(_apps, "Apps");
        _apps.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as DependencyObject)?.FindParent<ListBoxItem>() is { DataContext: WorkspacePrograms.Shortcut app })
                _ = Launch(app);
        };
        _apps.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _apps.SelectedItem is WorkspacePrograms.Shortcut app) { _ = Launch(app); e.Handled = true; }
            else if (e.Key == Key.Escape) { _menu!.IsOpen = false; e.Handled = true; }
        };
        _apps.DisplayMemberPath = nameof(WorkspacePrograms.Shortcut.Name);
        _loading.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        _launchMessage.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        AutomationProperties.SetLiveSetting(_launchMessage, AutomationLiveSetting.Polite);
        var panel = new StackPanel();
        panel.Children.Add(_search);
        panel.Children.Add(_loading);
        panel.Children.Add(_launchMessage);
        panel.Children.Add(_apps);
        var card = new Border
        {
            Width = 260, Padding = new Thickness(8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.18 },
            Margin = new Thickness(12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        _menu = new Popup
        {
            Child = card, PlacementTarget = _launcher, Placement = PlacementMode.Top, StaysOpen = false,
            AllowsTransparency = true, VerticalOffset = 8, HorizontalOffset = -12,
        };
        _menu.Opened += (_, _) => { _search.Focus(); Keyboard.Focus(_search); };

        _tick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _ = Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _ = Refresh(); _tick.Start(); }
            else { _tick.Stop(); _menu.IsOpen = false; }
        };
    }

    /// <summary>The window buttons, front first; for the gate.</summary>
    internal IReadOnlyList<Button> WindowButtons => [.. _windows.Children.OfType<Button>()];

    /// <summary>What the window buttons read right now, front first; for the gate.</summary>
    internal IReadOnlyList<string> Titles => [.. _windows.Children.OfType<Button>().Select(b => (string)b.Tag)];

    /// <summary>Reads the workspace's windows off the UI thread and redraws the buttons if they changed.</summary>
    internal async Task Refresh()
    {
        _clock.Text = DateTime.Now.ToShortTimeString();
        if (_refreshing || _runtime()?.Plane is not { } plane) return;
        _refreshing = true;
        try
        {
            AgentWindow[] shown = await Task.Run(() =>
            {
                try
                {
                    AgentWindow[] found = [.. plane.Windows().Where(w => w.Title.Trim().Length > 0)];
                    // Icons are read here, off the UI thread: asking a window can wait on it.
                    foreach (AgentWindow window in found)
                        if (!_icons.ContainsKey(window.Handle)) _icons[window.Handle] = Icon(window.Handle);
                    return found;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return []; }
            });
            string key = string.Join("\n", shown.Select(w => w.Handle + "|" + w.Title));
            if (key == _shown) return;
            _shown = key;
            _windows.Children.Clear();
            foreach (nint gone in _icons.Keys.Where(h => shown.All(w => w.Handle != h)).ToList()) _icons.TryRemove(gone, out _);
            bool roomy = Height >= 36;
            for (int i = 0; i < shown.Length; i++)
            {
                AgentWindow window = shown[i];
                // Like Windows' own taskbar: the app's icon, and on the roomier workspace page a
                // short name beside it. The whole title is the tooltip.
                var face = new StackPanel { Orientation = Orientation.Horizontal };
                if (_icons.GetValueOrDefault(window.Handle) is { } icon)
                    face.Children.Add(new Image { Source = icon, Width = roomy ? 18 : 16, Height = roomy ? 18 : 16 });
                if (roomy || face.Children.Count == 0)
                {
                    var title = new TextBlock
                    {
                        Text = Short(window.Title), FontSize = 12, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(face.Children.Count > 0 ? 7 : 0, 0, 0, 0),
                    };
                    title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                    face.Children.Add(title);
                }
                Button button = Flat(face, "Bring " + window.Title + " to the front");
                button.Tag = window.Title;
                button.ToolTip = window.Title;
                button.Margin = new Thickness(0, 0, 4, 0);
                // The front window is marked, as a taskbar marks the active app.
                if (i == 0) button.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
                nint handle = window.Handle;
                button.Click += (_, _) => Front(handle);
                _windows.Children.Add(button);
            }
        }
        finally { _refreshing = false; }
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<nint, ImageSource?> _icons = new();

    /// <summary>"Untitled - Notepad" reads "Untitled", a page reads its own title, a console its program.</summary>
    internal static string Short(string title)
    {
        string text = title.Trim();
        int dash = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) text = text[..dash].Trim();
        if (text.Length > 3 && text[1] == ':' && text.IndexOfAny(System.IO.Path.GetInvalidFileNameChars().Where(c => c is not ('\\' or ':')).ToArray()) < 0)
            text = System.IO.Path.GetFileNameWithoutExtension(text);
        return text.Length > 0 ? text : title;
    }

    /// <summary>The window's own icon, the way the Windows taskbar finds it: what the window says,
    /// then its class's icon. Null when it has none. Frozen, so it can come from a pool thread.</summary>
    static ImageSource? Icon(nint window)
    {
        nint handle = 0;
        foreach (int kind in new[] { 2, 0, 1 })   // ICON_SMALL2, ICON_SMALL, ICON_BIG
        {
            if (SendMessageTimeout(window, 0x007F, kind, 0, 0x0002, 100, out handle) != 0 && handle != 0) break;   // WM_GETICON, abort if hung
            handle = 0;
        }
        if (handle == 0) handle = GetClassLongPtr(window, -34);   // GCLP_HICONSM
        if (handle == 0) handle = GetClassLongPtr(window, -14);   // GCLP_HICON
        if (handle == 0) return null;
        try
        {
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    static extern nint GetClassLongPtr(nint window, int index);

    /// <summary>Brings one of the workspace's windows to the front, the owner's hand on the wheel.</summary>
    internal void Front(nint window)
    {
        if (_runtime()?.Computer is not { } computer) return;
        _ownerActed();
        _ = Task.Run(() => computer.Arrange(window, WindowArrangement.Front))
            .ContinueWith(_ => Dispatcher.BeginInvoke(() => { _shown = string.Empty; _ = Refresh(); }));
    }

    /// <summary>Starts an app inside the workspace, never on the owner's own desktop.</summary>
    internal async Task<int> Launch(WorkspacePrograms.Shortcut app)
    {
        if (_launching) return 0;
        if (WorkspacePrograms.ShellOnly(app.Target))
        {
            LaunchMessage(app.Name + " opens through Windows on your desktop. Choose another app for this workspace.");
            return 0;
        }
        if (_runtime()?.Plane is not { } plane)
        {
            LaunchMessage("This workspace has stopped. It starts again when an agent needs it.");
            return 0;
        }
        _ownerActed();
        _launching = true;
        _apps.IsEnabled = false;
        LaunchMessage("Opening " + app.Name + "...");
        try
        {
            int pid = await Task.Run(() => plane.Open(app.Target, app.Arguments.Length > 0 ? app.Arguments : null, quiet: false, out _));
            if (pid > 0) _menu.IsOpen = false;
            else LaunchMessage("Couldn’t open " + app.Name + " in this workspace. Try another app.");
            return pid;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or System.IO.IOException or UnauthorizedAccessException)
        {
            LaunchMessage("Couldn’t open " + app.Name + " in this workspace. Try again.");
            return 0;
        }
        finally { _launching = false; _apps.IsEnabled = true; }
    }

    void LaunchMessage(string text)
    {
        _launchMessage.Text = text;
        _launchMessage.Visibility = Visibility.Visible;
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(_launchMessage)
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    async void OpenMenu()
    {
        _launchMessage.Visibility = Visibility.Collapsed;
        _search.Text = string.Empty;
        _menu.IsOpen = true;
        if (_all.Count > 0) { Filter(); return; }
        _loading.Visibility = Visibility.Visible;
        _all = await Task.Run(() =>
        {
            try { return WorkspacePrograms.Apps(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return []; }
        });
        _loading.Visibility = Visibility.Collapsed;
        Filter();
    }

    void Filter()
    {
        string text = _search.Text.Trim();
        var matches = _all.Where(app => text.Length == 0 || app.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(app => text.Length > 0 && !app.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _apps.ItemsSource = matches;
        if (matches.Count > 0) _apps.SelectedIndex = 0;
        if (_all.Count > 0) _loading.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_all.Count > 0 && matches.Count == 0) _loading.Text = "No app called that";
        else _loading.Text = "Finding apps...";
    }

    void SearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _apps.SelectedItem is WorkspacePrograms.Shortcut app:
                _ = Launch(app);
                e.Handled = true;
                break;
            case Key.Down when _apps.Items.Count > 0:
                _apps.SelectedIndex = Math.Min(_apps.Items.Count - 1, _apps.SelectedIndex + 1);
                _apps.ScrollIntoView(_apps.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when _apps.Items.Count > 0:
                _apps.SelectedIndex = Math.Max(0, _apps.SelectedIndex - 1);
                _apps.ScrollIntoView(_apps.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                _menu.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>The launcher's glyph: four rounded squares, the usual sign for "apps".</summary>
    static FrameworkElement Apps(double height)
    {
        double size = height >= 36 ? 14 : 12;
        var glyph = new Path
        {
            Width = size, Height = size, Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M1,0 H5 A1,1 0 0 1 6,1 V5 A1,1 0 0 1 5,6 H1 A1,1 0 0 1 0,5 V1 A1,1 0 0 1 1,0 Z "
                + "M9,0 H13 A1,1 0 0 1 14,1 V5 A1,1 0 0 1 13,6 H9 A1,1 0 0 1 8,5 V1 A1,1 0 0 1 9,0 Z "
                + "M1,8 H5 A1,1 0 0 1 6,9 V13 A1,1 0 0 1 5,14 H1 A1,1 0 0 1 0,13 V9 A1,1 0 0 1 1,8 Z "
                + "M9,8 H13 A1,1 0 0 1 14,9 V13 A1,1 0 0 1 13,14 H9 A1,1 0 0 1 8,13 V9 A1,1 0 0 1 9,8 Z"),
        };
        glyph.SetResourceReference(Shape.FillProperty, "AccentBrush");
        return glyph;
    }

    static readonly ControlTemplate FlatTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Face" Background="{TemplateBinding Background}" CornerRadius="6"
                  Padding="{TemplateBinding Padding}" BorderThickness="1" BorderBrush="Transparent">
            <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Face" Property="Background" Value="{DynamicResource HoverBrush}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Face" Property="Background" Value="{DynamicResource PressedBrush}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
              <Setter TargetName="Face" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    static Button Flat(object content, string name)
    {
        var button = new Button
        {
            Content = content, Template = FlatTemplate, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4),
            FocusVisualStyle = null,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }
}

internal static class VisualParents
{
    /// <summary>The nearest ancestor of a type, walking the visual tree up from an element.</summary>
    internal static T? FindParent<T>(this DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? at = start; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T found) return found;
        return null;
    }
}
