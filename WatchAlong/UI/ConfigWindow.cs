using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace WatchAlong.UI;

/// <summary>
/// Settings: identity, playback, and every session action that isn't the video itself (join,
/// leave, invite, place a screen) — opened from Dalamud's own plugin-installer "gear" icon via
/// <c>UiBuilder.OpenConfigUi</c>, or <c>/wa settings</c>. The main <see cref="ViewerWindow"/> is
/// just the screen; everything else lives here rather than scattered across overlay buttons.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;
    private readonly Action<double, bool> _setAudio;
    private readonly Func<string, bool> _joinRoom;
    private readonly Action _leaveRoom;
    private readonly Action _openPlacement;
    private readonly Action _copyInvite;
    private readonly Action _toggleFocus;
    private readonly Func<bool> _hasActiveSession;
    private readonly Func<bool> _hasActiveScreen;
    private readonly Func<IReadOnlyCollection<string>> _getSessionMembers;

    private string _displayName;
    private float _volume;
    private bool _muted;
    private string _roomUrlInput = "";
    private string? _joinError;

    public ConfigWindow(
        Configuration configuration,
        Action<double, bool> setAudio,
        Func<string, bool> joinRoom,
        Action leaveRoom,
        Action openPlacement,
        Action copyInvite,
        Action toggleFocus,
        Func<bool> hasActiveSession,
        Func<bool> hasActiveScreen,
        Func<IReadOnlyCollection<string>> getSessionMembers)
        : base("WatchAlong Settings##WatchAlongSettings")
    {
        // Wide enough that "Place / Move Screen", "Copy Invite", and "Focus on Screen" always
        // fit their own row without relying solely on ImGuiLayout.SameLineOrWrap's fallback.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _configuration = configuration;
        _setAudio = setAudio;
        _joinRoom = joinRoom;
        _leaveRoom = leaveRoom;
        _openPlacement = openPlacement;
        _copyInvite = copyInvite;
        _toggleFocus = toggleFocus;
        _hasActiveSession = hasActiveSession;
        _hasActiveScreen = hasActiveScreen;
        _getSessionMembers = getSessionMembers;

        _displayName = configuration.KosmiDisplayName;
        _volume = configuration.MasterVolume;
        _muted = configuration.Muted;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        using var theme = WatchAlongTheme.Push();

        ImGui.TextUnformatted("Identity");
        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGuiLayout.ClampedWidth(220));
        if (ImGui.InputText("Display name", ref _displayName, 40))
        {
            _configuration.KosmiDisplayName = _displayName;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Playback");
        ImGui.Separator();

        var changed = false;
        ImGui.SetNextItemWidth(ImGuiLayout.ClampedWidth(160));
        changed |= ImGui.SliderFloat("Volume", ref _volume, 0f, 1.5f);
        changed |= ImGui.Checkbox("Mute", ref _muted);
        if (changed)
            _setAudio(_volume, _muted);

        ImGui.Spacing();
        ImGui.TextUnformatted("Session");
        ImGui.Separator();

        if (_hasActiveSession())
        {
            var name = string.IsNullOrWhiteSpace(_configuration.KosmiDisplayName) ? "(no display name set)" : _configuration.KosmiDisplayName;
            ImGui.TextUnformatted($"Joined as: {name}");

            var sessionMembers = _getSessionMembers();
            if (sessionMembers.Count > 0)
                ImGui.TextUnformatted($"Also in session: {string.Join(", ", sessionMembers)}");

            if (ImGui.Button("Leave Room"))
            {
                _leaveRoom();
                _joinError = null;
            }
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            var submitted = ImGui.InputText("##RoomUrl", ref _roomUrlInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            if ((submitted || ImGui.Button("Join Room")) && !string.IsNullOrWhiteSpace(_roomUrlInput))
            {
                _joinError = _joinRoom(_roomUrlInput.Trim())
                    ? null
                    : "That doesn't look like a Kosmi room link or invite.";
            }

            if (_joinError is not null)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _joinError);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Screen");
        ImGui.Separator();

        if (ImGui.Button("Place / Move Screen"))
            _openPlacement();

        ImGuiLayout.SameLineOrWrap("Copy Invite");
        using (ImRaii.Disabled(!_hasActiveSession()))
        {
            if (ImGui.Button("Copy Invite"))
                _copyInvite();
        }

        ImGuiLayout.SameLineOrWrap("Focus on Screen");
        using (ImRaii.Disabled(!_hasActiveScreen()))
        {
            if (ImGui.Button("Focus on Screen"))
                _toggleFocus();
        }
    }
}
