using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using WatchAlong.Screens.Ported;
using WatchAlong.Shared.Input;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;

namespace WatchAlong.UI;

/// <summary>
/// The main window: the Kosmi screen, always interactive — no separate "control mode" window or
/// toggle. Mouse/keyboard forward to the page whenever this window is focused (design.md §6.7's
/// input forwarding, merged into the one window a user actually looks at instead of split across
/// two). Session actions (join/leave/volume/invite/placement) live in <see cref="ConfigWindow"/>;
/// this window only ever shows the video and a way to reach settings.
/// </summary>
public sealed class ViewerWindow : Window, IDisposable
{
    private readonly KosmiSession _session;
    private readonly Direct3D11VideoTexture _texture;
    private readonly Action<IpcMessage> _send;
    public ViewerWindow(KosmiSession session, Direct3D11VideoTexture texture, Action<IpcMessage> send, Action openSettings)
        : base("WatchAlong##WatchAlongViewer")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 240),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // A title-bar icon (next to the window's own close button) rather than a button that
        // eats space from the video every frame — same place Dalamud's own plugin-installer gear
        // lives, so it reads as "settings" without a label.
        TitleBarButtons.Add(new Dalamud.Interface.Windowing.TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Priority = int.MaxValue,
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
            Click = _ => openSettings(),
        });

        _session = session;
        _texture = texture;
        _send = send;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (_session.State == KosmiSessionState.Idle)
        {
            DrawStatusMessage("Not connected — open Settings to join a room.");
            return;
        }

        if (_session.State is KosmiSessionState.Reconnecting or KosmiSessionState.Error)
        {
            DrawStatusMessage(_session.State == KosmiSessionState.Reconnecting
                ? "Reconnecting…"
                : KosmiErrorMessages.Describe(_session.ErrorCode ?? ""));
            return;
        }

        // Interactive as soon as there's a page to show something can be clicked on (JoinGate,
        // NeedsUser) — not gated to InRoomPlaying like the old read-only viewer was, since this
        // window is now the only way to finish a join that needs a manual click.
        if (!_texture.HasTexture)
        {
            DrawStatusMessage(_session.State switch
            {
                KosmiSessionState.StartingRenderer => "Starting the browser process…",
                KosmiSessionState.Loading => "Loading the Kosmi page…",
                _ => "Connecting…",
            });
            return;
        }

        DrawInteractiveVideo();
    }

    // Crops to the actual content rect (design.md §5.2/§8.2's ContentUv) rather than showing the
    // raw captured frame — the page has real chrome/letterboxing around the video, and without
    // this crop that shows up as a big blank border above/around the content.
    private void DrawInteractiveVideo()
    {
        var uv = _texture.ContentUv;
        var contentX = (int)MathF.Round(uv.MinU * _texture.Width);
        var contentY = (int)MathF.Round(uv.MinV * _texture.Height);
        var contentW = Math.Max(1, (int)MathF.Round((uv.MaxU - uv.MinU) * _texture.Width));
        var contentH = Math.Max(1, (int)MathF.Round((uv.MaxV - uv.MinV) * _texture.Height));

        var available = ImGui.GetContentRegionAvail();
        var drawSize = FitAspect(available, (float)contentW / contentH);
        var origin = ImGui.GetCursorScreenPos();

        if (_texture.HasTexture)
        {
            ImGui.Image(
                new ImTextureID(_texture.ShaderResourceView),
                drawSize,
                new Vector2(uv.MinU, uv.MinV),
                new Vector2(uv.MaxU, uv.MaxV));
        }

        if (ImGui.IsWindowFocused())
        {
            ImGui.SetNextFrameWantCaptureKeyboard(true);
            ForwardMouse(origin, drawSize, contentX, contentY, contentW, contentH);
            ForwardKeyboard();
        }
    }

    private static Vector2 FitAspect(Vector2 available, float aspect)
    {
        var height = available.X / aspect;
        return height <= available.Y
            ? new Vector2(available.X, height)
            : new Vector2(available.Y * aspect, available.Y);
    }

    private void ForwardMouse(Vector2 origin, Vector2 displaySize, int contentX, int contentY, int contentW, int contentH)
    {
        var mouse = ImGui.GetMousePos() - origin;
        var (x, y) = InputCoordinateMapper.MapToViewCoordinates(
            mouse.X, mouse.Y, displaySize.X, displaySize.Y,
            contentX, contentY, contentW, contentH);

        if (!ImGui.IsWindowHovered())
            return;

        _send(new InputMessage(InputKind.MouseMove, x, y));

        foreach (var (button, name) in new[] { (ImGuiMouseButton.Left, "Left"), (ImGuiMouseButton.Right, "Right"), (ImGuiMouseButton.Middle, "Middle") })
        {
            if (ImGui.IsMouseClicked(button))
                _send(new InputMessage(InputKind.MouseButton, x, y, name, Up: false, Clicks: 1));
            if (ImGui.IsMouseReleased(button))
                _send(new InputMessage(InputKind.MouseButton, x, y, name, Up: true, Clicks: 1));
        }

        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0)
            _send(new InputMessage(InputKind.Wheel, x, y, Dy: wheel * 100));
    }

    // Windows virtual-key codes for the documented shortcut subset (design.md §6.7).
    private static readonly (ImGuiKey Key, int Vk)[] ForwardedKeys =
    [
        (ImGuiKey.Enter, 0x0D), (ImGuiKey.Backspace, 0x08), (ImGuiKey.Tab, 0x09),
        (ImGuiKey.LeftArrow, 0x25), (ImGuiKey.UpArrow, 0x26), (ImGuiKey.RightArrow, 0x27), (ImGuiKey.DownArrow, 0x28),
        (ImGuiKey.Escape, 0x1B),
    ];

    private void ForwardKeyboard()
    {
        foreach (var (key, vk) in ForwardedKeys)
        {
            if (ImGui.IsKeyPressed(key))
                _send(new InputMessage(InputKind.Key, Vk: vk, Up: false));
        }

        var ctrl = ImGui.GetIO().KeyCtrl;
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V))
        {
            var clipboard = ImGui.GetClipboardText();
            if (!string.IsNullOrEmpty(clipboard))
                _send(new InputMessage(InputKind.Text, Text: clipboard));
        }
        else if (ctrl && (ImGui.IsKeyPressed(ImGuiKey.A) || ImGui.IsKeyPressed(ImGuiKey.C) || ImGui.IsKeyPressed(ImGuiKey.X) || ImGui.IsKeyPressed(ImGuiKey.Z)))
        {
            var vk = ImGui.IsKeyPressed(ImGuiKey.A) ? 0x41 : ImGui.IsKeyPressed(ImGuiKey.C) ? 0x43 : ImGui.IsKeyPressed(ImGuiKey.X) ? 0x58 : 0x5A;
            _send(new InputMessage(InputKind.Key, Vk: vk, Up: false, Mods: 0x2));
        }

        var io = ImGui.GetIO();
        for (var i = 0; i < io.InputQueueCharacters.Size; i++)
        {
            var ch = (char)io.InputQueueCharacters[i];
            if (!char.IsControl(ch))
                _send(new InputMessage(InputKind.Text, Text: ch.ToString()));
        }
    }

    private static void DrawStatusMessage(string message)
    {
        var available = ImGui.GetContentRegionAvail();
        var textSize = ImGui.CalcTextSize(message);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + (available - textSize) * 0.5f);
        ImGui.TextUnformatted(message);
    }
}
