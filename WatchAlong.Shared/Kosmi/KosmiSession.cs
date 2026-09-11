using WatchAlong.Shared.Ipc;

namespace WatchAlong.Shared.Kosmi;

public enum KosmiSessionState
{
    Idle,
    StartingRenderer,
    Loading,
    JoinGate,
    NeedsUser,
    InRoomNoMedia,
    InRoomPlaying,
    Reconnecting,
    Error,
}

/// <summary>
/// The plugin-side session state machine, restricted to the Phase 1 states from design.md
/// §8.1 (no groups/venues): Idle → StartingRenderer → Loading → JoinGate →
/// InRoom.{NoMedia,Playing} / NeedsUser / Error / Reconnecting. Driven entirely by explicit
/// calls (renderer lifecycle events, <c>PageState</c>/<c>MediaState</c> IPC messages) so it has
/// no dependency on the actual renderer process or Dalamud and can be unit tested directly,
/// per specs/kosmi-session/spec.md "Session state is always observable".
/// </summary>
public sealed class KosmiSession
{
    public KosmiSessionState State { get; private set; } = KosmiSessionState.Idle;
    public string? ErrorCode { get; private set; }
    public MediaStateMessage? Media { get; private set; }
    public RoomInfoMessage? RoomInfo { get; private set; }

    private int _consecutiveJoinGateReports;

    public event Action<KosmiSessionState>? StateChanged;

    public void OpenRoom()
    {
        _consecutiveJoinGateReports = 0;
        ErrorCode = null;
        Media = null;
        SetState(KosmiSessionState.StartingRenderer);
    }

    public void RendererStarted() => SetState(KosmiSessionState.Loading);

    public void RendererFailedToStart() => SetError("RendererStartFailed");

    /// <summary>Applies a <c>PageState</c> message from the renderer's page agent.</summary>
    public void ReportPageState(string pageState)
    {
        switch (pageState)
        {
            case "Loading":
                SetState(KosmiSessionState.Loading);
                _consecutiveJoinGateReports = 0;
                break;

            case "JoinGate":
                _consecutiveJoinGateReports++;
                // Per design.md §6.4 item 2: automation gets two attempts before falling back.
                if (_consecutiveJoinGateReports > 2)
                    SetState(KosmiSessionState.NeedsUser);
                else
                    SetState(KosmiSessionState.JoinGate);
                break;

            case "RoomNotFound":
                SetError("RoomNotFound");
                break;

            case "LoginRequired":
                SetError("LoginRequired");
                break;

            case "Kicked":
                SetError("Kicked");
                break;

            case "Full":
                SetError("Full");
                break;

            case "InRoom":
                _consecutiveJoinGateReports = 0;
                if (State != KosmiSessionState.InRoomPlaying)
                    SetState(KosmiSessionState.InRoomNoMedia);
                break;
        }
    }

    /// <summary>Applies a <c>MediaState</c> message: media becomes active, or goes back to none.</summary>
    public void ReportMediaState(MediaStateMessage media)
    {
        Media = media;
        SetState(media.Kind is "none" or null or "" ? KosmiSessionState.InRoomNoMedia : KosmiSessionState.InRoomPlaying);
    }

    public void ReportRoomInfo(RoomInfoMessage info) => RoomInfo = info;

    /// <summary>Applies an <c>Error</c> IPC message (e.g. blocked navigation, audio init failure) — specs/kosmi-session/spec.md diagnosability, task 6.4.</summary>
    public void ReportError(string code) => SetError(code);

    /// <summary>The renderer process crashed or the connection dropped — retry with backoff (owned by the caller/watchdog).</summary>
    public void RendererDisconnected() => SetState(KosmiSessionState.Reconnecting);

    /// <summary>Called once the renderer has been relaunched and the room replayed.</summary>
    public void ReconnectSucceeded() => SetState(KosmiSessionState.Loading);

    public void Close()
    {
        _consecutiveJoinGateReports = 0;
        ErrorCode = null;
        Media = null;
        RoomInfo = null;
        SetState(KosmiSessionState.Idle);
    }

    private void SetError(string code)
    {
        ErrorCode = code;
        SetState(KosmiSessionState.Error);
    }

    private void SetState(KosmiSessionState state)
    {
        if (State == state)
            return;

        State = state;
        StateChanged?.Invoke(state);
    }
}
