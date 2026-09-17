using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class VenueMembershipStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wa-venue-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Remember_then_load_round_trips_the_room_code()
    {
        var store = new VenueMembershipStore(_dir);

        store.Remember("house_1_2_3_4_5_6", "sulync");

        Assert.Equal("sulync", store.TryLoad("house_1_2_3_4_5_6"));
    }

    [Fact]
    public void TryLoad_returns_null_for_a_location_with_nothing_remembered()
    {
        var store = new VenueMembershipStore(_dir);

        Assert.Null(store.TryLoad("zone_1_2_3"));
    }

    [Fact]
    public void Remembering_again_for_the_same_location_replaces_the_old_room()
    {
        var store = new VenueMembershipStore(_dir);
        store.Remember("house_1_2_3_4_5_6", "sulync");

        store.Remember("house_1_2_3_4_5_6", "movie-night");

        Assert.Equal("movie-night", store.TryLoad("house_1_2_3_4_5_6"));
    }

    [Fact]
    public void ClearAll_removes_every_remembered_room()
    {
        var store = new VenueMembershipStore(_dir);
        store.Remember("house_1_2_3_4_5_6", "sulync");
        store.Remember("zone_1_2_3", "another-room");

        store.ClearAll();

        Assert.Null(store.TryLoad("house_1_2_3_4_5_6"));
        Assert.Null(store.TryLoad("zone_1_2_3"));
    }
}
