using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace WatchAlong.UI;

/// <summary>
/// A shared "movie night" purple/red accent palette pushed around every WatchAlong window's
/// <c>Draw()</c>, replacing Dalamud's default flat gray so all of this plugin's windows read as
/// one consistent UI. Deliberately only touches interactive-widget colors (buttons, frames,
/// sliders, title bars, separators) — never <c>Text</c> or a window's own content (e.g.
/// <see cref="ViewerWindow"/>'s video image is untouched by this).
/// </summary>
internal static class WatchAlongTheme
{
    private static readonly (ImGuiCol Slot, Vector4 Color)[] Colors =
    [
        (ImGuiCol.TitleBg, new Vector4(0.14f, 0.06f, 0.16f, 1.00f)),
        (ImGuiCol.TitleBgActive, new Vector4(0.55f, 0.10f, 0.22f, 1.00f)),
        (ImGuiCol.TitleBgCollapsed, new Vector4(0.12f, 0.05f, 0.14f, 0.85f)),

        (ImGuiCol.Header, new Vector4(0.36f, 0.18f, 0.50f, 0.65f)),
        (ImGuiCol.HeaderHovered, new Vector4(0.48f, 0.26f, 0.64f, 0.85f)),
        (ImGuiCol.HeaderActive, new Vector4(0.55f, 0.10f, 0.22f, 0.90f)),

        (ImGuiCol.Button, new Vector4(0.49f, 0.23f, 0.66f, 1.00f)),
        (ImGuiCol.ButtonHovered, new Vector4(0.60f, 0.36f, 0.75f, 1.00f)),
        (ImGuiCol.ButtonActive, new Vector4(0.82f, 0.14f, 0.30f, 1.00f)),

        (ImGuiCol.FrameBg, new Vector4(0.16f, 0.09f, 0.22f, 0.85f)),
        (ImGuiCol.FrameBgHovered, new Vector4(0.24f, 0.14f, 0.32f, 0.90f)),
        (ImGuiCol.FrameBgActive, new Vector4(0.32f, 0.18f, 0.42f, 0.95f)),

        (ImGuiCol.CheckMark, new Vector4(0.88f, 0.20f, 0.34f, 1.00f)),
        (ImGuiCol.SliderGrab, new Vector4(0.62f, 0.32f, 0.82f, 1.00f)),
        (ImGuiCol.SliderGrabActive, new Vector4(0.88f, 0.20f, 0.34f, 1.00f)),

        (ImGuiCol.Separator, new Vector4(0.45f, 0.20f, 0.42f, 0.60f)),
        (ImGuiCol.SeparatorHovered, new Vector4(0.62f, 0.28f, 0.40f, 0.80f)),
        (ImGuiCol.SeparatorActive, new Vector4(0.88f, 0.20f, 0.34f, 1.00f)),

        (ImGuiCol.Border, new Vector4(0.40f, 0.18f, 0.38f, 0.55f)),

        (ImGuiCol.ResizeGrip, new Vector4(0.55f, 0.10f, 0.22f, 0.45f)),
        (ImGuiCol.ResizeGripHovered, new Vector4(0.70f, 0.16f, 0.28f, 0.75f)),
        (ImGuiCol.ResizeGripActive, new Vector4(0.88f, 0.20f, 0.34f, 0.90f)),
    ];

    /// <summary>Pushes the whole palette. Dispose the result (a <c>using</c> at the top of <c>Draw()</c> is enough) to pop it again before the frame ends.</summary>
    public static IDisposable Push()
    {
        foreach (var (slot, color) in Colors)
            ImGui.PushStyleColor(slot, color);
        return new Popper(Colors.Length);
    }

    private sealed class Popper(int count) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ImGui.PopStyleColor(count);
        }
    }
}
