using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;
using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class KosmiSessionControllerTests
{
    private sealed class FakeRendererProcess : IRendererProcess
    {
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }

    private static KosmiSessionController MakeController(out List<OpenRoomMessage> sentOpenRoom, out int[] sentCloseRoomCount)
    {
        var supervisor = new RendererSupervisor(new FakeRendererProcess(), new RendererWatchdog());
        var opened = new List<OpenRoomMessage>();
        var closedCountBox = new int[1];
        supervisor.SendOpenRoom += opened.Add;
        supervisor.SendCloseRoom += () => closedCountBox[0]++;

        sentOpenRoom = opened;
        sentCloseRoomCount = closedCountBox;

        return new KosmiSessionController(supervisor);
    }

    [Fact]
    public void Valid_room_url_opens_the_room_end_to_end()
    {
        var controller = MakeController(out var opened, out _);

        var ok = controller.TryOpenRoom("https://app.kosmi.io/room/sulync", "Ray", 1280, 720, 30, VoiceMode.Off, false);

        Assert.True(ok);
        Assert.Equal(KosmiSessionState.StartingRenderer, controller.Session.State);
        // Not sent yet: a freshly-spawned renderer process hasn't connected its pipe in this
        // same synchronous call, so RendererSupervisor defers the actual send to NotifyConnected().
        Assert.Empty(opened);

        controller.ReportRendererStarted(); // simulates RendererClient's pipe connection completing

        Assert.Single(opened);
        Assert.Equal("https://app.kosmi.io/room/sulync", opened[0].RoomUrl);
    }

    [Theory]
    [InlineData("https://evil.example/phishing")]
    [InlineData("not a url")]
    [InlineData("https://app.kosmi.io.evil.tld/room/sulync")]
    public void Invalid_room_url_is_rejected_and_never_sent(string invalidInput)
    {
        var controller = MakeController(out var opened, out _);

        var ok = controller.TryOpenRoom(invalidInput, "Ray", 1280, 720, 30, VoiceMode.Off, false);

        Assert.False(ok);
        Assert.Empty(opened); // never reached an OpenRoom IPC message
        Assert.Equal(KosmiSessionState.Idle, controller.Session.State); // session never left Idle for bad input
    }

    [Fact]
    public void Renderer_page_and_media_state_messages_drive_the_session_to_InRoomPlaying()
    {
        var controller = MakeController(out _, out _);
        controller.TryOpenRoom("https://app.kosmi.io/room/sulync", "Ray", 1280, 720, 30, VoiceMode.Off, false);
        controller.ReportRendererStarted();

        controller.HandleRendererMessage(new PageStateMessage("JoinGate", null));
        controller.HandleRendererMessage(new PageStateMessage("InRoom", null));
        Assert.Equal(KosmiSessionState.InRoomNoMedia, controller.Session.State);

        controller.HandleRendererMessage(new MediaStateMessage("webrtc", 1280, 720, false, null, null, null, [0, 0, 1280, 720]));
        Assert.Equal(KosmiSessionState.InRoomPlaying, controller.Session.State);
    }

    [Fact]
    public void RoomInfo_message_is_captured_on_the_session()
    {
        var controller = MakeController(out _, out _);

        controller.HandleRendererMessage(new RoomInfoMessage("Movie night", ["Ray"], "Ray"));

        Assert.NotNull(controller.Session.RoomInfo);
        Assert.Equal("Movie night", controller.Session.RoomInfo!.Title);
    }

    [Fact]
    public void Error_message_surfaces_a_documented_human_readable_description()
    {
        var controller = MakeController(out _, out _);

        controller.HandleRendererMessage(new ErrorMessage("NavigationBlocked", "raw detail"));

        Assert.Equal(KosmiSessionState.Error, controller.Session.State);
        Assert.Equal("NavigationBlocked", controller.Session.ErrorCode);
        Assert.Contains("app.kosmi.io", KosmiErrorMessages.Describe(controller.Session.ErrorCode!));
    }

    [Fact]
    public void CloseRoom_closes_the_session_and_sends_CloseRoom()
    {
        var controller = MakeController(out _, out var closedCount);
        controller.TryOpenRoom("https://app.kosmi.io/room/sulync", "Ray", 1280, 720, 30, VoiceMode.Off, false);

        controller.CloseRoom();

        Assert.Equal(KosmiSessionState.Idle, controller.Session.State);
        Assert.True(closedCount[0] > 0);
    }
}
