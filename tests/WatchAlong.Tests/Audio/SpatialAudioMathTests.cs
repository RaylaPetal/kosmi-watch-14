using System.Numerics;
using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class SpatialAudioMathTests
{
    [Fact]
    public void GetListeningPosition_defaults_to_pure_camera_position_at_player_height()
    {
        var camera = new Vector3(10, 5, 0);
        var player = new Vector3(0, 2, 0);

        var listening = SpatialAudioMath.GetListeningPosition(camera, player);

        Assert.Equal(new Vector3(10, 2, 0), listening);
    }

    [Fact]
    public void GetListeningPosition_blends_toward_the_player_as_the_slider_increases()
    {
        var camera = new Vector3(10, 5, 0);
        var player = new Vector3(0, 2, 0);

        var listening = SpatialAudioMath.GetListeningPosition(camera, player, cameraPlayerBlend: 1f);

        Assert.Equal(player, listening);
    }

    [Theory]
    [InlineData(0, 100)] // listener at the screen: full volume
    [InlineData(50, 100)] // halfway: squared falloff, not linear
    [InlineData(100, 100)] // at max distance: silent
    [InlineData(150, 100)] // beyond max distance: still silent, not negative
    public void CalculateGain_attenuates_with_distance(float distance, float maxDistance)
    {
        var listener = Vector3.Zero;
        var screen = new Vector3(distance, 0, 0);

        var gain = SpatialAudioMath.CalculateGain(masterVolume: 1f, listener, screen, maxDistance);

        Assert.InRange(gain, 0f, 1f);
        if (distance <= 0) Assert.Equal(1f, gain, 0.001f);
        if (distance >= maxDistance) Assert.Equal(0f, gain, 0.001f);
    }

    [Fact]
    public void CalculateGain_close_is_louder_than_far()
    {
        var listener = Vector3.Zero;
        var close = SpatialAudioMath.CalculateGain(1f, listener, new Vector3(10, 0, 0), 40f);
        var far = SpatialAudioMath.CalculateGain(1f, listener, new Vector3(30, 0, 0), 40f);

        Assert.True(close > far);
    }

    [Fact]
    public void CalculateGain_scales_with_master_volume()
    {
        var listener = Vector3.Zero;
        var gain = SpatialAudioMath.CalculateGain(0.5f, listener, listener, 40f);

        Assert.Equal(0.5f, gain, 0.001f);
    }

    [Fact]
    public void AngleDir_is_zero_when_target_is_directly_ahead()
    {
        var fwd = new Vector3(0, 0, 1);
        var up = new Vector3(0, 1, 0);
        var dir = SpatialAudioMath.AngleDir(fwd, fwd, up);

        Assert.Equal(0f, dir, 0.001f);
    }

    [Fact]
    public void AngleDir_is_positive_when_target_is_to_the_right()
    {
        var fwd = new Vector3(0, 0, 1);
        var up = new Vector3(0, 1, 0);
        var right = new Vector3(1, 0, 0);

        var dir = SpatialAudioMath.AngleDir(fwd, right, up);

        Assert.True(dir > 0);
    }

    [Fact]
    public void AngleDir_is_negative_when_target_is_to_the_left()
    {
        var fwd = new Vector3(0, 0, 1);
        var up = new Vector3(0, 1, 0);
        var left = new Vector3(-1, 0, 0);

        var dir = SpatialAudioMath.AngleDir(fwd, left, up);

        Assert.True(dir < 0);
    }
}
