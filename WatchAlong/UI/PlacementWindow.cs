using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WatchAlong.Shared.Invites;
using WatchAlong.Shared.Screens;

namespace WatchAlong.UI;

/// <summary>
/// The placement gizmo's UI (world-screens spec "Screen placement is created and adjustable
/// in-world"): live numeric move/resize/rotate fields plus "Place in front of me", confirm, and
/// cancel — the numeric-field half of XivMediaPlayer's ported <c>PlacementManipulator</c>
/// (design.md Appendix C). A full on-screen drag-handle gizmo (the manipulator's mouse
/// hit-testing) is deferred: it needs the game's live camera/view-projection matrices to
/// validate visually, which isn't verifiable in a build-only environment — the numeric fields
/// satisfy every world-screens placement scenario (move/resize/rotate live, confirm/cancel, a
/// quick default placement) on their own.
/// </summary>
public sealed class PlacementWindow : Window
{
    private readonly ScreenController _screenController;
    private readonly Func<(Vector3 Position, Vector3 Forward)> _getPlayerFacing;
    private readonly Action _onCommit;
    private readonly Action _onCancel;
    private readonly Func<string> _getDisplayName;
    private readonly Action<string> _copyToClipboard;

    private ScreenTransform? _editing;

    public PlacementWindow(
        ScreenController screenController,
        Func<(Vector3 Position, Vector3 Forward)> getPlayerFacing,
        Action onCommit,
        Action onCancel,
        Func<string> getDisplayName,
        Action<string> copyToClipboard)
        : base("WatchAlong Screen Placement##WatchAlongPlacement")
    {
        _screenController = screenController;
        _getPlayerFacing = getPlayerFacing;
        _onCommit = onCommit;
        _onCancel = onCancel;
        _getDisplayName = getDisplayName;
        _copyToClipboard = copyToClipboard;
    }

    /// <summary>True while the gizmo owns input — <c>InputRouter</c>/control mode should not also receive it (design.md D6).</summary>
    public bool IsEditing => _editing is not null;

    public override void OnOpen()
    {
        var existing = _screenController.ActiveAnchor?.Transform;
        _editing = existing ?? PlaceInFrontOfPlayer();
        if (existing is null)
            _screenController.Place(_editing);
    }

    public override void OnClose() => _editing = null;

    public override void Draw()
    {
        if (_editing is not { } transform)
            return;

        var changed = false;
        var position = transform.Position;
        var rotation = transform.RotationDegrees;
        var scale = transform.Scale;

        ImGui.TextUnformatted("Position (yalms)");
        changed |= ImGui.DragFloat3("##Position", ref position, 0.05f);

        ImGui.TextUnformatted("Rotation (degrees: pitch / yaw / roll)");
        changed |= ImGui.DragFloat3("##Rotation", ref rotation, 0.5f);

        // Locked to Kosmi's fixed 1920x1080 output (see WorldVideoRenderer's contentAspect comment)
        // rather than a free width/height drag — an off-ratio screen either gets letterboxed by the
        // depth-tested shader's own aspect correction or visibly stretched in quad mode; a single
        // size control that always derives height from width avoids either happening by construction.
        ImGui.TextUnformatted("Width (yalms, 16:9 height follows)");
        var width = scale.X;
        if (ImGui.DragFloat("##Width", ref width, 0.02f, 0.2f, 20f))
        {
            scale = new Vector2(width, width * 9f / 16f);
            changed = true;
        }

        if (changed)
        {
            _editing = transform with { Position = position, RotationDegrees = rotation, Scale = scale };
            _screenController.Preview(_editing);
        }

        ImGui.Separator();

        if (ImGui.Button("Place in front of me"))
        {
            _editing = PlaceInFrontOfPlayer();
            _screenController.Preview(_editing);
        }

        ImGui.SameLine();
        if (ImGui.Button("Confirm"))
        {
            _screenController.Commit();
            _onCommit();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _screenController.Cancel();
            _onCancel();
            IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove screen"))
        {
            _screenController.Remove();
            _onCancel();
            IsOpen = false;
        }

        // group-invites spec "Sharing position from an existing placement" / "Moving the screen
        // does not trigger a share on its own": only this explicit button ever encodes/copies a
        // share message — dragging, resizing, or rotating above never does.
        if (_screenController.ActiveAnchor is { } activeAnchor && ImGui.Button("Share position"))
            _copyToClipboard(InviteCodec.EncodePositionShare(_getDisplayName(), activeAnchor));
    }

    private ScreenTransform PlaceInFrontOfPlayer()
    {
        var (position, forward) = _getPlayerFacing();
        var previous = _screenController.ActiveAnchor?.Transform.Scale;
        return ScreenTransform.PlaceLookingAt(position + forward * 3f, position, previous);
    }
}
