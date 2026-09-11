namespace WatchAlong.Shared.Audio;

/// <summary>
/// Gates outgoing <c>SetAudio</c> commands to at most 20 Hz and only when the value actually
/// changed, per design.md §8.4/D2 ("sent only when a value changes... ≤ 20/s"). Driven by an
/// explicit clock so the throttling is deterministic to test.
/// </summary>
public sealed class AudioCommandThrottle(double minIntervalMs = 50.0, double changeThreshold = 0.01)
{
    private double? _lastSentVolume;
    private bool? _lastSentMuted;
    private double? _lastSentPan;
    private double? _lastSentAtMs;

    /// <summary>Returns true if this (volume, muted) pair should actually be sent now.</summary>
    public bool ShouldSend(double volume, bool muted, double nowMs) => ShouldSend(volume, muted, nowMs, pan: _lastSentPan ?? 0.0);

    /// <summary>
    /// Returns true if this (volume, muted, pan) triple should actually be sent now — pan
    /// changes are gated by the same threshold/rate limit as volume (design.md D4/tasks.md 5.2).
    /// </summary>
    public bool ShouldSend(double volume, bool muted, double nowMs, double pan)
    {
        var mutedChanged = _lastSentMuted is null || muted != _lastSentMuted;
        var volumeChanged = _lastSentVolume is null || Math.Abs(volume - _lastSentVolume.Value) > changeThreshold;
        var panChanged = _lastSentPan is null || Math.Abs(pan - _lastSentPan.Value) > changeThreshold;

        if (!mutedChanged && !volumeChanged && !panChanged)
            return false;

        // Mute toggles are never dropped for being "too soon" — only continuous volume
        // dragging gets rate-limited, per D2's intent (avoid flooding the pipe while dragging).
        if (!mutedChanged && _lastSentAtMs is { } lastAt && nowMs - lastAt < minIntervalMs)
            return false;

        _lastSentVolume = volume;
        _lastSentMuted = muted;
        _lastSentPan = pan;
        _lastSentAtMs = nowMs;
        return true;
    }
}
