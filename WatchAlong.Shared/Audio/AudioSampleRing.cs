namespace WatchAlong.Shared.Audio;

/// <summary>
/// Lock-free single-producer/single-consumer ring buffer for interleaved float samples,
/// per design.md §6.6 ("a lock-free SPSC ring holding about 250 ms"). The CEF audio thread
/// writes, the NAudio playback thread reads — never the other way around.
/// </summary>
public sealed class AudioSampleRing(int capacitySamples)
{
    private readonly float[] _buffer = new float[capacitySamples];
    private long _writeIndex;
    private long _readIndex;

    public int CapacitySamples => _buffer.Length;

    public int AvailableToRead => (int)(Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex));

    /// <summary>Writes as many samples as fit; returns how many were actually written (older data is never overwritten — excess is dropped).</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        var readIdx = Volatile.Read(ref _readIndex);
        var free = _buffer.Length - (int)(writeIdx - readIdx);
        var toWrite = Math.Min(free, samples.Length);

        for (var i = 0; i < toWrite; i++)
            _buffer[(writeIdx + i) % _buffer.Length] = samples[i];

        Volatile.Write(ref _writeIndex, writeIdx + toWrite);
        return toWrite;
    }

    /// <summary>Reads as many samples as available, filling the rest of <paramref name="destination"/> with silence. Returns how many real samples were read.</summary>
    public int Read(Span<float> destination)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        var readIdx = Volatile.Read(ref _readIndex);
        var available = (int)(writeIdx - readIdx);
        var toRead = Math.Min(available, destination.Length);

        for (var i = 0; i < toRead; i++)
            destination[i] = _buffer[(readIdx + i) % _buffer.Length];

        for (var i = toRead; i < destination.Length; i++)
            destination[i] = 0f; // underrun: silence rather than stale/garbage data

        Volatile.Write(ref _readIndex, readIdx + toRead);
        return toRead;
    }
}
