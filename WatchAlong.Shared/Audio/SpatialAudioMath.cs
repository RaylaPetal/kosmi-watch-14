// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — MediaPlayerCore/MediaManager's
// GetListeningPosition/AngleDir/CalculateObjectVolume — extracted as pure functions (no
// MediaObject/camera-service dependency) so they're directly unit-testable, per design.md D3.
using System;
using System.Numerics;

namespace WatchAlong.Shared.Audio;

public static class SpatialAudioMath
{
    /// <summary>
    /// The point spatial audio is measured from: a blend between the camera (at the player's
    /// height) and the player's own position. XivMediaPlayer's slider defaults to 0 (pure
    /// camera position), which is what WatchAlong uses too.
    /// </summary>
    public static Vector3 GetListeningPosition(Vector3 cameraPosition, Vector3 playerPosition, float cameraPlayerBlend = 0f)
    {
        var cameraAtPlayerHeight = new Vector3(cameraPosition.X, playerPosition.Y, cameraPosition.Z);
        return Vector3.Lerp(cameraAtPlayerHeight, playerPosition, cameraPlayerBlend);
    }

    /// <summary>
    /// Signed left/right pan: positive when <paramref name="targetDir"/> is to the right of
    /// <paramref name="fwd"/>, negative to the left, ~0 when directly ahead or behind. Not
    /// separately clamped here — <see cref="CalculateGain"/>'s caller clamps for the wire format.
    /// </summary>
    public static float AngleDir(Vector3 fwd, Vector3 targetDir, Vector3 up)
    {
        var perp = Vector3.Cross(fwd, targetDir);
        return Vector3.Dot(perp, up);
    }

    /// <summary>
    /// Volume attenuated by distance from <paramref name="maxDistance"/> (inaudible) down to 0
    /// (at the listener), with a squared falloff curve so it drops off more naturally than linear.
    /// </summary>
    public static float CalculateGain(float masterVolume, Vector3 listenerPosition, Vector3 screenPosition, float maxDistance)
    {
        if (maxDistance <= 0f)
            return 0f;

        var distance = Vector3.Distance(listenerPosition, screenPosition);
        var attenuation = Math.Clamp((maxDistance - distance) / maxDistance, 0f, 1f);
        var exponentialAttenuation = MathF.Pow(attenuation, 2f);
        return Math.Clamp(masterVolume * exponentialAttenuation, 0f, 1f);
    }
}
