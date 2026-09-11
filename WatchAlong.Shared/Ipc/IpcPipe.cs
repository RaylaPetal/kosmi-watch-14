using System.IO.Pipes;
using WatchAlong.Shared.Ipc;

namespace WatchAlong.Shared.Ipc;

/// <summary>
/// Length-prefixed <see cref="IpcMessage"/> framing over a duplex named pipe (design.md §7:
/// <c>\\.\pipe\WatchAlong-&lt;gamePid&gt;-&lt;nonce&gt;</c>, 4-byte length prefix + UTF-8 JSON).
/// Wraps whatever <see cref="PipeStream"/> the caller already has open — a
/// <see cref="NamedPipeServerStream"/> on the plugin side, a <see cref="NamedPipeClientStream"/>
/// on the renderer side — so both ends share the exact same read/write/dispatch logic.
/// </summary>
public sealed class IpcPipe(PipeStream stream) : IAsyncDisposable
{
    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        var framed = IpcCodec.Encode(message);
        await stream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and decodes the next message. Returns null on a clean disconnect. Per
    /// specs/ipc-protocol/spec.md "Unknown or malformed message": a frame that fails to
    /// decode is skipped (logged by the caller) rather than tearing down the connection.
    /// </summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false))
                return null;

            var length = BitConverter.ToInt32(lengthBuffer);
            if (length is < 0 or > 16 * 1024 * 1024)
                return null; // absurd length: treat as a dead/corrupt connection rather than allocate

            var payload = new byte[length];
            if (!await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
                return null;

            if (IpcCodec.TryDecode(payload, out var message))
                return message;

            // Malformed/unknown message: per spec, discard and keep reading rather than disconnect.
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false; // pipe closed

            offset += read;
        }

        return true;
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
