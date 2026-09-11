using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class ScreenVisibilityGateTests
{
    [Fact]
    public void Normal_play_shows_the_screen()
    {
        Assert.True(ScreenVisibilityGate.ShouldShow(isInCutscene: false, isGPosing: false, showScreensInGPose: false));
    }

    [Fact]
    public void Cutscene_hides_the_screen_regardless_of_gpose_settings()
    {
        Assert.False(ScreenVisibilityGate.ShouldShow(isInCutscene: true, isGPosing: false, showScreensInGPose: true));
    }

    [Fact]
    public void GPose_hides_the_screen_by_default()
    {
        Assert.False(ScreenVisibilityGate.ShouldShow(isInCutscene: false, isGPosing: true, showScreensInGPose: false));
    }

    [Fact]
    public void GPose_shows_the_screen_when_the_user_opted_in()
    {
        Assert.True(ScreenVisibilityGate.ShouldShow(isInCutscene: false, isGPosing: true, showScreensInGPose: true));
    }

    [Fact]
    public void Leaving_gpose_returns_to_normal_visibility_regardless_of_the_setting()
    {
        // Simulates the transition: GPose (hidden, setting off) -> normal play.
        Assert.False(ScreenVisibilityGate.ShouldShow(isInCutscene: false, isGPosing: true, showScreensInGPose: false));
        Assert.True(ScreenVisibilityGate.ShouldShow(isInCutscene: false, isGPosing: false, showScreensInGPose: false));
    }
}
