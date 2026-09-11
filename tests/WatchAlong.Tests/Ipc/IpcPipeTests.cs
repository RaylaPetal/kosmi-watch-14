using System.IO.Pipes;
using WatchAlong.Shared.Ipc;
using Xunit;

namespace WatchAlong.Tests.Ipc;

public class IpcPipeTests
{
    [Fact]
    public async Task Handshake_and_a_command_round_trip_over_a_real_named_pipe()
    {
        var pipeName = "watchalong-test-" + Guid.NewGuid();

        await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var acceptTask = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(5000);
        await acceptTask;

        await using var server = new IpcPipe(serverStream); // plugin side
        await using var client = new IpcPipe(clientStream); // renderer side

        // Renderer announces itself first, per design.md §7 handshake.
        await client.SendAsync(new HelloMessage(1, "149.0.60", "149.0.7000.0", new CodecSupport(false, true, true, true)));
        var hello = await server.ReceiveAsync();
        Assert.IsType<HelloMessage>(hello);
        Assert.Equal(1, ((HelloMessage)hello!).Protocol);

        await server.SendAsync(new HelloAckMessage(1, "Info", IsWine: false));
        var ack = await client.ReceiveAsync();
        Assert.IsType<HelloAckMessage>(ack);

        // Plugin then sends a command; renderer receives it.
        var openRoom = new OpenRoomMessage("https://app.kosmi.io/room/sulync", "Ray", new ViewportSize(1280, 720), 30, VoiceMode.Off, false);
        await server.SendAsync(openRoom);
        var received = await client.ReceiveAsync();

        Assert.IsType<OpenRoomMessage>(received);
        Assert.Equal("https://app.kosmi.io/room/sulync", ((OpenRoomMessage)received!).RoomUrl);
    }

    [Fact]
    public async Task Malformed_frame_is_skipped_and_the_next_valid_message_still_arrives()
    {
        var pipeName = "watchalong-test-" + Guid.NewGuid();

        await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var acceptTask = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(5000);
        await acceptTask;

        await using var server = new IpcPipe(serverStream);
        await using var client = new IpcPipe(clientStream);

        // Write a garbage frame directly (bypassing IpcPipe.SendAsync), then a real message.
        var garbage = "{ not valid json"u8.ToArray();
        var lengthPrefix = BitConverter.GetBytes(garbage.Length);
        await clientStream.WriteAsync(lengthPrefix);
        await clientStream.WriteAsync(garbage);
        await clientStream.FlushAsync();

        await client.SendAsync(new ReloadMessage());

        var received = await server.ReceiveAsync();

        Assert.IsType<ReloadMessage>(received); // the garbage frame was skipped, not fatal
    }
}
