using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class RendererWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Healthy_process_never_triggers_a_restart()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        watchdog.RestartRequested += () => restarts++;

        for (var i = 0; i < 100; i++)
            watchdog.Tick(T0 + TimeSpan.FromSeconds(i), processRunning: true, lastHeartbeat: T0 + TimeSpan.FromSeconds(i));

        Assert.Equal(0, restarts);
        Assert.Equal(WatchdogState.Healthy, watchdog.State);
    }

    [Fact]
    public void Dead_process_restarts_after_1_second_backoff()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        watchdog.RestartRequested += () => restarts++;

        watchdog.Tick(T0, processRunning: false, lastHeartbeat: null);
        Assert.Equal(WatchdogState.Restarting, watchdog.State);
        Assert.Equal(0, restarts);

        watchdog.Tick(T0 + TimeSpan.FromMilliseconds(999), processRunning: false, lastHeartbeat: null);
        Assert.Equal(0, restarts);

        watchdog.Tick(T0 + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void Stalled_process_with_no_heartbeat_for_over_5_seconds_triggers_restart()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        watchdog.RestartRequested += () => restarts++;

        var lastHeartbeat = T0;

        // Process is alive but heartbeat stops advancing.
        watchdog.Tick(T0 + TimeSpan.FromSeconds(4), processRunning: true, lastHeartbeat);
        Assert.Equal(WatchdogState.Healthy, watchdog.State); // under the 5s threshold

        watchdog.Tick(T0 + TimeSpan.FromSeconds(6), processRunning: true, lastHeartbeat);
        Assert.Equal(WatchdogState.Restarting, watchdog.State); // stall detected, backoff scheduled
    }

    [Fact]
    public void Backoff_escalates_1_2_5_15_seconds_then_gives_up()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        var gaveUp = false;
        watchdog.RestartRequested += () => restarts++;
        watchdog.GaveUp += () => gaveUp = true;

        var now = T0;
        var expectedDelays = new[] { 1, 2, 5, 15 };

        foreach (var delaySeconds in expectedDelays)
        {
            watchdog.Tick(now, processRunning: false, lastHeartbeat: null); // detect failure, schedule restart
            Assert.Equal(WatchdogState.Restarting, watchdog.State);

            now += TimeSpan.FromSeconds(delaySeconds) - TimeSpan.FromMilliseconds(1);
            var restartsBefore = restarts;
            watchdog.Tick(now, processRunning: false, lastHeartbeat: null);
            Assert.Equal(restartsBefore, restarts); // not yet — one ms short

            now += TimeSpan.FromMilliseconds(1);
            watchdog.Tick(now, processRunning: false, lastHeartbeat: null);
            Assert.Equal(restartsBefore + 1, restarts); // fires exactly at the delay
        }

        Assert.False(gaveUp);

        // A 5th consecutive failure (after 4 restart attempts) exhausts the backoff table.
        watchdog.Tick(now, processRunning: false, lastHeartbeat: null);

        Assert.True(gaveUp);
        Assert.Equal(WatchdogState.Failed, watchdog.State);
        Assert.Equal(4, restarts); // no 5th restart was scheduled
    }

    [Fact]
    public void Failed_watchdog_ignores_further_ticks_until_manually_retried()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        watchdog.RestartRequested += () => restarts++;

        // Exhaust all 4 attempts.
        var now = T0;
        for (var i = 0; i < 4; i++)
        {
            watchdog.Tick(now, processRunning: false, lastHeartbeat: null);
            now += TimeSpan.FromSeconds(20);
            watchdog.Tick(now, processRunning: false, lastHeartbeat: null);
        }
        watchdog.Tick(now, processRunning: false, lastHeartbeat: null); // 5th failure -> gives up

        Assert.Equal(WatchdogState.Failed, watchdog.State);
        var restartsAtFailure = restarts;

        watchdog.Tick(now + TimeSpan.FromSeconds(100), processRunning: false, lastHeartbeat: null);
        Assert.Equal(restartsAtFailure, restarts); // still ignored

        watchdog.RetryManually();

        Assert.Equal(WatchdogState.Healthy, watchdog.State);
        Assert.Equal(restartsAtFailure + 1, restarts); // manual retry restarts immediately, no backoff wait
    }

    [Fact]
    public void ReportHealthy_resets_backoff_so_the_next_failure_starts_at_1_second_again()
    {
        var watchdog = new RendererWatchdog();
        var restarts = 0;
        watchdog.RestartRequested += () => restarts++;

        watchdog.Tick(T0, processRunning: false, lastHeartbeat: null);
        watchdog.Tick(T0 + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);
        Assert.Equal(1, restarts);

        watchdog.ReportHealthy();

        var now = T0 + TimeSpan.FromSeconds(100);
        watchdog.Tick(now, processRunning: false, lastHeartbeat: null);
        watchdog.Tick(now + TimeSpan.FromSeconds(1), processRunning: false, lastHeartbeat: null);

        Assert.Equal(2, restarts); // fired again after only 1s, proving backoff was reset
    }
}
