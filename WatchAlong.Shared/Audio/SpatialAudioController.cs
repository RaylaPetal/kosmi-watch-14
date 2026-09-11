using System;
using System.Numerics;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Screens;

namespace WatchAlong.Shared.Audio;

/// <summary>
/// Drives <c>SetAudio</c> from the active screen's world position, per design.md D3/§8.4: flat
/// (master-volume-only) audio when no screen is world-placed, distance/angle-based volume and
/// pan once one is (viewer-playback spec "Audio output is flat only when no screen is
/// world-placed"). The caller supplies camera/player positions each tick — this class has no
/// game-engine dependency, so the throttling and math are directly testable.
/// </summary>
public sealed class SpatialAudioController(ScreenController screenController, Action<SetAudioMessage> send, BgmDucker? bgmDucker = null, double tickIntervalMs = 50.0)
{
    private readonly AudioCommandThrottle _throttle = new();
    private double _lastTickMs = double.NegativeInfinity;

    /// <summary>Max distance (yalms) at which a placed screen's audio is still audible. Default per design.md: 40, range 10-100.</summary>
    public float MaxDistance { get; set; } = 40f;

    public float MasterVolume { get; set; } = 0.8f;
    public bool Muted { get; set; }

    /// <summary>
    /// Called once per frame; internally rate-limited to <see cref="tickIntervalMs"/>. When
    /// there's no active anchor, restores BGM (if ducked) and returns — flat audio is the
    /// caller's (ViewerWindow's) direct <c>SetAudio</c> responsibility, per the viewer-playback
    /// delta. <paramref name="isMediaActive"/> is whether the session currently has playing
    /// content — BGM only ducks for a placed, *active* screen (spatial-audio spec).
    /// </summary>
    public void Tick(double nowMs, Vector3 cameraPosition, Vector3 cameraForward, Vector3 playerPosition, bool isMediaActive = true)
    {
        if (screenController.ActiveAnchor is not { } anchor)
        {
            bgmDucker?.Restore();
            return;
        }

        if (nowMs - _lastTickMs < tickIntervalMs)
            return;
        _lastTickMs = nowMs;

        var listener = SpatialAudioMath.GetListeningPosition(cameraPosition, playerPosition);
        var screenPosition = anchor.Transform.Position;
        var distance = Vector3.Distance(listener, screenPosition);
        var inRange = distance <= MaxDistance;

        if (inRange && isMediaActive)
            bgmDucker?.Duck();
        else
            bgmDucker?.Restore();

        // Matches the existing SetAudio convention (ViewerWindow): Volume carries the computed
        // level regardless of Muted — the renderer/player is what actually silences output on
        // that flag, so a later unmute doesn't need a full recompute.
        var gain = SpatialAudioMath.CalculateGain(MasterVolume, listener, screenPosition, MaxDistance);

        double pan = 0.0;
        if (distance > 0.001f)
        {
            var dir = Vector3.Normalize(screenPosition - listener);
            pan = Math.Clamp(SpatialAudioMath.AngleDir(cameraForward, dir, Vector3.UnitY), -1.0, 1.0);
        }

        if (_throttle.ShouldSend(gain, Muted, nowMs, pan))
            send(new SetAudioMessage(gain, Muted, pan));
    }

    /// <summary>
    /// Reverts to flat audio and restores BGM immediately — called when the active anchor is
    /// cleared or the session closes (viewer-playback spec "Placement removed"; spatial-audio
    /// spec "Screen becomes inactive while player is in range" / "Plugin shuts down or crashes
    /// while ducked" — design.md D5's explicit-hooks approach, not a timer).
    /// </summary>
    public void RevertToFlat(double nowMs)
    {
        bgmDucker?.Restore();
        if (_throttle.ShouldSend(MasterVolume, Muted, nowMs, 0.0))
            send(new SetAudioMessage(MasterVolume, Muted, 0.0));
    }
}
