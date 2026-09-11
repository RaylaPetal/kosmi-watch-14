using WatchAlong.Shared.Input;
using Xunit;

namespace WatchAlong.Tests.Input;

public class InputCoordinateMapperTests
{
    [Fact]
    public void No_scaling_or_cropping_maps_1_to_1()
    {
        var (x, y) = InputCoordinateMapper.MapToViewCoordinates(
            displayX: 100, displayY: 200, displayWidth: 1280, displayHeight: 720,
            contentX: 0, contentY: 0, contentW: 1280, contentH: 720);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void Scales_when_the_displayed_window_is_smaller_than_the_content()
    {
        // Displayed at half size (640x360 window showing a 1280x720 content area).
        var (x, y) = InputCoordinateMapper.MapToViewCoordinates(
            displayX: 320, displayY: 180, displayWidth: 640, displayHeight: 360,
            contentX: 0, contentY: 0, contentW: 1280, contentH: 720);

        Assert.Equal(640, x);
        Assert.Equal(360, y);
    }

    [Fact]
    public void Offsets_by_the_content_rect_origin_when_letterboxed()
    {
        // Content is letterboxed: only [100,50]..[100+800,50+600] of the viewport is real media.
        var (x, y) = InputCoordinateMapper.MapToViewCoordinates(
            displayX: 0, displayY: 0, displayWidth: 800, displayHeight: 600,
            contentX: 100, contentY: 50, contentW: 800, contentH: 600);

        Assert.Equal(100, x);
        Assert.Equal(50, y);
    }

    [Fact]
    public void Zero_sized_display_does_not_divide_by_zero()
    {
        var (x, y) = InputCoordinateMapper.MapToViewCoordinates(
            displayX: 10, displayY: 10, displayWidth: 0, displayHeight: 0,
            contentX: 5, contentY: 5, contentW: 100, contentH: 100);

        Assert.Equal(5, x);
        Assert.Equal(5, y);
    }
}
