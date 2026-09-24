using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

/// <summary>
/// A shortcut shown as key caps. Click it, or press Space or Enter on it, then
/// press a combination with at least one modifier; Escape, Tab or clicking elsewhere cancels.
/// </summary>
public sealed class SettingsShortcut : Button
{
    public static readonly DependencyProperty RecordingProperty = DependencyProperty.Register(
        nameof(Recording), typeof(bool), typeof(SettingsShortcut), new PropertyMetadata(false));

    string _keys;

    internal SettingsShortcut(string keys)
    {
        _keys = keys;
        Draw(keys);
    }

    /// <summary>Waiting for the owner to press a combination.</summary>
    public bool Recording { get => (bool)GetValue(RecordingProperty); private set => SetValue(RecordingProperty, value); }

    internal string Keys => _keys;

    /// <summary>A combination was pressed and accepted, as WorkspaceHotkey writes it ("Ctrl+Alt+P").</summary>
    internal event Action<string>? Chosen;

    /// <summary>False refuses a combination, such as the one the other shortcut already uses.</summary>
    internal Func<string, bool> Accepts { get; set; } = _ => true;

    /// <summary>Shows a shortcut the store now holds.</summary>
    internal void Set(string keys)
    {
        _keys = keys;
        if (!Recording) Draw(keys);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (Recording) return;
        Recording = true;
        Focus();
        Prompt(ModifierKeys.None);
    }

    internal void Cancel()
    {
        if (!Recording) return;
        Recording = false;
        Draw(_keys);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!Recording) { base.OnPreviewKeyDown(e); return; }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) { Cancel(); return; }
        e.Handled = true;
        Record(Keyboard.Modifiers, key);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!Recording) { base.OnPreviewKeyUp(e); return; }
        e.Handled = true;
        Prompt(Keyboard.Modifiers);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Cancel();
    }

    /// <summary>One key press while recording. True when it became the shortcut.</summary>
    internal bool Record(ModifierKeys held, Key key)
    {
        if (!Recording) return false;
        if (key == Key.Escape && held == ModifierKeys.None) { Cancel(); return false; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
        {
            Prompt(held);
            return false;
        }
        string keys = WorkspaceHotkey.Format(held, key);
        if (!WorkspaceHotkey.Parse(keys, out _, out _) || !Accepts(keys)) { Prompt(held); return false; }
        _keys = keys;
        Recording = false;
        Draw(keys);
        Chosen?.Invoke(keys);
        return true;
    }

    void Draw(string keys)
    {
        var caps = new StackPanel { Orientation = Orientation.Horizontal };
        string[] parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) caps.Children.Add(Plus());
            var cap = new ContentControl { Content = parts[i], Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0) };
            cap.SetResourceReference(StyleProperty, "KeyCap");
            caps.Children.Add(cap);
        }
        Content = caps;
        AutomationProperties.SetItemStatus(this, keys);
    }

    // While recording: the modifiers held so far, or a prompt until there are some.
    void Prompt(ModifierKeys held)
    {
        if (held == ModifierKeys.None)
        {
            Content = new TextBlock { Text = "Press a shortcut", LineHeight = 19.375, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            return;
        }
        string modifiers = WorkspaceHotkey.Format(held, Key.None);
        Draw(modifiers[..modifiers.LastIndexOf('+')]);
        ((StackPanel)Content).Children.Add(Plus());
    }

    static TextBlock Plus() => new()
    {
        Text = "+", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        LineHeight = 19.375, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
}
