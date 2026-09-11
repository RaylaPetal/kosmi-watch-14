using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Runtime;

namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// Wires <see cref="KosmiSession"/> to a <see cref="RendererSupervisor"/>: user-facing
/// <see cref="TryOpenRoom"/>/<see cref="CloseRoom"/> commands go out, renderer IPC messages
/// come back in and update the session. Per specs/renderer-process/spec.md "User-provided room
/// URL validated before use" (task 6.3), an invalid URL is rejected here and never reaches an
/// <c>OpenRoom</c> message at all.
/// </summary>
public sealed class KosmiSessionController
{
    private readonly RendererSupervisor _supervisor;

    // Session.State only ever leaves StartingRenderer via ReportRendererStarted(), which fires
    // once per *new* pipe connection. If a room is opened while the renderer is already
    // connected (a repeat join, e.g. running /wa join twice), no new connection event will ever
    // arrive to move the state back out of StartingRenderer — it would get stuck there forever.
    private bool _rendererConnected;

    public KosmiSession Session { get; } = new();

    /// <summary>The normalized room code of the currently open (or last opened) room, or null before any room has been joined — the source an invite (tasks.md 5.1) is generated from.</summary>
    public string? CurrentRoomCode { get; private set; }

    /// <summary>Raised when the renderer process itself fails to spawn (distinct from a valid room address) — the caller should log the exception.</summary>
    public event Action<Exception>? RendererSpawnFailed;

    public KosmiSessionController(RendererSupervisor supervisor)
    {
        _supervisor = supervisor;
        // RendererSupervisor already catches spawn exceptions itself (so its own automatic
        // restarts never throw back into the watchdog) and reports them here instead.
        _supervisor.SpawnFailed += ex =>
        {
            Session.RendererFailedToStart();
            RendererSpawnFailed?.Invoke(ex);
        };
    }

    /// <summary>Returns false (and sends nothing) if <paramref name="roomUrlOrCode"/> doesn't validate.</summary>
    public bool TryOpenRoom(string roomUrlOrCode, string displayName, int viewportW, int viewportH, int fps, VoiceMode voiceMode, bool hideIp)
    {
        if (!KosmiRoomUrl.TryParse(roomUrlOrCode, out var code))
            return false;

        CurrentRoomCode = code;
        Session.OpenRoom();
        if (_rendererConnected)
            Session.RendererStarted(); // already connected: no future "started" event will fire to leave StartingRenderer

        var message = new OpenRoomMessage(KosmiRoomUrl.ToUrl(code), displayName, new ViewportSize(viewportW, viewportH), fps, voiceMode, hideIp);
        _supervisor.OpenRoom(message);
        return true;
    }

    public void CloseRoom()
    {
        CurrentRoomCode = null;
        Session.Close();
        _supervisor.CloseRoom();
    }

    /// <summary>Called once the renderer process has actually launched, before the handshake completes.</summary>
    public void ReportRendererStarted()
    {
        _rendererConnected = true;
        Session.RendererStarted();
        // The pipe connection was just established — this is the first safe point to send
        // OpenRoomMessage (see RendererSupervisor.OpenRoom/HandleRestartRequested, which defer
        // to here instead of sending at spawn time, when the connection can't exist yet).
        _supervisor.NotifyConnected();
    }

    public void ReportRendererFailedToStart() => Session.RendererFailedToStart();

    public void ReportRendererDisconnected()
    {
        _rendererConnected = false;
        Session.RendererDisconnected();
    }

    public void ReportReconnectSucceeded() => Session.ReconnectSucceeded();

    /// <summary>Applies one incoming IPC message from the renderer to the session, if it's one the session cares about.</summary>
    public void HandleRendererMessage(IpcMessage message)
    {
        switch (message)
        {
            case PageStateMessage pageState:
                Session.ReportPageState(pageState.State);
                break;

            case MediaStateMessage mediaState:
                Session.ReportMediaState(mediaState);
                break;

            case RoomInfoMessage roomInfo:
                Session.ReportRoomInfo(roomInfo);
                break;

            case ErrorMessage error:
                Session.ReportError(error.Code);
                break;
        }
    }
}
