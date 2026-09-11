namespace WatchAlong.Shared.Audio;

/// <summary>
/// Ramps volume/mute changes over 50 ms instead of stepping instantly, per design.md §6.6
/// ("Parameters arriving from the plugin are ramped over 50 ms to avoid zipper noise").
/// </summary>
public sealed class GainRamp(double rampMilliseconds = 50.0)
{
    private double _from;
    private double _to;
    private double _elapsedMs;

    public double Current { get; private set; }

    /// <summary>Starts ramping from the current gain toward <paramref name="target"/>.</summary>
    public void SetTarget(double target)
    {
        _from = Current;
        _to = target;
        _elapsedMs = 0;
    }

    /// <summary>Advances the ramp by <paramref name="deltaMs"/> and returns the new current gain.</summary>
    public double Advance(double deltaMs)
    {
        if (_elapsedMs >= rampMilliseconds)
        {
            Current = _to;
            return Current;
        }

        _elapsedMs = Math.Min(rampMilliseconds, _elapsedMs + deltaMs);
        var t = rampMilliseconds <= 0 ? 1.0 : _elapsedMs / rampMilliseconds;
        Current = _from + (_to - _from) * t;
        return Current;
    }
}
