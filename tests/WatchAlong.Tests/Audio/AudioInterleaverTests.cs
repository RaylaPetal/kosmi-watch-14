using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class AudioInterleaverTests
{
    [Fact]
    public void Interleaves_left_and_right_channels_sample_by_sample()
    {
        ReadOnlySpan<float> left = [1f, 2f, 3f];
        ReadOnlySpan<float> right = [10f, 20f, 30f];
        var destination = new float[6];

        AudioInterleaver.InterleaveStereo(left, right, destination);

        Assert.Equal([1f, 10f, 2f, 20f, 3f, 30f], destination);
    }

    [Fact]
    public void Throws_when_destination_is_too_small()
    {
        var left = new float[] { 1f, 2f };
        var right = new float[] { 3f, 4f };
        var destination = new float[3];

        Assert.Throws<ArgumentException>(() => AudioInterleaver.InterleaveStereo(left, right, destination));
    }
}
