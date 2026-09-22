using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// Settings, hosted by the hub window below its title bar when it is 1200 DIP wide. Every control
/// reads and writes <see cref="AppSettingsStore"/> and follows changes made anywhere else; nothing
/// here keeps its own copy of a setting.
/// </summary>
public partial class SettingsView : UserControl
{
    static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["general"] = "General", ["agents"] = "Agents", ["accounts"] = "Accounts", ["history"] = "History & privacy",
    };

    // Four categories replaced ten in the cut round (WAVE1B.md C.1): a caller that still passes one
    // of the retired ids lands on the page that now holds what survived from it, rather than
    // nowhere. Control's one surviving row (Pause every agent) moved to Agents; Corner window's one
    // surviving row (Show the corner window) moved to General; Privacy & safety's one surviving row
    // (Delete all Deskweave data) moved to History & privacy, alongside About's Licenses and data
    // folder, which moved to General. Notifications and Performance kept nothing, so, like any other
    // unknown id, they fall through to the plain default below.
    static readonly Dictionary<string, string> LegacyCategory = new(StringComparer.Ordinal)
    {
        ["browser"] = "accounts",
        ["control"] = "agents",
        ["corner"] = "general",
        ["privacy"] = "history",
        ["about"] = "general",
    };

    // A category whose page can be entirely SettingsFeatures-gated:
    // once every row on it is hidden, the row it would show is only a bare
    // card, so the category itself leaves the nav until the flag it names turns on. History &
    // privacy is not here: Save screenshots may hide, but Storage and Delete all Deskweave data
    // always show, so its page and nav row are never empty.
    static readonly Dictionary<string, Func<bool>> CategoryGate = new(StringComparer.Ordinal)
    {
        ["accounts"] = () => SettingsFeatures.Accounts,
    };

    readonly List<Bound> _bound = [];
    readonly List<Action<AppSettings>> _followers = [];
    // Run and cleared each time Show() replaces the page, since a control built for one page (a
    // shortcut row following ModuleEntry.PauseShortcutTakenChanged) is not guaranteed an Unloaded event
    // just because Page.Content moved on to a different tree - Page itself, and this view, stay
    // loaded throughout, so nothing here is ever disconnected from a live PresentationSource.
    readonly List<Action> _cleanup = [];
    readonly bool _card;
    bool _listening;
    // True while controls are being set from the store, so they do not write it back.
    bool _following;

    public SettingsView() : this(card: false) { }

    /// <summary>With <paramref name="card"/>, the hub's narrow card: a home page of quick tiles and
    /// categories, each page sliding in over it (SettingsView.Card.cs).</summary>
    public SettingsView(bool card)
    {
        _card = card;
        InitializeComponent();
        if (card) BuildCard();
        // Bubbling, so an open dropdown or a shortcut being recorded takes Escape first.
        AddHandler(KeyDownEvent, new KeyEventHandler(EscapeGoesBack));
        Loaded += (_, _) => { Listen(true); Follow(AppSettingsStore.Current); };
        Unloaded += (_, _) => Listen(false);
        IsVisibleChanged += (_, _) => { if (IsVisible) Follow(AppSettingsStore.Current); };
        // The host can focus the view itself; the chosen category takes it from there.
        GotKeyboardFocus += (_, e) => { if (ReferenceEquals(e.NewFocus, this)) Selected()?.Focus(); };
        Show("general");
        if (card) ShowCardHome(animate: false);
    }

    /// <summary>"Workspaces" at the top of the list was chosen, or Escape pressed.</summary>
    public event Action? BackRequested;

    /// <summary>The category on screen.</summary>
    internal string Category { get; private set; } = "general";

    /// <summary>The controls on the current page that are bound to one setting each.</summary>
    internal IReadOnlyList<Bound> BoundControls => _bound;

    /// <summary>The categories the nav shows right now - every one whose page is not entirely hidden
    /// behind an off <see cref="SettingsFeatures"/> flag.</summary>
    internal IReadOnlyList<string> AvailableCategories =>
        Nav.Children.OfType<RadioButton>().Where(item => item.Visibility == Visibility.Visible).Select(item => (string)item.Tag).ToList();

    // Notifications and Browser & accounts can each end up with nothing wired on yet (the latter
    // down to its banner); re-run on every Show() since a gate can flip a flag and re-show without
    // building a whole new SettingsView.
    void RefreshNavAvailability()
    {
        foreach (RadioButton item in Nav.Children.OfType<RadioButton>())
            if (CategoryGate.TryGetValue((string)item.Tag, out Func<bool>? on))
                item.Visibility = on() ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens a category: general, agents, accounts or history. A retired id
    /// (<see cref="LegacyCategory"/>) opens whatever page now holds its content; any other unknown
    /// id opens General.</summary>
    public void Show(string category)
    {
        RefreshNavAvailability();
        string id = Titles.ContainsKey(category) ? category
            : LegacyCategory.TryGetValue(category, out string? mapped) && Titles.ContainsKey(mapped) ? mapped
            : "general";
        Category = id;
        foreach (RadioButton item in Nav.Children) if ((string)item.Tag == id) item.IsChecked = true;
        TabToChecked(Nav.Children.OfType<RadioButton>().ToList());
        PageTitle.Text = Titles[id];
        _bound.Clear();
        _followers.Clear();
        foreach (Action cleanup in _cleanup) cleanup();
        _cleanup.Clear();
        Page.Content = Build(id);
        Follow(AppSettingsStore.Current);
        Scroller.ScrollToTop();
        if (_card) SlidePageIn();
        else if (IsLoaded && SystemParameters.ClientAreaAnimation)
            Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id } && id != Category) Show(id);
    }

    void Nav_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) return;
        MoveChoice(Nav.Children.OfType<RadioButton>().ToList(), e);
    }

    RadioButton? Selected() => Nav.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true);

    void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    void EscapeGoesBack(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        GoBack();
    }

    /// <summary>One step back: in the card, from a page to the card's home; otherwise out of Settings.</summary>
    internal void GoBack()
    {
        if (_card && !AtCardHome) ShowCardHome(animate: true);
        else BackRequested?.Invoke();
    }

    /// <summary>Arrow keys, Home and End move the choice inside a group of radio buttons, as in Windows.</summary>
    internal static void MoveChoice(IReadOnlyList<RadioButton> group, KeyEventArgs e)
    {
        int step = e.Key switch { Key.Up or Key.Left => -1, Key.Down or Key.Right => 1, _ => 0 };
        if (step == 0 && e.Key is not (Key.Home or Key.End)) return;
        var choices = group.Where(choice => choice.IsEnabled && choice.Visibility == Visibility.Visible).ToList();
        if (choices.Count == 0) return;
        int at = choices.FindIndex(choice => choice.IsKeyboardFocused);
        if (at < 0) at = choices.FindIndex(choice => choice.IsChecked == true);
        int next = e.Key == Key.Home ? 0 : e.Key == Key.End ? choices.Count - 1 : Math.Clamp(at + step, 0, choices.Count - 1);
        choices[next].IsChecked = true;
        choices[next].Focus();
        e.Handled = true;
    }

    /// <summary>Tab enters a group of radio buttons once, at the chosen one.</summary>
    static void TabToChecked(IReadOnlyList<RadioButton> group)
    {
        bool any = group.Any(choice => choice.IsChecked == true);
        for (int i = 0; i < group.Count; i++) group[i].IsTabStop = any ? group[i].IsChecked == true : i == 0;
    }

    void Listen(bool on)
    {
        if (on == _listening) return;
        _listening = on;
        if (on) AppSettingsStore.Changed += StoreChanged;
        else AppSettingsStore.Changed -= StoreChanged;
    }

    // Raised on whichever thread made the change.
    void StoreChanged(AppSettings settings) =>
        Dispatcher.BeginInvoke(() => { if (_listening) Follow(AppSettingsStore.Current); });

    void Follow(AppSettings settings)
    {
        _following = true;
        try
        {
            foreach (Bound bound in _bound) bound.Show(settings);
            foreach (Action<AppSettings> follower in _followers) follower(settings);
            if (_card) RefreshCardHome(settings);
        }
        finally { _following = false; }
    }

    void Write(Func<AppSettings, AppSettings> change)
    {
        if (!_following) AppSettingsStore.Update(change);
    }
}

/// <summary>
/// One control bound to one setting. <see cref="Choose"/> changes the control the way a click does
/// (and so writes the store); <see cref="Shown"/> is what it shows; <see cref="Values"/> are the
/// choices it offers.
/// </summary>
internal sealed record Bound(string Label, FrameworkElement Control, IReadOnlyList<object> Values,
    Func<AppSettings, object> Read, Func<AppSettings, object, AppSettings> Write,
    Action<object> Choose, Func<object?> Shown, Action<AppSettings> Show);

/// <summary>A dropdown entry: the value it stands for and the words it shows.</summary>
internal sealed record Choice(object Value, string Text)
{
    public override string ToString() => Text;
}
