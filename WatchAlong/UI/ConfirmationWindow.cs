using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace WatchAlong.UI;

/// <summary>
/// A single confirm/decline prompt, shared by the join-invite and sync-position flows —
/// group-invites spec "Joining an invite always requires explicit confirmation" and "Position-share
/// messages are offered as a confirmable sync action". One code path for both a clicked chat link
/// and a typed <c>/wa</c> command, per that same requirement.
/// </summary>
public sealed class ConfirmationWindow : Window
{
    private const float WrapWidth = 380f;

    private string _message = "";
    private Action? _onConfirm;

    public ConfirmationWindow() : base("WatchAlong##WatchAlongConfirm", ImGuiWindowFlags.AlwaysAutoResize)
    {
    }

    public void Show(string message, Action onConfirm)
    {
        _message = message;
        _onConfirm = onConfirm;
        IsOpen = true;
    }

    public override void Draw()
    {
        using var theme = WatchAlongTheme.Push();

        // AlwaysAutoResize otherwise grows this window to fit the message on one line — capped
        // so a long invite/display name doesn't produce an absurdly wide popup.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WrapWidth);
        ImGui.TextWrapped(_message);
        ImGui.PopTextWrapPos();
        ImGui.Separator();

        if (ImGui.Button("Confirm"))
        {
            _onConfirm?.Invoke();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            IsOpen = false;
    }
}
