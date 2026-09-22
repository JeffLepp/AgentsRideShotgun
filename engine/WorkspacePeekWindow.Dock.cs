using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Deskweave.AgentWorkspaces;

public partial class WorkspacePeekWindow
{
    WorkspacePeekTab? _edgeTab;
    Rect? _slideBounds;
    double _slideOffset;
    int _slideVersion;
    internal bool Docked { get; private set; }
    internal bool Sliding => _slideBounds is not null;
    internal event Action? EdgeRevealRequested;
    internal bool? DockAnimationsForTests;
    bool DockAnimations => DockAnimationsForTests ?? SystemParameters.ClientAreaAnimation;

    internal void Dock(string workspaceName)
    {
        if (Docked)
        {
            if (!Sliding) _edgeTab?.Place(WorkspacePeekPlacement.MonitorFor(FrontRect).WorkArea, FrontRect, workspaceName);
            return;
        }
        // Preserve the logical position if a return is interrupted by dismissal or a policy change.
        ResetSlide();
        Rect card = FrontRect;
        Rect work = WorkspacePeekPlacement.MonitorFor(card).WorkArea;
        _edgeTab ??= MakeEdgeTab();
        _edgeTab.Place(work, card, workspaceName);
        Docked = true;
        _leaving = true;
        SetHover(false);
        UpdateActivityAnimation();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible || !DockAnimations) { FinishDock(); return; }
        PrepareSlide(work);
        AnimateSlide(_slideOffset, FinishDock);
    }

    WorkspacePeekTab MakeEdgeTab()
    {
        var tab = new WorkspacePeekTab();
        tab.RevealRequested += () => EdgeRevealRequested?.Invoke();
        return tab;
    }

    void FinishDock()
    {
        Hide();
        ResetSlide();
        // The host already retains this frame. Reuse it on return while the fresh capture arrives.
        HideFramePatch();
        _edgeTab?.Show();
    }

    // Animate content within one clipped viewport. Moving the native window off-screen would
    // expose it on an adjoining monitor and can trigger a DPI change midway through the slide.
    void PrepareSlide(Rect work)
    {
        Rect bounds = new(Left, Top, Width, Height);
        _slideBounds = bounds;
        bool right = FrontRect.Left + FrontRect.Width / 2 >= work.Left + work.Width / 2;
        double left = right ? bounds.Left : Math.Min(bounds.Left, work.Left);
        double end = right ? Math.Max(bounds.Right, work.Right) : bounds.Right;
        _slideOffset = right ? work.Right - bounds.Left : work.Left - bounds.Right;
        Root.Width = bounds.Width;
        Root.Height = bounds.Height;
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.VerticalAlignment = VerticalAlignment.Top;
        Root.Margin = new Thickness(bounds.Left - left, 0, 0, 0);
        Left = left;
        Width = end - left;
        DockViewport.Clip = new RectangleGeometry(new Rect(work.Left - left, work.Top - Top, work.Width, work.Height));
        var motion = Root.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Root.RenderTransform = motion;
        motion.BeginAnimation(TranslateTransform.YProperty, null);
        motion.Y = 0;
    }

    void AnimateSlide(double target, Action finished)
    {
        int version = ++_slideVersion;
        var motion = (TranslateTransform)Root.RenderTransform;
        var animation = new DoubleAnimation(motion.X, target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) => { if (version == _slideVersion) finished(); };
        motion.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    bool ReturnFromEdge()
    {
        if (!Docked) return false;
        Docked = false;
        _leaving = false;
        _edgeTab?.Hide();
        bool interrupted = Sliding;
        if (!interrupted && DockAnimations)
        {
            PrepareSlide(WorkspacePeekPlacement.MonitorFor(FrontRect).WorkArea);
            ((TranslateTransform)Root.RenderTransform).X = _slideOffset;
        }
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        Topmost = true;
        UpdateActivityAnimation();
        if (DockAnimations && Sliding) AnimateSlide(0, FinishReturn);
        else FinishReturn();
        return true;
    }

    void FinishReturn()
    {
        ResetSlide();
        UpdateLayout();
        SetHover(Root.IsMouseOver);
        HoverChanged?.Invoke();
    }

    void ResetSlide()
    {
        ++_slideVersion;
        if (Root.RenderTransform is TranslateTransform motion)
        {
            motion.BeginAnimation(TranslateTransform.XProperty, null);
            motion.X = 0;
        }
        if (_slideBounds is not { } bounds) return;
        _slideBounds = null;
        DockViewport.Clip = null;
        Root.Width = Root.Height = double.NaN;
        Root.HorizontalAlignment = HorizontalAlignment.Stretch;
        Root.VerticalAlignment = VerticalAlignment.Stretch;
        Root.Margin = new Thickness(0);
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
    }

    void CancelDock()
    {
        Docked = false;
        _edgeTab?.Hide();
        ResetSlide();
        SetHover(false);
    }

    void CloseEdgeTab()
    {
        ++_slideVersion;
        _edgeTab?.Close();
        _edgeTab = null;
    }
}
