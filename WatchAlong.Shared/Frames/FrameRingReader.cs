namespace WatchAlong.Shared.Frames;

/// <summary>
/// Reads the newest complete frame from a ring initialized by <see cref="FrameRingWriter"/>,
/// per design.md §6.5's seqlock algorithm and specs/ipc-protocol/spec.md "Reader never sees
/// a torn frame": copy the slot unconditionally, then verify SeqBegin/SeqEnd still match the
/// sequence number that was current when the copy began — if not, the writer lapped the
/// reader mid-copy and the frame is discarded. The caller is expected to try again on its
/// next tick (e.g. the next `UiBuilder.Draw`), not to retry synchronously in a loop.
/// </summary>
public sealed unsafe class FrameRingReader
{
    private readonly byte* _basePtr;
    private long _lastSeen = -1;

    public FrameRingReader(byte* basePtr)
    {
        _basePtr = basePtr;
    }

    private FrameRingHeader* Header => (FrameRingHeader*)_basePtr;

    /// <summary>True once a valid ring header (magic + version) has been written by a producer.</summary>
    public bool IsInitialized => Header->Magic == FrameRingHeader.ExpectedMagic && Header->Version == FrameRingHeader.CurrentVersion;

    public long LastPublishedSeq => Volatile.Read(ref Header->PublishedSeq);

    public long WriterHeartbeatTicks => Volatile.Read(ref Header->WriterHeartbeatTicks);

    /// <summary>
    /// Copies the newest published frame's pixels into <paramref name="destination"/> and
    /// reports its header, if a new, non-torn frame is available. Returns false when there is
    /// nothing new (same seq as last time) or when a torn read was detected (destination is
    /// left untouched in that case; the previous frame should keep being displayed).
    /// </summary>
    public bool TryReadLatest(Span<byte> destination, out FrameSlotHeader header)
    {
        header = default;

        var seq = Volatile.Read(ref Header->PublishedSeq);
        if (seq == 0 || seq == _lastSeen)
            return false;

        var slotStride = Header->SlotStride;
        var slotCount = Header->SlotCount;
        var slot = seq % slotCount;
        var slotHeader = (FrameSlotHeader*)(_basePtr + FrameRingLayout.SlotOffset((int)slot, slotStride));

        // Unconditionally copy pixels first, then confirm below that the slot still belonged
        // to `seq` the whole time — never trust a pre-copy check, since the writer can start
        // overwriting immediately afterward.
        var maxPixelBytes = (int)(slotStride - FrameRingLayout.SlotHeaderSize);
        var pixelSpan = new ReadOnlySpan<byte>((byte*)slotHeader + FrameRingLayout.SlotHeaderSize, maxPixelBytes);
        pixelSpan[..Math.Min(destination.Length, maxPixelBytes)].CopyTo(destination);
        var width = slotHeader->Width;
        var height = slotHeader->Height;
        var stride = slotHeader->Stride;

        // The writer is a separate process — never trust its geometry blindly. A width/height/
        // stride that doesn't actually fit this slot's allocated capacity (or the ring's
        // negotiated max) must never reach the caller: downstream, this feeds unchecked
        // pointer arithmetic in Direct3D11VideoTexture.Upload, on the game's own process. A
        // seqlock-consistent-but-nonsensical header is still rejected here.
        if (width <= 0 || height <= 0 || stride < width * 4 ||
            width > Header->MaxWidth || height > Header->MaxHeight ||
            (long)height * stride > maxPixelBytes)
            return false;

        var contentX = slotHeader->ContentX;
        var contentY = slotHeader->ContentY;
        var contentW = slotHeader->ContentW;
        var contentH = slotHeader->ContentH;
        var captureTicks = slotHeader->CaptureTicks;

        // A full fence, not just Volatile.Read's one-directional acquire semantics: without it
        // this read could be reordered earlier by the JIT/CPU relative to the plain copies
        // above, letting a torn frame slip past undetected.
        Thread.MemoryBarrier();
        var seqBegin = Volatile.Read(ref slotHeader->SeqBegin);
        var seqEnd = Volatile.Read(ref slotHeader->SeqEnd);

        if (seqBegin != seq || seqEnd != seq)
            return false; // torn: the writer lapped us during or before the copy

        _lastSeen = seq;
        header = new FrameSlotHeader
        {
            SeqBegin = seq,
            Width = width,
            Height = height,
            Stride = stride,
            ContentX = contentX,
            ContentY = contentY,
            ContentW = contentW,
            ContentH = contentH,
            CaptureTicks = captureTicks,
            SeqEnd = seq,
        };
        return true;
    }
}
