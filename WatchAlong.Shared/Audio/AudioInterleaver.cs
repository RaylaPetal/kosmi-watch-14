namespace WatchAlong.Shared.Audio;

/// <summary>
/// Interleaves CEF's planar per-channel float buffers into a single interleaved stream
/// (design.md §6.6), independent of however the channel pointers were actually obtained
/// (so it can be unit tested without CefSharp).
/// </summary>
public static class AudioInterleaver
{
    public static void InterleaveStereo(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> destination)
    {
        if (destination.Length < left.Length * 2)
            throw new ArgumentException("Destination too small for interleaved stereo output.", nameof(destination));

        for (var i = 0; i < left.Length; i++)
        {
            destination[i * 2] = left[i];
            destination[i * 2 + 1] = right[i];
        }
    }
}
