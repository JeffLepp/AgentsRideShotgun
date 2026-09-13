using System.Windows;
using HiveMind.AgentWorkspaces;

namespace Deskweave.UiProbe;

/// <summary>Corner window states: references 01, 02 and 08-13.</summary>
static class CornerScenes
{
    /// <summary>Behavior checks for the corner window, run at the end of the UI gate.</summary>
    internal static Task Gate() => Task.CompletedTask;

    // The pre-MVP corner view, as the baseline the new one is measured against.
    [Scene("corner-working", "08-corner-working", 60, 43, 344, 215)]
    static async Task<FrameworkElement> Working(SceneContext scene)
    {
        var window = scene.Own(new WorkspacePeekWindow());
        Point at = SceneContext.OffScreen;
        window.Place(new Rect(at.X, at.Y, 344, 215));
        window.Describe("shop", "Claude Code", "AWWorkingBrush");
        window.ShowFrame(scene.Site("shop"));
        window.Arrive();
        await scene.Settle();
        return window;
    }
}
