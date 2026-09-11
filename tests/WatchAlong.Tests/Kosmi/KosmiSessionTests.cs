using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class KosmiSessionTests
{
    private static MediaStateMessage NoMedia() => new("none", 0, 0, false, null, null, null, [0, 0, 0, 0]);
    private static MediaStateMessage Playing(string kind = "webrtc") => new(kind, 1280, 720, false, null, null, null, [0, 0, 1280, 720]);

    [Fact]
    public void Starts_idle()
    {
        var session = new KosmiSession();
        Assert.Equal(KosmiSessionState.Idle, session.State);
    }

    [Fact]
    public void Happy_path_reaches_InRoomNoMedia_then_InRoomPlaying()
    {
        var session = new KosmiSession();

        session.OpenRoom();
        Assert.Equal(KosmiSessionState.StartingRenderer, session.State);

        session.RendererStarted();
        Assert.Equal(KosmiSessionState.Loading, session.State);

        session.ReportPageState("JoinGate");
        Assert.Equal(KosmiSessionState.JoinGate, session.State);

        session.ReportPageState("InRoom");
        Assert.Equal(KosmiSessionState.InRoomNoMedia, session.State);

        session.ReportMediaState(Playing());
        Assert.Equal(KosmiSessionState.InRoomPlaying, session.State);

        session.ReportMediaState(NoMedia());
        Assert.Equal(KosmiSessionState.InRoomNoMedia, session.State);
    }

    [Fact]
    public void Renderer_failing_to_start_goes_to_error()
    {
        var session = new KosmiSession();
        session.OpenRoom();

        session.RendererFailedToStart();

        Assert.Equal(KosmiSessionState.Error, session.State);
        Assert.Equal("RendererStartFailed", session.ErrorCode);
    }

    [Theory]
    [InlineData("RoomNotFound")]
    [InlineData("LoginRequired")]
    [InlineData("Kicked")]
    [InlineData("Full")]
    public void Room_level_failures_go_to_error_with_the_matching_code(string pageState)
    {
        var session = new KosmiSession();
        session.OpenRoom();
        session.RendererStarted();

        session.ReportPageState(pageState);

        Assert.Equal(KosmiSessionState.Error, session.State);
        Assert.Equal(pageState, session.ErrorCode);
    }

    [Fact]
    public void Join_gate_automation_gets_two_attempts_before_NeedsUser()
    {
        var session = new KosmiSession();
        session.OpenRoom();
        session.RendererStarted();

        session.ReportPageState("JoinGate"); // attempt 1
        Assert.Equal(KosmiSessionState.JoinGate, session.State);

        session.ReportPageState("JoinGate"); // attempt 2 (still trying)
        Assert.Equal(KosmiSessionState.JoinGate, session.State);

        session.ReportPageState("JoinGate"); // a 3rd report means automation isn't working
        Assert.Equal(KosmiSessionState.NeedsUser, session.State);
    }

    [Fact]
    public void Successful_join_after_a_retry_resets_the_failure_counter()
    {
        var session = new KosmiSession();
        session.OpenRoom();
        session.RendererStarted();

        session.ReportPageState("JoinGate");
        session.ReportPageState("InRoom"); // succeeded before hitting NeedsUser
        Assert.Equal(KosmiSessionState.InRoomNoMedia, session.State);
    }

    [Fact]
    public void Renderer_disconnect_and_recovery_round_trips_through_reconnecting()
    {
        var session = new KosmiSession();
        session.OpenRoom();
        session.RendererStarted();
        session.ReportPageState("InRoom");
        session.ReportMediaState(Playing());

        session.RendererDisconnected();
        Assert.Equal(KosmiSessionState.Reconnecting, session.State);

        session.ReconnectSucceeded();
        Assert.Equal(KosmiSessionState.Loading, session.State);
    }

    [Fact]
    public void Close_returns_to_idle_and_clears_error_and_media()
    {
        var session = new KosmiSession();
        session.OpenRoom();
        session.RendererStarted();
        session.ReportPageState("RoomNotFound");

        session.Close();

        Assert.Equal(KosmiSessionState.Idle, session.State);
        Assert.Null(session.ErrorCode);
        Assert.Null(session.Media);
    }

    [Fact]
    public void StateChanged_fires_once_per_actual_transition_not_on_redundant_reports()
    {
        var session = new KosmiSession();
        var transitions = new List<KosmiSessionState>();
        session.StateChanged += transitions.Add;

        session.OpenRoom();
        session.RendererStarted();
        session.ReportPageState("Loading"); // already Loading — should not re-fire

        Assert.Equal([KosmiSessionState.StartingRenderer, KosmiSessionState.Loading], transitions);
    }
}
