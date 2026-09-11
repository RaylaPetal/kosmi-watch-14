using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class BgmDuckerTests
{
    [Fact]
    public void Duck_lowers_volume_to_the_configured_percent_and_remembers_the_original()
    {
        uint volume = 100;
        var ducker = new BgmDucker(() => volume, v => volume = v, duckToPercent: 20);

        ducker.Duck();

        Assert.Equal(20u, volume);
        Assert.True(ducker.IsDucked);
    }

    [Fact]
    public void Restore_sets_volume_back_to_what_it_was_before_ducking()
    {
        uint volume = 75;
        var ducker = new BgmDucker(() => volume, v => volume = v, duckToPercent: 20);
        ducker.Duck();

        ducker.Restore();

        Assert.Equal(75u, volume);
        Assert.False(ducker.IsDucked);
    }

    [Fact]
    public void Restore_without_a_prior_duck_is_a_safe_no_op()
    {
        uint volume = 50;
        var ducker = new BgmDucker(() => volume, v => volume = v);

        ducker.Restore();

        Assert.Equal(50u, volume);
        Assert.False(ducker.IsDucked);
    }

    [Fact]
    public void Duck_while_already_ducked_does_not_overwrite_the_saved_original()
    {
        uint volume = 100;
        var ducker = new BgmDucker(() => volume, v => volume = v, duckToPercent: 20);
        ducker.Duck();
        volume = 999; // something external changes it while ducked (shouldn't normally happen, but must not corrupt the saved value)

        ducker.Duck(); // second call should be a no-op since we're already ducked
        ducker.Restore();

        Assert.Equal(100u, volume);
    }

    [Fact]
    public void Restore_is_idempotent()
    {
        uint volume = 100;
        var ducker = new BgmDucker(() => volume, v => volume = v, duckToPercent: 20);
        ducker.Duck();

        ducker.Restore();
        volume = 60; // user changes their BGM slider after restore
        ducker.Restore(); // must not clobber it back to the pre-duck value

        Assert.Equal(60u, volume);
    }
}
