using CefSharp;
using CefSharp.Enums;
using CefSharp.Structs;
using NAudio.Wave;
using WatchAlong.Shared.Audio;

namespace WatchAlong.Renderer;

/// <summary>
/// CEF's <see cref="IAudioHandler"/> callback → <see cref="AudioSampleRing"/> → NAudio
/// playback, per design.md §6.6. Once this handler is set, audio goes to us instead of the
/// speakers directly (§3.5). All the actual mixing/ramping/ring math lives in
/// WatchAlong.Shared.Audio, unit-tested there.
/// </summary>
public sealed class AudioSink : IAudioHandler
{
    private const int SampleRateHz = 48_000;
    private const int Channels = 2;

    private readonly AudioSampleRing _ring = new(SampleRateHz * Channels / 4); // ~250ms at 48kHz stereo
    private readonly GainRamp _gain = new();
    private IWavePlayer? _output;
    private int _underruns;

    public bool Muted { get; private set; }
    public double Volume { get; private set; } = 1.0;
    public int Underruns => _underruns;

    public void SetVolume(double volume, bool muted)
    {
        Volume = volume;
        Muted = muted;
        _gain.SetTarget(muted ? 0.0 : volume);
    }

    public bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref AudioParameters parameters)
    {
        parameters = new AudioParameters(ChannelLayout.LayoutStereo, SampleRateHz, 1024);
        return true;
    }

    public void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, AudioParameters parameters, int channels)
    {
        var provider = new RingBufferSampleProvider(_ring, _gain, SampleRateHz, Channels, () => Interlocked.Increment(ref _underruns));

        try
        {
            _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, latency: 80);
            _output.Init(provider);
            _output.Play();
        }
        catch (Exception)
        {
            // Fall back per design.md §6.6: WASAPI can fail to init or crackle under Wine.
            _output?.Dispose();
            _output = new WaveOutEvent { DesiredLatency = 100 };
            _output.Init(provider);
            _output.Play();
        }
    }

    public unsafe void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames, long pts)
    {
        // CEF delivers planar float channels: `data` points to an array of per-channel float* pointers.
        var channelPtrs = (IntPtr*)data;
        var left = new ReadOnlySpan<float>((float*)channelPtrs[0], noOfFrames);
        var right = Channels > 1 ? new ReadOnlySpan<float>((float*)channelPtrs[1], noOfFrames) : left;

        Span<float> interleaved = noOfFrames * Channels <= 4096
            ? stackalloc float[noOfFrames * Channels]
            : new float[noOfFrames * Channels];

        AudioInterleaver.InterleaveStereo(left, right, interleaved);
        _ring.Write(interleaved);
    }

    public void OnAudioStreamStopped(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        _output?.Stop();
    }

    public void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage)
    {
    }

    public void Dispose()
    {
        _output?.Dispose();
    }

    /// <summary>Pulls from the ring, applying the ramped gain, per design.md §6.6.</summary>
    private sealed class RingBufferSampleProvider(AudioSampleRing ring, GainRamp gain, int sampleRate, int channels, Action onUnderrun) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var span = buffer.AsSpan(offset, count);
            var read = ring.Read(span);
            if (read < count)
                onUnderrun();

            var msPerSample = 1000.0 / sampleRate / channels;
            for (var i = 0; i < span.Length; i++)
            {
                var g = gain.Advance(msPerSample);
                span[i] = (float)(span[i] * g);
            }

            return count;
        }
    }
}
