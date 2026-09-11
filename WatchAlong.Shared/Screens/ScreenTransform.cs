// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — MediaPlayerCore/Compositing/WorldScreenTransform.
// Trimmed to the Phase 2a fields WatchAlong actually uses (position/rotation/scale) — the
// VLC-specific opacity/projector-mode/screensaver/idle-branding/visual-effect fields aren't
// meaningful for a Kosmi video feed and are left out rather than ported unused, per Appendix C.
using System;
using System.Numerics;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// Pure description of a placed screen's position, rotation, and size in world space — no
/// game-engine dependency, so it's usable from both the plugin and tests (world-screens spec
/// "Screen placement is created and adjustable in-world").
/// </summary>
public sealed record ScreenTransform
{
    public Vector3 Position { get; init; } = Vector3.Zero;

    /// <summary>Degrees: X = pitch, Y = yaw, Z = roll. Yaw 0 faces +Z, 90 faces +X.</summary>
    public Vector3 RotationDegrees { get; init; } = Vector3.Zero;

    /// <summary>World-unit (yalm) width/height. Default ~3m wide at 16:9.</summary>
    public Vector2 Scale { get; init; } = new(3.0f, 1.6875f);

    public Matrix4x4 RotationMatrix
    {
        get
        {
            var pitch = RotationDegrees.X * MathF.PI / 180f;
            var yaw = RotationDegrees.Y * MathF.PI / 180f;
            var roll = RotationDegrees.Z * MathF.PI / 180f;
            return Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        }
    }

    /// <summary>The four corners of the screen quad in world space, in TL/TR/BR/BL order when facing the screen.</summary>
    public (Vector3 TL, Vector3 TR, Vector3 BR, Vector3 BL) Corners
    {
        get
        {
            var halfW = Scale.X * 0.5f;
            var halfH = Scale.Y * 0.5f;

            var tl = new Vector3(-halfW, halfH, 0);
            var tr = new Vector3(halfW, halfH, 0);
            var br = new Vector3(halfW, -halfH, 0);
            var bl = new Vector3(-halfW, -halfH, 0);

            var rotation = RotationMatrix;
            tl = Vector3.Transform(tl, rotation) + Position;
            tr = Vector3.Transform(tr, rotation) + Position;
            br = Vector3.Transform(br, rotation) + Position;
            bl = Vector3.Transform(bl, rotation) + Position;

            return (tl, tr, br, bl);
        }
    }

    /// <summary>The forward direction the screen faces (its surface normal).</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, RotationMatrix);

    /// <summary>A transform placed at <paramref name="position"/>, facing toward <paramref name="lookAtTarget"/>.</summary>
    public static ScreenTransform PlaceLookingAt(Vector3 position, Vector3 lookAtTarget, Vector2? scale = null)
    {
        var dir = Vector3.Normalize(lookAtTarget - position);
        var yaw = MathF.Atan2(dir.X, dir.Z) * 180f / MathF.PI;
        var pitch = MathF.Asin(-dir.Y) * 180f / MathF.PI;
        return new ScreenTransform
        {
            Position = position,
            RotationDegrees = new Vector3(pitch, yaw, 0),
            Scale = scale ?? new Vector2(3.0f, 1.6875f),
        };
    }
}
