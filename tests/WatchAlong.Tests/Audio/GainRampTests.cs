using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class GainRampTests
{
    [Fact]
    public void Ramps_linearly_over_50ms_without_a_step_discontinuity()
    {
        var ramp = new GainRamp(rampMilliseconds: 50);
        ramp.SetTarget(1.0);

        var g0 = ramp.Advance(0);
        var g25 = ramp.Advance(25); // halfway
        var g25again = ramp.Advance(25); // fully arrived

        Assert.Equal(0.0, g0, precision: 6);
        Assert.Equal(0.5, g25, precision: 6);
        Assert.Equal(1.0, g25again, precision: 6);
    }

    [Fact]
    public void Never_overshoots_past_the_target()
    {
        var ramp = new GainRamp(rampMilliseconds: 50);
        ramp.SetTarget(0.8);

        var result = ramp.Advance(1000); // way more than the ramp duration

        Assert.Equal(0.8, result, precision: 6);
    }

    [Fact]
    public void Mute_ramps_down_to_zero_rather_than_cutting_instantly()
    {
        var ramp = new GainRamp(rampMilliseconds: 50);
        ramp.SetTarget(1.0);
        ramp.Advance(50);

        ramp.SetTarget(0.0); // mute
        var midMute = ramp.Advance(25);

        Assert.Equal(0.5, midMute, precision: 6);
        Assert.True(midMute > 0, "Mute should ramp down, not step instantly to 0.");
    }

    [Fact]
    public void Retargeting_mid_ramp_starts_from_the_current_value_not_the_original_target()
    {
        var ramp = new GainRamp(rampMilliseconds: 50);
        ramp.SetTarget(1.0);
        var midway = ramp.Advance(25); // 0.5

        ramp.SetTarget(0.2); // change target before finishing the previous ramp
        var afterRetarget = ramp.Advance(0);

        Assert.Equal(midway, afterRetarget, precision: 6); // no jump at the retarget instant
    }
}
