using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WatchAlong.Shared.Ipc;

/// <summary>
/// Encodes/decodes messages for the named-pipe control channel: a 4-byte little-endian
/// length prefix followed by UTF-8 JSON (specs/ipc-protocol/spec.md, design.md §7).
/// </summary>
public static class IpcCodec
{
    public static byte[] Encode(IpcMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);
        var buffer = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, json.Length);
        json.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    /// <summary>
    /// Attempts to decode one length-prefixed frame from <paramref name="payload"/> (the JSON
    /// body, without the length prefix). Returns false — rather than throwing — for anything
    /// unparsable or referencing an unknown message type, per spec "Unknown or malformed message":
    /// the caller should log and continue rather than tear down the connection.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out IpcMessage? message)
    {
        try
        {
            message = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.IpcMessage);
            return message is not null;
        }
        catch (JsonException)
        {
            message = null;
            return false;
        }
        catch (NotSupportedException)
        {
            // Thrown by System.Text.Json when the "t" discriminator doesn't match any
            // registered JsonDerivedType — i.e. an unknown message type.
            message = null;
            return false;
        }
    }

    public static string ToDebugString(IpcMessage message) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage));
}
