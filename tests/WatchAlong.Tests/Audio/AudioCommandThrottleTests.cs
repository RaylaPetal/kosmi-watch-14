using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class AudioCommandThrottleTests
{
    [Fact]
    public void First_value_is_always_sent()
    {
        var throttle = new AudioCommandThrottle();
        Assert.True(throttle.ShouldSend(0.8, false, 0));
    }

    [Fact]
    public void Redundant_identical_values_are_not_re_sent()
    {
        var throttle = new AudioCommandThrottle();
        throttle.ShouldSend(0.8, false, 0);

        Assert.False(throttle.ShouldSend(0.8, false, 1000));
        Assert.False(throttle.ShouldSend(0.8, false, 2000));
    }

    [Fact]
    public void Tiny_changes_below_the_threshold_are_not_re_sent()
    {
        var throttle = new AudioCommandThrottle(changeThreshold: 0.01);
        throttle.ShouldSend(0.8, false, 0);

        Assert.False(throttle.ShouldSend(0.805, false, 1000));
    }

    [Fact]
    public void Volume_changes_are_rate_limited_to_roughly_20hz()
    {
        var throttle = new AudioCommandThrottle(minIntervalMs: 50);
        throttle.ShouldSend(0.1, false, 0);

        // A rapid drag sending many distinct values within one 50ms window.
        Assert.False(throttle.ShouldSend(0.2, false, 10));
        Assert.False(throttle.ShouldSend(0.3, false, 30));
        Assert.True(throttle.ShouldSend(0.4, false, 55)); // past the 50ms floor
    }

    [Fact]
    public void Mute_toggle_is_never_dropped_for_being_too_soon()
    {
        var throttle = new AudioCommandThrottle(minIntervalMs: 50);
        throttle.ShouldSend(0.8, false, 0);

        Assert.True(throttle.ShouldSend(0.8, true, 5)); // mute, well within the 50ms window
    }

    [Fact]
    public void Pan_only_change_is_sent()
    {
        var throttle = new AudioCommandThrottle(changeThreshold: 0.01);
        throttle.ShouldSend(0.8, false, 0, pan: 0.0);

        Assert.True(throttle.ShouldSend(0.8, false, 1000, pan: 0.5));
    }

    [Fact]
    public void Tiny_pan_changes_below_the_threshold_are_not_re_sent()
    {
        var throttle = new AudioCommandThrottle(changeThreshold: 0.01);
        throttle.ShouldSend(0.8, false, 0, pan: 0.2);

        Assert.False(throttle.ShouldSend(0.8, false, 1000, pan: 0.205));
    }
}
