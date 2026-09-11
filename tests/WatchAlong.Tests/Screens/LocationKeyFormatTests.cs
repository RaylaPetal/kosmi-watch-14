using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class LocationKeyFormatTests
{
    [Fact]
    public void House_key_includes_world_territory_ward_plot_room_and_house_id()
    {
        Assert.Equal("house_42_282_5_12_3_99", LocationKeyFormat.House(42, 282, 5, 12, 3, 99));
    }

    [Fact]
    public void Plot_key_has_no_room_or_house_id()
    {
        Assert.Equal("zone_42_5_282_plot_12", LocationKeyFormat.Plot(42, 5, 282, 12));
    }

    [Fact]
    public void Island_key_is_scoped_to_world_and_owner_name()
    {
        Assert.Equal("island_42_Ray Petal", LocationKeyFormat.Island(42, "Ray Petal"));
    }

    [Theory]
    [InlineData(0f, 0f, 0, 0)]
    [InlineData(59.9f, 0f, 0, 0)]
    [InlineData(60f, 0f, 1, 0)]
    [InlineData(-0.1f, 0f, -1, 0)]
    public void ZoneGrid_buckets_position_into_60_yalm_cells(float x, float z, int expectedGridX, int expectedGridZ)
    {
        var key = LocationKeyFormat.ZoneGrid(42, 5, 148, x, z);

        Assert.Equal($"zone_42_5_148_grid_{expectedGridX}_{expectedGridZ}", key);
    }

    [Fact]
    public void Zone_key_has_no_position_component()
    {
        Assert.Equal("zone_42_5_148", LocationKeyFormat.Zone(42, 5, 148));
    }
}
