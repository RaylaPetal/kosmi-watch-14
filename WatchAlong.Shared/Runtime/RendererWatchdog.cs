namespace WatchAlong.Shared.Runtime;

public enum WatchdogState { Healthy, Restarting, Failed }

/// <summary>
/// Detects a dead or stalled renderer and drives restart-with-backoff, per design.md §6.1.4
/// and specs/renderer-process/spec.md "Renderer process lifecycle" / "Repeated restart
/// failures". Driven by an external clock via <see cref="Tick"/> rather than owning a timer,
/// so backoff timing is deterministic to test.
/// </summary>
public sealed class RendererWatchdog
{
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
    ];

    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);

    private int _restartAttempt;
    private DateTimeOffset? _pendingRestartAt;

    public WatchdogState State { get; private set; } = WatchdogState.Healthy;

    /// <summary>Fired when it's time to actually kill/relaunch the renderer.</summary>
    public event Action? RestartRequested;

    /// <summary>Fired once all backoff attempts are exhausted — surface a manual-retry error to the user.</summary>
    public event Action? GaveUp;

    /// <param name="now">Current time.</param>
    /// <param name="processRunning">Whether the OS process is still alive.</param>
    /// <param name="lastHeartbeat">Timestamp of the last shmem heartbeat write, if any frame/heartbeat has ever been observed.</param>
    public void Tick(DateTimeOffset now, bool processRunning, DateTimeOffset? lastHeartbeat)
    {
        if (State == WatchdogState.Failed)
            return;

        if (_pendingRestartAt is { } scheduledAt)
        {
            if (now >= scheduledAt)
            {
                _pendingRestartAt = null;
                RestartRequested?.Invoke();
            }
            return;
        }

        var stalled = processRunning && lastHeartbeat is { } hb && now - hb > StallThreshold;
        var crashed = !processRunning;

        if (crashed || stalled)
            ScheduleRestart(now);
    }

    /// <summary>Call once the renderer is confirmed healthy again, so the next failure restarts backoff from the top.</summary>
    public void ReportHealthy()
    {
        _restartAttempt = 0;
        State = WatchdogState.Healthy;
    }

    /// <summary>User-triggered retry after <see cref="GaveUp"/> — bypasses backoff and retries immediately.</summary>
    public void RetryManually()
    {
        _restartAttempt = 0;
        State = WatchdogState.Healthy;
        RestartRequested?.Invoke();
    }

    private void ScheduleRestart(DateTimeOffset now)
    {
        if (_restartAttempt >= BackoffDelays.Length)
        {
            State = WatchdogState.Failed;
            GaveUp?.Invoke();
            return;
        }

        _pendingRestartAt = now + BackoffDelays[_restartAttempt];
        _restartAttempt++;
        State = WatchdogState.Restarting;
    }
}
