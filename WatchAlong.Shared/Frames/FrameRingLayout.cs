using System.Runtime.InteropServices;

namespace WatchAlong.Shared.Frames;

/// <summary>Sizing/offset math shared by the writer and reader, per design.md §6.5.</summary>
public static class FrameRingLayout
{
    public static readonly int HeaderSize = Marshal.SizeOf<FrameRingHeader>();
    public static readonly int SlotHeaderSize = Marshal.SizeOf<FrameSlotHeader>();

    private const int Alignment = 64;

    /// <summary>Bytes per slot: slot header + a full BGRA frame at the ring's max resolution, 64-byte aligned.</summary>
    public static long ComputeSlotStride(int maxWidth, int maxHeight)
    {
        long raw = SlotHeaderSize + (long)maxWidth * maxHeight * 4;
        return (raw + (Alignment - 1)) / Alignment * Alignment;
    }

    public static long ComputeTotalSize(int slotCount, int maxWidth, int maxHeight) =>
        HeaderSize + slotCount * ComputeSlotStride(maxWidth, maxHeight);

    public static long SlotOffset(int slot, long slotStride) => HeaderSize + slot * slotStride;
}
