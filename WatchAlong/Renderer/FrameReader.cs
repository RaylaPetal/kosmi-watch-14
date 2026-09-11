using System;
using WatchAlong.Screens.Ported;
using WatchAlong.Shared.Frames;
using WatchAlong.Shared.Ipc;

namespace WatchAlong.Renderer;

/// <summary>
/// Opens the renderer's shared-memory frame ring (by the name/size from its
/// <c>FrameRingInfo</c> handshake message, task 7.3) and uploads the newest frame into a
/// <see cref="Direct3D11VideoTexture"/> once per <c>UiBuilder.Draw</c>, per design.md §8.2.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private readonly SharedMemoryFrameRingReader _ring;
    private readonly byte[] _scratch;

    public FrameReader(string mapName, long totalSize, int maxWidth, int maxHeight)
    {
        _ring = SharedMemoryFrameRingReader.Open(mapName, totalSize);
        _scratch = new byte[maxWidth * maxHeight * 4];
    }

    /// <summary>The writer's last heartbeat, as a UTC timestamp (design.md §6.1.4 watchdog stall detection).</summary>
    public DateTimeOffset WriterHeartbeat => new(_ring.Ring.WriterHeartbeatTicks, TimeSpan.Zero);

    /// <summary>
    /// Task 7.3: opens the mapping the renderer just announced in its <c>FrameRingInfo</c>
    /// handshake message — same map name, same total size computed the same way the writer did.
    /// </summary>
    public static FrameReader FromHandshake(FrameRingInfoMessage info)
    {
        var totalSize = FrameRingLayout.ComputeTotalSize(info.Slots, info.MaxWidth, info.MaxHeight);
        return new FrameReader(info.MapName, totalSize, info.MaxWidth, info.MaxHeight);
    }

    /// <summary>
    /// Uploads the newest frame if one is available and not torn. Returns false (no memcpy
    /// performed beyond the ring's own read) when there's nothing new or the read was
    /// discarded as torn — the caller keeps showing the texture's current contents either way.
    /// </summary>
    public bool TryUpload(Direct3D11VideoTexture texture)
    {
        if (!_ring.Ring.TryReadLatest(_scratch, out var header))
            return false;

        var uv = ContentUv.FromSlotHeader(header);
        // header.Width/Height are already bounds-checked against the ring's negotiated max in
        // FrameRingReader, but _scratch is sized for that same max — this guards the slice
        // itself against ever exceeding the buffer we actually allocated.
        var pixelBytes = header.Width * header.Height * 4;
        if (pixelBytes <= 0 || pixelBytes > _scratch.Length)
            return false;

        return texture.Upload(_scratch.AsSpan(0, pixelBytes), header.Width, header.Height, header.Stride, uv);
    }

    public void Dispose() => _ring.Dispose();
}
