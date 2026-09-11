namespace WatchAlong.Shared.Frames;

/// <summary>
/// Writes into a pre-sized shared-memory ring (design.md §6.5). The caller owns the backing
/// memory (a <see cref="System.IO.MemoryMappedFiles.MemoryMappedFile"/> view in production,
/// a pinned buffer in tests) — this class only knows the layout, not how the memory was obtained,
/// so it has no CEF or Dalamud dependency (specs/ipc-protocol/spec.md).
/// </summary>
public sealed unsafe class FrameRingWriter
{
    private readonly byte* _basePtr;
    private readonly long _slotStride;
    private readonly ushort _slotCount;

    /// <summary>Initializes the ring header at <paramref name="basePtr"/> and returns a writer over it.</summary>
    public static FrameRingWriter CreateAndInitialize(byte* basePtr, ushort slotCount, int maxWidth, int maxHeight)
    {
        var slotStride = FrameRingLayout.ComputeSlotStride(maxWidth, maxHeight);
        var header = (FrameRingHeader*)basePtr;
        *header = new FrameRingHeader
        {
            Magic = FrameRingHeader.ExpectedMagic,
            Version = FrameRingHeader.CurrentVersion,
            SlotCount = slotCount,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            SlotStride = slotStride,
            PublishedSeq = 0,
            WriterHeartbeatTicks = DateTime.UtcNow.Ticks,
        };
        return new FrameRingWriter(basePtr, slotCount, slotStride);
    }

    private FrameRingWriter(byte* basePtr, ushort slotCount, long slotStride)
    {
        _basePtr = basePtr;
        _slotCount = slotCount;
        _slotStride = slotStride;
    }

    private FrameRingHeader* Header => (FrameRingHeader*)_basePtr;

    private FrameSlotHeader* SlotHeader(long slot) =>
        (FrameSlotHeader*)(_basePtr + FrameRingLayout.SlotOffset((int)slot, _slotStride));

    private byte* SlotPixels(long slot) => (byte*)SlotHeader(slot) + FrameRingLayout.SlotHeaderSize;

    /// <summary>
    /// Publishes one BGRA frame: copies pixels into the next slot, stamps its header, and
    /// makes it visible to readers with a single <see cref="Volatile.Write(ref long, long)"/>
    /// on <c>PublishedSeq</c> — the seqlock's publish point.
    /// </summary>
    public void PublishFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, int contentX, int contentY, int contentW, int contentH)
    {
        var seq = Header->PublishedSeq + 1;
        var slot = seq % _slotCount;
        var slotHeader = SlotHeader(slot);

        Volatile.Write(ref slotHeader->SeqBegin, seq);
        // Full fence: guarantees the pixel/metadata writes below cannot be reordered ahead of
        // the SeqBegin write above, mirroring the fence on the reader's side of the check.
        Thread.MemoryBarrier();

        var maxPixelBytes = (int)(_slotStride - FrameRingLayout.SlotHeaderSize);
        var pixelSpan = new Span<byte>(SlotPixels(slot), maxPixelBytes);
        bgraPixels[..Math.Min(bgraPixels.Length, maxPixelBytes)].CopyTo(pixelSpan);

        slotHeader->Width = width;
        slotHeader->Height = height;
        slotHeader->Stride = stride;
        slotHeader->ContentX = contentX;
        slotHeader->ContentY = contentY;
        slotHeader->ContentW = contentW;
        slotHeader->ContentH = contentH;
        slotHeader->CaptureTicks = DateTime.UtcNow.Ticks;

        Thread.MemoryBarrier();
        Volatile.Write(ref slotHeader->SeqEnd, seq);

        Volatile.Write(ref Header->PublishedSeq, seq);
        Volatile.Write(ref Header->WriterHeartbeatTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Updates the heartbeat without publishing a frame (watchdog liveness while hidden).</summary>
    public void Heartbeat() => Volatile.Write(ref Header->WriterHeartbeatTicks, DateTime.UtcNow.Ticks);
}
