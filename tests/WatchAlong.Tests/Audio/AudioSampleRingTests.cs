using WatchAlong.Shared.Audio;
using Xunit;

namespace WatchAlong.Tests.Audio;

public class AudioSampleRingTests
{
    [Fact]
    public void Write_then_read_round_trips_samples_in_order()
    {
        var ring = new AudioSampleRing(capacitySamples: 16);
        var written = ring.Write([1f, 2f, 3f, 4f]);

        var destination = new float[4];
        var read = ring.Read(destination);

        Assert.Equal(4, written);
        Assert.Equal(4, read);
        Assert.Equal([1f, 2f, 3f, 4f], destination);
    }

    [Fact]
    public void Underrun_fills_remaining_destination_with_silence_instead_of_stale_data()
    {
        var ring = new AudioSampleRing(capacitySamples: 16);
        ring.Write([1f, 2f]);

        var destination = new float[5];
        var read = ring.Read(destination);

        Assert.Equal(2, read);
        Assert.Equal([1f, 2f, 0f, 0f, 0f], destination);
    }

    [Fact]
    public void Writing_more_than_capacity_drops_the_excess_rather_than_overwriting_unread_data()
    {
        var ring = new AudioSampleRing(capacitySamples: 4);
        var written = ring.Write([1f, 2f, 3f, 4f, 5f, 6f]);

        Assert.Equal(4, written);

        var destination = new float[4];
        ring.Read(destination);
        Assert.Equal([1f, 2f, 3f, 4f], destination); // not [3,4,5,6] — nothing unread was clobbered
    }

    [Fact]
    public void Wraps_around_the_buffer_correctly()
    {
        var ring = new AudioSampleRing(capacitySamples: 4);
        ring.Write([1f, 2f, 3f]);
        ring.Read(new float[3]);

        ring.Write([4f, 5f, 6f]); // wraps past the end of the underlying array

        var destination = new float[3];
        ring.Read(destination);
        Assert.Equal([4f, 5f, 6f], destination);
    }
}
