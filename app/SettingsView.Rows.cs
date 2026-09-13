using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// The building blocks every settings page is made of: a card of rows (reference .card/.srw), the
/// three control kinds a row can end in (toggle, dropdown, a group of radio choices), a shortcut row
/// and a two-step confirm. Each wiring helper writes <see cref="AppSettingsStore"/> on a real change,
/// registers a follower so <see cref="Follow"/> can show a change made elsewhere, and adds a
/// <see cref="Bound"/> entry so the UI gate can drive it the way a click would.
/// </summary>
public partial class SettingsView
{
    static TextBlock Styled(string text, string style)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    /// <summary>The label (and optional "Default" tag) over an optional muted hint, as every row's
    /// left side is built (reference .srw .t).</summary>
    static StackPanel RowText(string label, string? hint = null)
    {
        var stack = new StackPanel();
        var head = new TextBlock();
        head.SetResourceReference(StyleProperty, "RowLabel");
        head.Inlines.Add(new Run(label));
        stack.Children.Add(head);
        if (hint is not null) stack.Children.Add(Styled(hint, "RowHint"));
        return stack;
    }


    /// <summary>One row in a card (reference .srw): optional leading icon, the text, and a control
    /// on the right with an 8 DIP gap. The hairline between rows is added by <see cref="Group"/>,
    /// not here, so a row looks the same whether it is a plain row or a radio choice.</summary>
    static Border Row(UIElement text, FrameworkElement? control = null, UIElement? icon = null)
    {
        var grid = new Grid();
        if (icon is not null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (control is not null) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int column = 0;
        if (icon is not null)
        {
            if (icon is FrameworkElement iconElement) { iconElement.Margin = new Thickness(0, 0, 12, 0); iconElement.VerticalAlignment = VerticalAlignment.Center; }
            Grid.SetColumn(icon, column++);
            grid.Children.Add(icon);
        }
        if (text is FrameworkElement textElement) textElement.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, column++);
        grid.Children.Add(text);
        if (control is not null)
        {
            control.Margin = new Thickness(8, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }
        var border = new Border { Child = grid, BorderThickness = new Thickness(0) };
        border.SetResourceReference(StyleProperty, "SettingRow");
        return border;
    }

    /// <summary>A card of rows (reference .card), with a hairline between every pair - whatever the
    /// rows are, plain or radio choices - and none above the first. A row a <see cref="SettingsFeatures"/>
    /// flag starts collapsed is skipped for this: it takes no space either way, but a hairline drawn
    /// above one would still stretch the card's width and leave a bare line where nothing shows.</summary>
    static Border Group(params UIElement[] rows)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        var stack = new StackPanel();
        bool any = false;
        foreach (UIElement row in rows)
        {
            bool hidden = row is FrameworkElement { Visibility: Visibility.Collapsed };
            if (hidden || !any) { stack.Children.Add(row); if (!hidden) any = true; continue; }
            // The hairline is drawn in the seam, not added on top of it - CSS border-box keeps a
            // bordered row's outer height the same as an unbordered one. A WPF Border stacks its
            // BorderThickness outside its child instead, so pull the extra 1 DIP back with a
            // matching negative margin or every row below the first drifts down by 1 DIP.
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, -1, 0, 0), Child = row };
            line.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
            stack.Children.Add(line);
        }
        card.Child = stack;
        return card;
    }


    /// <summary>A section: an optional label (with an optional action button at its right, reference
    /// .lbl .btn) over one card, 22 DIP below the section before it.</summary>
    static StackPanel Section(string? label, Border card, UIElement? action = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        if (label is not null)
        {
            var text = Styled(label, "SectionLabel");
            if (action is null) { text.Margin = new Thickness(2, 0, 0, 8); stack.Children.Add(text); }
            else
            {
                // .lbl is a flex row with align-items:center (reference CSS) - the label and its
                // button share a middle, not a top, or the label sits high against the taller button.
                var header = new Grid { Margin = new Thickness(2, 0, 0, 8) };
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                text.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(text);
                Grid.SetColumn(action, 1);
                if (action is FrameworkElement actionElement) actionElement.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(action);
                stack.Children.Add(header);
            }
        }
        stack.Children.Add(card);
        return stack;
    }

    /// <summary>A toggle switch (reference .sw) bound to one bool setting.</summary>
    CheckBox Toggle(string claim, Func<AppSettings, bool> read, Func<AppSettings, bool, AppSettings> write)
    {
        var box = new CheckBox();
        box.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(box, claim);
        RoutedEventHandler changed = (_, _) =>
        {
            if (_following) return;
            Write(s => write(s, box.IsChecked == true));
        };
        box.Checked += changed;
        box.Unchecked += changed;
        void Show(AppSettings s) => box.IsChecked = read(s);
        _followers.Add(Show);
        _bound.Add(new Bound(claim, box, [true, false],
            s => read(s), (s, v) => write(s, (bool)v),
            v => box.IsChecked = (bool)v,
            () => box.IsChecked == true,
            Show));
        return box;
    }

    /// <summary>A dropdown (reference .dd) bound to one setting, offered as the exact choices given -
    /// which must be no more than <c>AppSettings.Sane</c> allows for that setting.</summary>
    ComboBox Dropdown<T>(string claim, IReadOnlyList<Choice> choices, Func<AppSettings, T> read,
        Func<AppSettings, T, AppSettings> write) where T : notnull
    {
        var box = new ComboBox { ItemsSource = choices };
        box.SetResourceReference(StyleProperty, "Dropdown");
        AutomationProperties.SetName(box, claim);
        box.SelectionChanged += (_, _) =>
        {
            if (_following || box.SelectedItem is not Choice choice) return;
            Write(s => write(s, (T)choice.Value));
        };
        void Show(AppSettings s) => box.SelectedItem = choices.FirstOrDefault(c => Equals(c.Value, read(s)));
        _followers.Add(Show);
        _bound.Add(new Bound(claim, box, choices.Select(c => c.Value).ToList(),
            s => read(s), (s, v) => write(s, (T)v),
            v => box.SelectedItem = choices.FirstOrDefault(c => Equals(c.Value, v)),
            () => (box.SelectedItem as Choice)?.Value,
            Show));
        return box;
    }


    /// <summary>A rebindable shortcut row (reference .key): the row and the control that shows and
    /// records it, so two rows on the same page can refuse each other's combination. When
    /// <paramref name="taken"/> is given, "Another app is using this shortcut." shows under the row
    /// exactly while it reports true (reference: the same style as the Start with Windows error),
    /// following <see cref="ModuleEntry.ShortcutsTakenChanged"/>. Show() replacing the page does not
    /// raise this row's Unloaded - Page and this view both stay connected to the same
    /// PresentationSource throughout, so nothing here is actually removed from a live tree - so the
    /// follower is unsubscribed explicitly through <c>_cleanup</c>, the same call Show() makes before
    /// building the next page; Unloaded is kept alongside it only for the whole view being torn
    /// down.</summary>
    (Border Row, SettingsShortcut Control) ShortcutRow(string claim, string label,
        Func<AppSettings, string> read, Func<AppSettings, string, AppSettings> write, Func<bool>? taken = null)
    {
        var shortcut = new SettingsShortcut(read(AppSettingsStore.Current));
        shortcut.SetResourceReference(StyleProperty, "ShortcutButton");
        AutomationProperties.SetName(shortcut, label);
        shortcut.Chosen += keys => { if (!_following) Write(s => write(s, keys)); };
        void Show(AppSettings s) => shortcut.Set(read(s));
        _followers.Add(Show);
        _bound.Add(new Bound(claim, shortcut, [], s => read(s), (s, v) => write(s, (string)v),
            _ => { }, () => shortcut.Keys, Show));

        UIElement text;
        if (taken is null) text = RowText(label);
        else
        {
            var error = Styled("Another app is using this shortcut.", "RowError");
            void RefreshError() => error.Visibility = taken() ? Visibility.Visible : Visibility.Collapsed;
            RefreshError();
            ModuleEntry.ShortcutsTakenChanged += RefreshError;
            _cleanup.Add(() => ModuleEntry.ShortcutsTakenChanged -= RefreshError);
            var stack = new StackPanel();
            stack.Children.Add(RowText(label));
            stack.Children.Add(error);
            // Belt and suspenders for the one case _cleanup does not cover: this view itself torn
            // down (not just Show() moving to another page) while this row is still the one showing.
            stack.Unloaded += (_, _) => ModuleEntry.ShortcutsTakenChanged -= RefreshError;
            text = stack;
        }
        return (Row(text, shortcut), shortcut);
    }

    /// <summary>A destructive or slow action that asks first: the row's control starts as one button
    /// (a plain <c>DeskButton</c>, or a <c>LinkButton</c> for a Storage kind's Clear) and turns into
    /// the question with a real Yes/Cancel, never a modal dialog the gate cannot see past. The prompt
    /// is read lazily, at the moment of asking, so a Storage row can name the size it holds right
    /// then rather than the one it had when the page was built. <paramref name="enabled"/> and
    /// <paramref name="disabledTooltip"/> are Scratch's own Clear disabled while its computer runs
    /// (WAVE1B.md C.5); every other caller leaves them null, always enabled.</summary>
    static ContentControl Confirm(string label, Func<string> prompt, Action action, bool danger,
        string style = "DeskButton", Func<bool>? enabled = null, string? disabledTooltip = null)
    {
        var host = new ContentControl { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
        void Ask()
        {
            var text = new TextBlock { Text = prompt(), TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right };
            text.SetResourceReference(StyleProperty, "RowHint");
            var yes = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
            yes.SetResourceReference(StyleProperty, style);
            if (danger) yes.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerInkBrush");
            yes.Click += (_, _) =>
            {
                if (enabled?.Invoke() ?? true) action();
                Ready();
            };
            var no = new Button { Content = "Cancel" };
            no.SetResourceReference(StyleProperty, "DeskButton");
            no.Click += (_, _) => Ready();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(text);
            stack.Children.Add(buttons);
            host.Content = stack;
        }
        void Ready()
        {
            var button = new Button { Content = label };
            button.SetResourceReference(StyleProperty, style);
            if (danger) button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerInkBrush");
            AutomationProperties.SetName(button, label);
            bool on = enabled?.Invoke() ?? true;
            button.IsEnabled = on;
            button.ToolTip = !on ? disabledTooltip : null;
            button.Click += (_, _) =>
            {
                if (enabled?.Invoke() ?? true) Ask();
                else Ready();
            };
            host.Content = button;
        }
        Ready();
        return host;
    }

    /// <summary>The 6 DIP meter above the Storage rows (reference .meter): a hairline track, radius
    /// 3, one segment per <see cref="StorageKind"/> that has a known, positive size, each as wide as
    /// its share of the total. Rebuilt whenever the sizes change, since a WPF Grid's star columns
    /// cannot be re-weighted any other way.</summary>
    static Border StorageMeter(Grid segments)
    {
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0), Child = segments };
        segments.SizeChanged += (_, _) => segments.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, segments.ActualWidth, segments.ActualHeight), 3, 3);
        track.SetResourceReference(Border.BackgroundProperty, "HairlineBrush");
        return track;
    }

    static void FillStorageMeter(Grid segments, IReadOnlyList<StorageKind> kinds, IReadOnlyList<long?> measured)
    {
        segments.Children.Clear();
        segments.ColumnDefinitions.Clear();
        int column = 0;
        for (int i = 0; i < kinds.Count; i++)
        {
            if (measured[i] is not { } bytes || bytes <= 0) continue;
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bytes, GridUnitType.Star) });
            var fill = new Border { Opacity = kinds[i].MeterOpacity };
            fill.SetResourceReference(Border.BackgroundProperty, kinds[i].MeterBrush);
            Grid.SetColumn(fill, column++);
            segments.Children.Add(fill);
        }
    }
}
