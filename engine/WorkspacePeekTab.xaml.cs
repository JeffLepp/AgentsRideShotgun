using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>A small, non-activating way back to a tucked workspace. No preview capture or global mouse hook.</summary>
internal partial class WorkspacePeekTab : Window
{
    readonly DispatcherTimer _hover;
    bool _hovering;
    internal event Action? RevealRequested;

    internal WorkspacePeekTab()
    {
        InitializeComponent();
        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _hover.Tick += (_, _) => { _hover.Stop(); if (IsVisible && _hovering) RevealRequested?.Invoke(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { _hovering = false; _hover.Stop(); } };
        Closed += (_, _) => _hover.Stop();
    }

    internal void Place(Rect work, Rect card, string name)
    {
        Rect tab = WorkspacePeekPlacement.EdgeTab(work, card);
        Left = tab.Left; Top = tab.Top; Width = tab.Width; Height = tab.Height;
        bool right = tab.Left > work.Left;
        Direction.Data = Geometry.Parse(right ? "M 5,1 L 1,5 L 5,9" : "M 1,1 L 5,5 L 1,9");
        RevealButton.ToolTip = "Show " + name;
        AutomationProperties.SetName(RevealButton, "Show " + name + " workspace");
    }

    void Reveal_Click(object sender, RoutedEventArgs e) { _hover.Stop(); RevealRequested?.Invoke(); }
    void Reveal_MouseEnter(object sender, MouseEventArgs e) { _hovering = true; _hover.Start(); }
    void Reveal_MouseLeave(object sender, MouseEventArgs e) { _hovering = false; _hover.Stop(); }
}
