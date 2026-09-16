using Dalamud.Bindings.ImGui;

namespace WatchAlong.UI;

/// <summary>
/// Small layout helpers shared by every WatchAlong window. These windows are all freely
/// user-resizable, and ImGui neither wraps nor scrolls a widget that doesn't fit on the current
/// line — it just gets silently clipped at the window's edge.
/// </summary>
internal static class ImGuiLayout
{
    /// <summary>
    /// Call instead of a bare <c>ImGui.SameLine()</c> before each button after the first in a row,
    /// passing that next button's own label: it only stays on the same line if there's actually
    /// room for it, and drops to a new line otherwise.
    /// </summary>
    public static void SameLineOrWrap(string nextButtonLabel)
    {
        var width = ImGui.CalcTextSize(nextButtonLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        if (ImGui.GetContentRegionAvail().X >= width)
            ImGui.SameLine();
    }

    /// <summary>Caps a preferred fixed pixel width to whatever room is actually left in the window, so a narrowed window clips a field instead of just shrinking it.</summary>
    public static float ClampedWidth(float preferred) => System.MathF.Min(preferred, System.MathF.Max(ImGui.GetContentRegionAvail().X, 50f));
}
