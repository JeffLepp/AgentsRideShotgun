using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
    static StackPanel RowText(string label, string? hint = null, Inline? tag = null)
    {
        var stack = new StackPanel();
        var head = new TextBlock();
        head.SetResourceReference(StyleProperty, "RowLabel");
        head.Inlines.Add(new Run(label));
        if (tag is not null) head.Inlines.Add(tag);
        stack.Children.Add(head);
        if (hint is not null) stack.Children.Add(Styled(hint, "RowHint"));
        return stack;
    }

    static Run DefaultTag()
    {
        var run = new Run(" Default");
        run.SetResourceReference(FrameworkContentElement.StyleProperty, "DefaultTag");
        return run;
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
    /// rows are, plain or radio choices - and none above the first.</summary>
    static Border Group(params UIElement[] rows)
    {
        var card = new Border();
        card.SetResourceReference(StyleProperty, "Card");
        var stack = new StackPanel();
        stack.PreviewKeyDown += RadioArrowKeys;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == 0) { stack.Children.Add(rows[i]); continue; }
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = rows[i] };
            line.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
            stack.Children.Add(line);
        }
        card.Child = stack;
        return card;
    }

    // Arrow keys move the checked choice inside whichever radio group has focus (reference .radio),
    // the same way MoveChoice already does for the category list. Left/Right are left alone, as
    // there too, since these are vertical lists.
    static void RadioArrowKeys(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) return;
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) return;
        if (Keyboard.FocusedElement is not RadioButton current) return;
        var group = new List<RadioButton>();
        foreach (UIElement child in ((StackPanel)sender).Children)
        {
            UIElement row = child is Border { Child: UIElement inner } ? inner : child;
            if (row is RadioButton radio && radio.GroupName == current.GroupName) group.Add(radio);
        }
        if (group.Count > 1) MoveChoice(group, e);
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
                var header = new Grid { Margin = new Thickness(2, 0, 0, 8) };
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(text);
                Grid.SetColumn(action, 1);
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

    /// <summary>A card's worth of radio choices (reference .radio), one per row, arrow keys and Tab
    /// already wired the way the category list works.</summary>
    RadioButton[] RadioRows<T>(string claim, string groupName,
        (T Value, string Label, string? Hint, bool Default)[] options,
        Func<AppSettings, T> read, Func<AppSettings, T, AppSettings> write) where T : notnull
    {
        var buttons = new RadioButton[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var (value, label, hint, isDefault) = options[i];
            var button = new RadioButton
            {
                GroupName = groupName,
                Tag = value,
                Content = RowText(label, hint, isDefault ? DefaultTag() : null),
            };
            button.SetResourceReference(StyleProperty, "ChoiceRadio");
            AutomationProperties.SetName(button, label);
            buttons[i] = button;
        }
        foreach (RadioButton button in buttons)
            button.Checked += (_, _) => { if (!_following) Write(s => write(s, (T)button.Tag!)); };
        void Show(AppSettings s)
        {
            T current = read(s);
            foreach (RadioButton button in buttons) button.IsChecked = Equals(button.Tag, current);
            TabToChecked(buttons);
        }
        _followers.Add(Show);
        _bound.Add(new Bound(claim, buttons[0], options.Select(o => (object)o.Value).ToList(),
            s => read(s), (s, v) => write(s, (T)v),
            v => { RadioButton? match = Array.Find(buttons, b => Equals(b.Tag, v)); if (match is not null) match.IsChecked = true; },
            () => Array.Find(buttons, b => b.IsChecked == true)?.Tag,
            Show));
        return buttons;
    }

    /// <summary>A rebindable shortcut row (reference .key): the row and the control that shows and
    /// records it, so two rows on the same page can refuse each other's combination.</summary>
    (Border Row, SettingsShortcut Control) ShortcutRow(string claim, string label,
        Func<AppSettings, string> read, Func<AppSettings, string, AppSettings> write)
    {
        var shortcut = new SettingsShortcut(read(AppSettingsStore.Current));
        shortcut.SetResourceReference(StyleProperty, "ShortcutButton");
        AutomationProperties.SetName(shortcut, label);
        shortcut.Chosen += keys => { if (!_following) Write(s => write(s, keys)); };
        void Show(AppSettings s) => shortcut.Set(read(s));
        _followers.Add(Show);
        _bound.Add(new Bound(claim, shortcut, [], s => read(s), (s, v) => write(s, (string)v),
            _ => { }, () => shortcut.Keys, Show));
        return (Row(RowText(label), shortcut), shortcut);
    }

    /// <summary>A destructive or slow action that asks first: the row's control starts as one button
    /// and turns into the question with a real Yes/Cancel, never a modal dialog the gate cannot see
    /// past.</summary>
    static ContentControl Confirm(string label, string prompt, Action action, bool danger)
    {
        var host = new ContentControl { Focusable = false, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Right };
        void Ask()
        {
            var text = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right };
            text.SetResourceReference(StyleProperty, "RowHint");
            var yes = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
            yes.SetResourceReference(StyleProperty, "DeskButton");
            if (danger) yes.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerInkBrush");
            yes.Click += (_, _) => action();
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
            button.SetResourceReference(StyleProperty, "DeskButton");
            if (danger) button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "DangerInkBrush");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => Ask();
            host.Content = button;
        }
        Ready();
        return host;
    }

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? value.ToString("0") : value.ToString("0.0")) + " " + units[unit];
    }
}
