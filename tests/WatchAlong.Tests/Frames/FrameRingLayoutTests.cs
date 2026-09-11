using System.Runtime.InteropServices;
using WatchAlong.Shared.Frames;
using Xunit;

namespace WatchAlong.Tests.Frames;

public class FrameRingLayoutTests
{
    [Fact]
    public void FrameRingHeader_size_is_8_byte_aligned()
    {
        var size = Marshal.SizeOf<FrameRingHeader>();

        Assert.Equal(0, size % 8);
        // uint + ushort + ushort + int + int + long + long + long, packed to 8-byte alignment.
        Assert.Equal(40, size);
    }

    [Fact]
    public void FrameSlotHeader_size_is_8_byte_aligned()
    {
        var size = Marshal.SizeOf<FrameSlotHeader>();

        Assert.Equal(0, size % 8);
        // long + int*7 + long + long, packed to 8-byte alignment.
        Assert.Equal(56, size);
    }

    [Theory]
    [InlineData(nameof(FrameRingHeader.PublishedSeq))]
    [InlineData(nameof(FrameRingHeader.WriterHeartbeatTicks))]
    public void FrameRingHeader_long_fields_are_8_byte_aligned(string fieldName)
    {
        var offset = Marshal.OffsetOf<FrameRingHeader>(fieldName).ToInt64();

        Assert.Equal(0, offset % 8);
    }

    [Theory]
    [InlineData(nameof(FrameSlotHeader.SeqBegin))]
    [InlineData(nameof(FrameSlotHeader.SeqEnd))]
    [InlineData(nameof(FrameSlotHeader.CaptureTicks))]
    public void FrameSlotHeader_long_fields_are_8_byte_aligned(string fieldName)
    {
        var offset = Marshal.OffsetOf<FrameSlotHeader>(fieldName).ToInt64();

        Assert.Equal(0, offset % 8);
    }

    [Fact]
    public void FrameRingHeader_magic_matches_spec_constant()
    {
        // 0x52464157 spells 'WAFR' little-endian, per design.md §6.5.
        Assert.Equal(0x52464157u, FrameRingHeader.ExpectedMagic);
    }
}
