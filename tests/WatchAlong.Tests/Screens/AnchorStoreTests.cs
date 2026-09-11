using System.Numerics;
using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class AnchorStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wa-anchor-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Save_then_load_round_trips_the_transform()
    {
        var store = new AnchorStore(_dir);
        var anchor = new ScreenAnchor("house_1_2_3_4_5_6", new ScreenTransform
        {
            Position = new Vector3(1.5f, 2.5f, -3.5f),
            RotationDegrees = new Vector3(0, 90, 0),
            Scale = new Vector2(3.0f, 1.6875f),
        });

        store.Save(anchor);
        var loaded = store.TryLoad("house_1_2_3_4_5_6");

        Assert.NotNull(loaded);
        Assert.Equal(anchor.LocationKey, loaded.LocationKey);
        Assert.Equal(anchor.Transform.Position, loaded.Transform.Position);
        Assert.Equal(anchor.Transform.RotationDegrees, loaded.Transform.RotationDegrees);
        Assert.Equal(anchor.Transform.Scale, loaded.Transform.Scale);
    }

    [Fact]
    public void TryLoad_returns_null_for_a_location_with_no_saved_anchor()
    {
        var store = new AnchorStore(_dir);

        Assert.Null(store.TryLoad("zone_1_2_3"));
    }

    [Fact]
    public void Delete_removes_the_saved_anchor()
    {
        var store = new AnchorStore(_dir);
        var anchor = new ScreenAnchor("zone_1_2_3", new ScreenTransform());
        store.Save(anchor);

        store.Delete("zone_1_2_3");

        Assert.Null(store.TryLoad("zone_1_2_3"));
    }
}
