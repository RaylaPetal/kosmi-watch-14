using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class RendererSupervisorTests
{
    private sealed class FakeRendererProcess : IRendererProcess
    {
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public bool ThrowOnStart { get; set; }

        public void Start()
        {
            StartCount++;
            if (ThrowOnStart)
                throw new InvalidOperationException("simulated spawn failure");

            IsRunning = true;
        }

        public void Stop() => IsRunning = false;
    }

    private static OpenRoomMessage Room(string code) =>
        new($"https://app.kosmi.io/room/{code}", "Ray", new ViewportSize(1280, 720), 30, VoiceMode.Off, false);

    [Fact]
    public void Opening_a_room_starts_exactly_one_renderer_process()
    {
        var process = new FakeRendererProcess();
        var supervisor = new RendererSupervisor(process, new RendererWatchdog());

        supervisor.OpenRoom(Room("first"));

        Assert.Equal(1, process.StartCount);
        Assert.True(process.IsRunning);
    }

    [Fact]
    public void Opening_a_second_room_closes_the_first_in_the_same_process_instead_of_spawning_another()
    {
        var process = new FakeRendererProcess();
        var supervisor = new RendererSupervisor(process, new RendererWatchdog());
        var closeCount = 0;
        supervisor.SendCloseRoom += () => closeCount++;

        supervisor.OpenRoom(Room("first"));
        supervisor.OpenRoom(Room("second"));

        Assert.Equal(1, process.StartCount); // never a second process
        Assert.Equal(1, closeCount); // first room was closed before the second opened
        Assert.Equal("https://app.kosmi.io/room/second", supervisor.CurrentRoom!.RoomUrl);
    }

    [Fact]
    public void Renderer_restart_replays_the_last_open_room_command_automatically()
    {
        var process = new FakeRendererProcess();
        var watchdog = new RendererWatchdog();
        var supervisor = new RendererSupervisor(process, watchdog);
        var replayed = new List<OpenRoomMessage>();
        supervisor.SendOpenRoom += replayed.Add;

        supervisor.OpenRoom(Room("movie-night"));
        supervisor.NotifyConnected(); // simulates the initial spawn's pipe connecting
        replayed.Clear(); // ignore the initial open, we only care about post-restart replay

        // Simulate the watchdog deciding the renderer crashed and restarting it.
        watchdog.Tick(DateTimeOffset.UtcNow, processRunning: false, lastHeartbeat: null);
        watchdog.Tick(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);
        supervisor.NotifyConnected(); // simulates the restarted process's pipe connecting

        Assert.Single(replayed);
        Assert.Equal("https://app.kosmi.io/room/movie-night", replayed[0].RoomUrl);
        Assert.Equal(2, process.StartCount); // original start + restart
    }

    [Fact]
    public void Restart_with_no_room_ever_opened_does_not_replay_anything()
    {
        var process = new FakeRendererProcess();
        var watchdog = new RendererWatchdog();
        var supervisor = new RendererSupervisor(process, watchdog);
        var replayed = new List<OpenRoomMessage>();
        supervisor.SendOpenRoom += replayed.Add;

        // Renderer was started but crashes before any OpenRoom was ever sent.
        process.Start();
        watchdog.Tick(DateTimeOffset.UtcNow, processRunning: false, lastHeartbeat: null);
        watchdog.Tick(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);

        Assert.Empty(replayed);
    }

    [Fact]
    public void Explicitly_closing_the_room_means_a_later_restart_does_not_replay_it()
    {
        var process = new FakeRendererProcess();
        var watchdog = new RendererWatchdog();
        var supervisor = new RendererSupervisor(process, watchdog);
        var replayed = new List<OpenRoomMessage>();
        supervisor.SendOpenRoom += replayed.Add;

        supervisor.OpenRoom(Room("movie-night"));
        supervisor.CloseRoom();
        replayed.Clear();

        watchdog.Tick(DateTimeOffset.UtcNow, processRunning: false, lastHeartbeat: null);
        watchdog.Tick(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);

        Assert.Empty(replayed);
    }

    [Fact]
    public void A_failed_spawn_from_OpenRoom_is_reported_instead_of_thrown()
    {
        var process = new FakeRendererProcess { ThrowOnStart = true };
        var supervisor = new RendererSupervisor(process, new RendererWatchdog());
        Exception? reported = null;
        supervisor.SpawnFailed += ex => reported = ex;

        // Must not throw — regression test for the crash reported live: an unhandled
        // exception here propagated through RendererWatchdog.Tick into the game's per-frame
        // update handler, spamming errors every tick and eventually corrupting plugin unload.
        var exception = Record.Exception(() => supervisor.OpenRoom(Room("first")));

        Assert.Null(exception);
        Assert.NotNull(reported);
        Assert.Equal("simulated spawn failure", reported!.Message);
    }

    [Fact]
    public void A_failed_spawn_during_an_automatic_restart_is_reported_instead_of_thrown()
    {
        var process = new FakeRendererProcess();
        var watchdog = new RendererWatchdog();
        var supervisor = new RendererSupervisor(process, watchdog);
        var failures = 0;
        supervisor.SpawnFailed += _ => failures++;

        supervisor.OpenRoom(Room("movie-night"));
        process.ThrowOnStart = true; // simulate the renderer exe going missing before a restart

        watchdog.Tick(DateTimeOffset.UtcNow, processRunning: false, lastHeartbeat: null);
        var exception = Record.Exception(() =>
            watchdog.Tick(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null));

        Assert.Null(exception);
        Assert.Equal(1, failures);
        Assert.Equal(WatchdogState.Restarting, watchdog.State); // still retrying, not stuck reporting healthy
    }
}
