using WatchAlong.Shared.Frames;
using Xunit;

namespace WatchAlong.Tests.Frames;

public class SharedMemoryFrameRingTests : IDisposable
{
    // Named memory-mapped files (the production path, via SharedMemoryFrameRingWriter.Create)
    // aren't supported by the .NET Unix PAL, only by Windows/Wine — see the class doc comment.
    // These tests exercise the identical pointer/ring plumbing through the file-backed path
    // instead, which *is* supported cross-platform.
    private readonly string _backingFile = Path.Combine(Path.GetTempPath(), "watchalong-ring-test-" + Guid.NewGuid());

    public void Dispose()
    {
        if (File.Exists(_backingFile))
            File.Delete(_backingFile);
    }

    [Fact]
    public void Writer_creates_a_mapping_and_reader_opens_the_same_one()
    {
        const int width = 4, height = 4;

        using var writer = SharedMemoryFrameRingWriter.CreateFileBacked(_backingFile, 3, width, height);
        using var reader = SharedMemoryFrameRingReader.OpenFileBacked(_backingFile, writer.TotalSize);

        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x42);
        writer.Ring.PublishFrame(pixels, width, height, width * 4, contentX: 0, contentY: 0, contentW: width, contentH: height);

        var destination = new byte[width * height * 4];
        var ok = reader.Ring.TryReadLatest(destination, out var header);

        Assert.True(ok);
        Assert.Equal(1, header.SeqBegin);
        Assert.Equal(width, header.Width);
        Assert.Equal(height, header.Height);
        Assert.All(destination, b => Assert.Equal(0x42, b));
    }

    [Fact]
    public void Content_rectangle_travels_with_the_frame()
    {
        const int width = 8, height = 8;

        using var writer = SharedMemoryFrameRingWriter.CreateFileBacked(_backingFile, 3, width, height);
        using var reader = SharedMemoryFrameRingReader.OpenFileBacked(_backingFile, writer.TotalSize);

        var pixels = new byte[width * height * 4];
        writer.Ring.PublishFrame(pixels, width, height, width * 4, contentX: 1, contentY: 2, contentW: 3, contentH: 4);

        var destination = new byte[width * height * 4];
        reader.Ring.TryReadLatest(destination, out var header);

        Assert.Equal(1, header.ContentX);
        Assert.Equal(2, header.ContentY);
        Assert.Equal(3, header.ContentW);
        Assert.Equal(4, header.ContentH);
    }
}
