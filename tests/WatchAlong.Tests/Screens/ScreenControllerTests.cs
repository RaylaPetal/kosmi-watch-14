using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public sealed class FakeLocationSource : ILocationSource
{
    public string? CurrentLocationKey { get; set; }

    public event Action<string>? LocationChanged;

    public void MoveTo(string key)
    {
        CurrentLocationKey = key;
        LocationChanged?.Invoke(key);
    }
}

public class ScreenControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wa-screen-controller-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Returning_to_a_location_with_a_saved_screen_restores_it()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);

        Assert.True(controller.Place(new ScreenTransform()));

        // Leave, then come back.
        location.MoveTo("zone_2");
        Assert.Null(controller.ActiveAnchor);

        location.MoveTo("house_1");
        Assert.NotNull(controller.ActiveAnchor);
        Assert.Equal("house_1", controller.ActiveAnchor!.LocationKey);
    }

    [Fact]
    public void No_saved_anchor_for_the_current_location_shows_no_world_screen()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "zone_3" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);

        Assert.Null(controller.ActiveAnchor);
    }

    [Fact]
    public void Removing_a_placement_deletes_it_so_it_does_not_restore_later()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);
        controller.Place(new ScreenTransform());

        controller.Remove();
        Assert.Null(controller.ActiveAnchor);

        location.MoveTo("zone_2");
        location.MoveTo("house_1");
        Assert.Null(controller.ActiveAnchor);
    }

    [Fact]
    public void Cancel_reverts_a_live_preview_to_the_last_saved_transform()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);
        var original = new ScreenTransform { Scale = new System.Numerics.Vector2(3, 1.6875f) };
        controller.Place(original);

        controller.Preview(original with { Scale = new System.Numerics.Vector2(5, 2.8f) });
        Assert.Equal(5, controller.ActiveAnchor!.Transform.Scale.X);

        controller.Cancel();
        Assert.Equal(3, controller.ActiveAnchor!.Transform.Scale.X);
    }

    [Fact]
    public void ApplyExternalAnchor_for_the_current_location_restores_it_immediately()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);
        var anchor = new ScreenAnchor("house_1", new ScreenTransform());

        controller.ApplyExternalAnchor(anchor);

        Assert.NotNull(controller.ActiveAnchor);
        Assert.Equal("house_1", controller.ActiveAnchor!.LocationKey);
    }

    [Fact]
    public void ApplyExternalAnchor_for_a_different_location_persists_without_showing_a_screen()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "zone_9" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);
        var anchor = new ScreenAnchor("house_1", new ScreenTransform());

        controller.ApplyExternalAnchor(anchor);

        Assert.Null(controller.ActiveAnchor);

        // world-screens spec "Anchor set by accepting an invite": it activates later, like any
        // other saved anchor, once the user actually visits that location.
        location.MoveTo("house_1");
        Assert.NotNull(controller.ActiveAnchor);
    }

    [Fact]
    public void Commit_persists_a_previewed_transform()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var store = new AnchorStore(_dir);
        var controller = new ScreenController(location, store);
        controller.Place(new ScreenTransform());

        controller.Preview(new ScreenTransform { Scale = new System.Numerics.Vector2(5, 2.8f) });
        controller.Commit();

        var reloaded = store.TryLoad("house_1");
        Assert.Equal(5, reloaded!.Transform.Scale.X);
    }
}
