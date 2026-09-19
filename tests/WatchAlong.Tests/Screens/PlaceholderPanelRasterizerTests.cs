using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class PlaceholderPanelRasterizerTests
{
    [Fact]
    public void Rasterize_produces_a_full_size_unpadded_bgra_buffer()
    {
        var pixels = PlaceholderPanelRasterizer.Rasterize("Waiting for the host to start something…", width: 64, height: 36);

        Assert.Equal(64 * 36 * 4, pixels.Length);
    }

    [Fact]
    public void Rasterize_fills_the_panel_with_the_dim_translucent_background_color()
    {
        var pixels = PlaceholderPanelRasterizer.Rasterize("", width: 32, height: 32);

        // A corner pixel, far from centered text, should be the panel's dim background
        // (BGRA: 0x1A, 0x1A, 0x1A, 0xCC) rather than left transparent/black.
        Assert.Equal(0x1A, pixels[0]);
        Assert.Equal(0x1A, pixels[1]);
        Assert.Equal(0x1A, pixels[2]);
        Assert.Equal(0xCC, pixels[3]);
    }

    [Fact]
    public void Rasterize_draws_something_other_than_the_flat_background_for_a_non_empty_message()
    {
        var blank = PlaceholderPanelRasterizer.Rasterize("", width: 256, height: 144);
        var withText = PlaceholderPanelRasterizer.Rasterize("Waiting for the host to start something…", width: 256, height: 144);

        Assert.NotEqual(blank, withText);
    }
}
