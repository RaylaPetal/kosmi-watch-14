using WatchAlong.Shared.Ipc;
using Xunit;

namespace WatchAlong.Tests.Ipc;

public class IpcMessageRoundTripTests
{
    public static IEnumerable<object[]> AllMessages()
    {
        yield return [new HelloMessage(1, "149.0.60", "149.0.7345.0", new CodecSupport(true, true, true, true))];
        yield return [new HelloAckMessage(1, "Info", true)];
        yield return [new OpenRoomMessage("https://app.kosmi.io/room/sulync", "Ray (in-game)", new ViewportSize(1280, 720), 30, VoiceMode.Off, false)];
        yield return [new CloseRoomMessage()];
        yield return [new SetViewModeMessage(ViewMode.Theater)];
        yield return [new SetViewModeMessage(ViewMode.Full)];
        yield return [new SetViewportMessage(1920, 1080)];
        yield return [new SetFrameRateMessage(30)];
        yield return [new SetAudioMessage(0.62, false)];
        yield return [new SetAudioMessage(0.62, false, -0.31)];
        yield return [new SetVoiceModeMessage(VoiceMode.Flat)];
        yield return [new InputMessage(InputKind.MouseMove, X: 640, Y: 402)];
        yield return [new InputMessage(InputKind.MouseButton, X: 640, Y: 402, Button: "Left", Up: false, Clicks: 1)];
        yield return [new InputMessage(InputKind.Wheel, X: 640, Y: 402, Dx: 0, Dy: -3)];
        yield return [new InputMessage(InputKind.Key, Vk: 13, Up: false, Mods: 0)];
        yield return [new InputMessage(InputKind.Text, Text: "hello")];
        yield return [new SetProfileMessage("""{"schema":1}""")];
        yield return [new SetAnchorMessage(0.5, 1.2, -3.0, 90, 0, 0, 3.0, 1.6875)];
        yield return [new ClearAnchorMessage()];
        yield return [new SetRenderModeMessage(ScreenRenderMode.Quad)];
        yield return [new SetRenderModeMessage(ScreenRenderMode.DepthTested)];
        yield return [new ReloadMessage()];
        yield return [new DebugSnapshotMessage()];
        yield return [new ShutdownMessage()];
        yield return [new FrameRingInfoMessage("WatchAlong-frames-1234", 3, 1920, 1080)];
        yield return [new PageStateMessage("JoinGate", null)];
        yield return [new MediaStateMessage("webrtc", 1280, 720, false, null, null, null, [0, 0, 1280, 720])];
        yield return [new RoomInfoMessage("Movie night", ["Ray", "Aya (in-game)"], "Ray")];
        yield return [new AudioStatsMessage(0, 62.5)];
        yield return [new ErrorMessage("NavigationBlocked", "Blocked top-level navigation to https://example.com")];
        yield return [new LogMessage("Info", "renderer ready")];
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void RoundTrips_through_encode_and_decode(IpcMessage original)
    {
        var encoded = IpcCodec.Encode(original);

        // Strip the 4-byte length prefix to get the JSON payload, and verify it matches.
        var length = BitConverter.ToInt32(encoded, 0);
        Assert.Equal(encoded.Length - 4, length);

        var ok = IpcCodec.TryDecode(encoded.AsSpan(4), out var decoded);

        Assert.True(ok);
        // Compare via re-serialization rather than record Equals(): several messages carry
        // array-typed fields (Rect, Members), and record-generated equality is reference-based
        // for arrays, so it would fail even on a correct round-trip.
        Assert.Equal(IpcCodec.ToDebugString(original), IpcCodec.ToDebugString(decoded!));
    }

    [Fact]
    public void TryDecode_returns_false_for_malformed_json()
    {
        var ok = IpcCodec.TryDecode("{ not json"u8, out var message);

        Assert.False(ok);
        Assert.Null(message);
    }

    [Fact]
    public void TryDecode_returns_false_for_unknown_message_type()
    {
        var ok = IpcCodec.TryDecode("""{"t":"SomethingThatDoesNotExist"}"""u8, out var message);

        Assert.False(ok);
        Assert.Null(message);
    }
}
