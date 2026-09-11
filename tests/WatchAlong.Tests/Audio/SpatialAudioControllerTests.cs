using System.Numerics;
using WatchAlong.Shared.Audio;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Screens;
using WatchAlong.Tests.Screens;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class SpatialAudioControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wa-spatial-audio-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ScreenController MakeScreenController(Vector3 screenPosition)
    {
        var location = new FakeLocationSource { CurrentLocationKey = "house_1" };
        var controller = new ScreenController(location, new AnchorStore(_dir));
        controller.Place(new ScreenTransform { Position = screenPosition });
        return controller;
    }

    [Fact]
    public void No_active_anchor_sends_nothing()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "zone_1" };
        var screenController = new ScreenController(location, new AnchorStore(_dir));
        var sent = new List<SetAudioMessage>();

        var spatial = new SpatialAudioController(screenController, sent.Add) { MasterVolume = 0.8f };
        spatial.Tick(0, Vector3.Zero, new Vector3(0, 0, 1), Vector3.Zero);

        Assert.Empty(sent);
    }

    [Fact]
    public void Close_listener_gets_near_full_volume()
    {
        var screenController = MakeScreenController(new Vector3(1, 0, 0));
        var sent = new List<SetAudioMessage>();
        var spatial = new SpatialAudioController(screenController, sent.Add) { MasterVolume = 1f, MaxDistance = 40f };

        spatial.Tick(0, cameraPosition: Vector3.Zero, cameraForward: new Vector3(1, 0, 0), playerPosition: Vector3.Zero);

        Assert.Single(sent);
        Assert.True(sent[0].Volume > 0.9);
    }

    [Fact]
    public void Far_listener_gets_silence()
    {
        var screenController = MakeScreenController(new Vector3(100, 0, 0));
        var sent = new List<SetAudioMessage>();
        var spatial = new SpatialAudioController(screenController, sent.Add) { MasterVolume = 1f, MaxDistance = 40f };

        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero);

        Assert.Single(sent);
        Assert.Equal(0.0, sent[0].Volume, 0.001);
    }

    [Fact]
    public void Ticks_faster_than_the_interval_are_coalesced()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        var sent = new List<SetAudioMessage>();
        var spatial = new SpatialAudioController(screenController, sent.Add, tickIntervalMs: 50) { MasterVolume = 1f };

        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero);
        spatial.Tick(10, new Vector3(0, 0, 1), new Vector3(1, 0, 0), Vector3.Zero); // within 50ms, should be skipped

        Assert.Single(sent);
    }

    [Fact]
    public void Screen_to_the_right_pans_right()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        var sent = new List<SetAudioMessage>();
        var spatial = new SpatialAudioController(screenController, sent.Add) { MasterVolume = 1f };

        // Facing +Z, screen is at +X (to the right).
        spatial.Tick(0, Vector3.Zero, new Vector3(0, 0, 1), Vector3.Zero);

        Assert.Single(sent);
        Assert.True(sent[0].Pan > 0);
    }

    [Fact]
    public void Entering_range_of_an_active_screen_ducks_bgm()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        uint bgm = 100;
        var ducker = new BgmDucker(() => bgm, v => bgm = v, duckToPercent: 20);
        var spatial = new SpatialAudioController(screenController, _ => { }, ducker) { MaxDistance = 40f };

        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero, isMediaActive: true);

        Assert.True(ducker.IsDucked);
        Assert.Equal(20u, bgm);
    }

    [Fact]
    public void Leaving_range_restores_bgm()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        uint bgm = 100;
        var ducker = new BgmDucker(() => bgm, v => bgm = v, duckToPercent: 20);
        var spatial = new SpatialAudioController(screenController, _ => { }, ducker) { MaxDistance = 40f };
        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero);
        Assert.True(ducker.IsDucked);

        spatial.Tick(1000, new Vector3(1000, 0, 0), new Vector3(1, 0, 0), new Vector3(1000, 0, 0));

        Assert.False(ducker.IsDucked);
        Assert.Equal(100u, bgm);
    }

    [Fact]
    public void Inactive_media_does_not_duck_even_when_in_range()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        uint bgm = 100;
        var ducker = new BgmDucker(() => bgm, v => bgm = v, duckToPercent: 20);
        var spatial = new SpatialAudioController(screenController, _ => { }, ducker) { MaxDistance = 40f };

        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero, isMediaActive: false);

        Assert.False(ducker.IsDucked);
    }

    [Fact]
    public void No_active_anchor_restores_bgm()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "zone_1" };
        var screenController = new ScreenController(location, new AnchorStore(_dir));
        uint bgm = 20;
        var ducker = new BgmDucker(() => bgm, v => bgm = v);
        // Simulate an externally-tracked "was ducked" state by ducking once directly first.
        ducker.Duck();
        var spatial = new SpatialAudioController(screenController, _ => { }, ducker);

        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero);

        Assert.False(ducker.IsDucked);
    }

    [Fact]
    public void RevertToFlat_restores_bgm()
    {
        var screenController = MakeScreenController(new Vector3(5, 0, 0));
        uint bgm = 100;
        var ducker = new BgmDucker(() => bgm, v => bgm = v, duckToPercent: 20);
        var spatial = new SpatialAudioController(screenController, _ => { }, ducker);
        spatial.Tick(0, Vector3.Zero, new Vector3(1, 0, 0), Vector3.Zero);
        Assert.True(ducker.IsDucked);

        spatial.RevertToFlat(1000);

        Assert.False(ducker.IsDucked);
        Assert.Equal(100u, bgm);
    }

    [Fact]
    public void RevertToFlat_sends_master_volume_with_no_pan()
    {
        var location = new FakeLocationSource { CurrentLocationKey = "zone_1" };
        var screenController = new ScreenController(location, new AnchorStore(_dir));
        var sent = new List<SetAudioMessage>();
        var spatial = new SpatialAudioController(screenController, sent.Add) { MasterVolume = 0.6f, Muted = false };

        spatial.RevertToFlat(0);

        Assert.Single(sent);
        Assert.Equal(0.6, sent[0].Volume, 0.001);
        Assert.Equal(0.0, sent[0].Pan, 0.001);
    }
}
