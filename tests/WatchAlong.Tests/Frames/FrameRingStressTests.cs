using System.Runtime.InteropServices;
using WatchAlong.Shared.Frames;
using Xunit;

namespace WatchAlong.Tests.Frames;

public unsafe class FrameRingStressTests
{
    private const int Width = 8;
    private const int Height = 8;
    private const int Stride = Width * 4;
    private const int PixelBytes = Stride * Height;
    private const long FrameCount = 1_000_000;

    [Fact]
    public void Reader_never_returns_a_torn_frame_under_concurrent_publish()
    {
        var totalSize = FrameRingLayout.ComputeTotalSize(3, Width, Height);
        var basePtr = (byte*)NativeMemory.AlignedAlloc((nuint)totalSize, 64);

        try
        {
            var writer = FrameRingWriter.CreateAndInitialize(basePtr, 3, Width, Height);
            var reader = new FrameRingReader(basePtr);

            long acceptedReads = 0;
            long tornFramesDetected = 0;

            var writerThread = new Thread(() =>
            {
                var pixels = new byte[PixelBytes];
                for (long seq = 1; seq <= FrameCount; seq++)
                {
                    // Every byte in the frame encodes the sequence number, so any read that
                    // mixes bytes from two different publishes is detectable below.
                    Array.Fill(pixels, (byte)(seq & 0xFF));
                    writer.PublishFrame(pixels, Width, Height, Stride, 0, 0, Width, Height);
                }
            });

            var readerThread = new Thread(() =>
            {
                var destination = new byte[PixelBytes];
                // Poll while the writer runs, plus a short drain afterward for the last frame(s).
                while (writerThread.IsAlive || reader.LastPublishedSeq < FrameCount)
                {
                    if (!reader.TryReadLatest(destination, out var header))
                        continue;

                    var expected = (byte)(header.SeqBegin & 0xFF);
                    foreach (var b in destination)
                    {
                        if (b != expected)
                        {
                            Interlocked.Increment(ref tornFramesDetected);
                            break;
                        }
                    }

                    Interlocked.Increment(ref acceptedReads);
                }
            });

            writerThread.Start();
            readerThread.Start();
            writerThread.Join();
            readerThread.Join(TimeSpan.FromSeconds(30));

            Assert.Equal(0, tornFramesDetected);
            Assert.True(acceptedReads > 0, "Reader never accepted a single frame — test setup is broken.");
        }
        finally
        {
            NativeMemory.AlignedFree(basePtr);
        }
    }
}
