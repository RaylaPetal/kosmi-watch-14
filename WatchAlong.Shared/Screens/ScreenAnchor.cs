using System.Numerics;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// A screen's saved placement at a specific location (design.md D1: <c>Location</c> anchors
/// only in this phase — no group-follow <c>Actor</c> anchor). The absence of an anchor for the
/// current location is the <c>Personal</c> fallback: no world screen, viewer window only.
/// </summary>
public sealed record ScreenAnchor(string LocationKey, ScreenTransform Transform);

/// <summary>Flat, JSON-friendly shape of a <see cref="ScreenAnchor"/> for on-disk persistence.</summary>
public sealed record AnchorRecord(
    string LocationKey,
    double PosX,
    double PosY,
    double PosZ,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees,
    double Width,
    double Height)
{
    public static AnchorRecord FromAnchor(ScreenAnchor anchor) => new(
        anchor.LocationKey,
        anchor.Transform.Position.X,
        anchor.Transform.Position.Y,
        anchor.Transform.Position.Z,
        anchor.Transform.RotationDegrees.Y,
        anchor.Transform.RotationDegrees.X,
        anchor.Transform.RotationDegrees.Z,
        anchor.Transform.Scale.X,
        anchor.Transform.Scale.Y);

    public ScreenAnchor ToAnchor() => new(
        LocationKey,
        new ScreenTransform
        {
            Position = new Vector3((float)PosX, (float)PosY, (float)PosZ),
            RotationDegrees = new Vector3((float)PitchDegrees, (float)YawDegrees, (float)RollDegrees),
            Scale = new Vector2((float)Width, (float)Height),
        });
}
