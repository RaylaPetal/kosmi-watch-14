using WatchAlong.Shared.Ipc;

namespace WatchAlong.Shared.Runtime;

/// <summary>Minimal process lifecycle the supervisor needs — lets tests fake it instead of spawning a real OS process.</summary>
public interface IRendererProcess
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}

/// <summary>Adapts <see cref="RendererProcessHost"/> to <see cref="IRendererProcess"/> for production use.</summary>
public sealed class RendererProcessAdapter(RendererProcessHost host, Func<RendererLaunchOptions> optionsFactory) : IRendererProcess
{
    public bool IsRunning => host.IsRunning;
    public void Start() => host.Start(optionsFactory());
    public void Stop() => host.Stop();
}

/// <summary>
/// Ties process lifecycle, the watchdog, and "what room are we in" together, per
/// specs/renderer-process/spec.md "Single active renderer" and design.md §6.1.5:
/// <list type="bullet">
/// <item>Opening a room while one is already open closes it in the SAME renderer process
/// instead of spawning a second one (never more than one <see cref="IRendererProcess"/> alive).</item>
/// <item>After a watchdog-triggered restart, the last <see cref="OpenRoomMessage"/> is replayed
/// automatically so the session resumes without the user re-entering anything.</item>
/// </list>
/// </summary>
public sealed class RendererSupervisor : IDisposable
{
    private readonly IRendererProcess _process;
    private readonly RendererWatchdog _watchdog;
    private OpenRoomMessage? _currentRoom;

    public event Action<OpenRoomMessage>? SendOpenRoom;
    public event Action? SendCloseRoom;

    /// <summary>
    /// Raised when actually spawning the renderer process throws (e.g. the executable isn't
    /// where it's expected). Never left to propagate as an unhandled exception — this fires on
    /// every call site that spawns, including the watchdog's automatic restarts, which run on
    /// the game's per-frame update and would otherwise spam an unhandled exception every tick.
    /// </summary>
    public event Action<Exception>? SpawnFailed;

    public RendererSupervisor(IRendererProcess process, RendererWatchdog watchdog)
    {
        _process = process;
        _watchdog = watchdog;
        _watchdog.RestartRequested += HandleRestartRequested;
    }

    public OpenRoomMessage? CurrentRoom => _currentRoom;

    public void EnsureStarted()
    {
        if (_process.IsRunning)
            return;

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            SpawnFailed?.Invoke(ex);
        }
    }

    /// <summary>Never spawns a second renderer: an already-open room is closed in-process first.</summary>
    public void OpenRoom(OpenRoomMessage message)
    {
        var wasRunning = _process.IsRunning;
        EnsureStarted();

        if (_currentRoom is not null)
            SendCloseRoom?.Invoke();

        _currentRoom = message;

        // A freshly-spawned process cannot possibly have connected its pipe yet in this same
        // synchronous call — RendererClient.SendAsync silently no-ops while its pipe is still
        // null, so sending now would just drop the message. NotifyConnected() (called once the
        // new process's pipe actually connects) replays _currentRoom instead.
        if (wasRunning && _process.IsRunning)
            SendOpenRoom?.Invoke(message);
    }

    /// <summary>
    /// Called once the renderer process's pipe connection is actually established (i.e. right
    /// after <see cref="KosmiSessionController.ReportRendererStarted"/> fires) — replays
    /// whatever room is current, covering both a fresh <see cref="OpenRoom"/> spawn and a
    /// watchdog-triggered restart, neither of which can safely send at spawn time (see above).
    /// </summary>
    public void NotifyConnected()
    {
        if (_currentRoom is { } room)
            SendOpenRoom?.Invoke(room);
    }

    public void CloseRoom()
    {
        if (_currentRoom is null)
            return;

        _currentRoom = null;
        SendCloseRoom?.Invoke();
    }

    private void HandleRestartRequested()
    {
        if (_process.IsRunning)
            _process.Stop();

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            // Don't report healthy and don't replay the room — the watchdog will detect
            // "still not running" on its next Tick and schedule the next backoff attempt on
            // its own; this handler must not throw back into the watchdog's caller.
            SpawnFailed?.Invoke(ex);
            return;
        }

        _watchdog.ReportHealthy();

        // Same race as OpenRoom(): the process was just (re)spawned and cannot have connected
        // its pipe yet, so replaying here would be silently dropped. NotifyConnected() does it
        // once the new connection is actually established.
    }

    public void Dispose()
    {
        _watchdog.RestartRequested -= HandleRestartRequested;
    }
}
