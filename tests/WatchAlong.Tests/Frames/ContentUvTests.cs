using WatchAlong.Shared.Frames;
using Xunit;

namespace WatchAlong.Tests.Frames;

public class ContentUvTests
{
    [Fact]
    public void Full_frame_content_rect_yields_0_to_1_uv()
    {
        var uv = ContentUv.FromContentRect(1280, 720, 0, 0, 1280, 720);

        Assert.Equal(new ContentUv(0f, 0f, 1f, 1f), uv);
    }

    [Fact]
    public void Letterboxed_content_rect_yields_a_cropped_uv_range()
    {
        // 1280x720 frame, but the actual video only occupies the middle 1280x480 (letterboxed top/bottom by 120px each).
        var uv = ContentUv.FromContentRect(1280, 720, 0, 120, 1280, 480);

        Assert.Equal(0f, uv.MinU);
        Assert.Equal(120f / 720f, uv.MinV, precision: 5);
        Assert.Equal(1f, uv.MaxU);
        Assert.Equal(600f / 720f, uv.MaxV, precision: 5);
    }

    [Fact]
    public void Zero_sized_frame_falls_back_to_full_frame_uv_rather_than_dividing_by_zero()
    {
        var uv = ContentUv.FromContentRect(0, 0, 0, 0, 0, 0);

        Assert.Equal(ContentUv.FullFrame, uv);
    }

    [Fact]
    public void FromSlotHeader_reads_the_same_fields_as_FromContentRect()
    {
        var header = new FrameSlotHeader { Width = 1920, Height = 1080, ContentX = 100, ContentY = 50, ContentW = 800, ContentH = 600 };

        var uv = ContentUv.FromSlotHeader(header);

        Assert.Equal(ContentUv.FromContentRect(1920, 1080, 100, 50, 800, 600), uv);
    }
}
