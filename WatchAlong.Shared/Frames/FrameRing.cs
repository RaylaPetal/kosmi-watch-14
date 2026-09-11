using System.Runtime.InteropServices;

namespace WatchAlong.Shared.Frames;

/// <summary>
/// Shared-memory frame ring layout v1, per design.md §6.5. All fields little-endian,
/// 8-byte aligned so they can be read/written with <see cref="System.Threading.Volatile"/>
/// and torn-free on both x64 Windows and Wine.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameRingHeader
{
    public const uint ExpectedMagic = 0x52464157; // 'WAFR'
    public const ushort CurrentVersion = 1;

    public uint Magic;
    public ushort Version;
    public ushort SlotCount;
    public int MaxWidth;
    public int MaxHeight;
    public long SlotStride;
    public long PublishedSeq;
    public long WriterHeartbeatTicks;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameSlotHeader
{
    public long SeqBegin;
    public int Width;
    public int Height;
    public int Stride;
    public int ContentX;
    public int ContentY;
    public int ContentW;
    public int ContentH;
    public long CaptureTicks;
    public long SeqEnd;
}
