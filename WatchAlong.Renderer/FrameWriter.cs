using CefSharp.OffScreen;
using WatchAlong.Shared.Frames;

namespace WatchAlong.Renderer;

/// <summary>
/// Thin glue between CefSharp's <see cref="ChromiumWebBrowser.Paint"/> event and the shared
/// seqlock ring (design.md §6.5). All the actual ring logic lives in
/// <see cref="SharedMemoryFrameRingWriter"/>/<see cref="FrameRingWriter"/> (WatchAlong.Shared),
/// which is unit-tested independent of CEF.
/// </summary>
public sealed unsafe class FrameWriter : IDisposable
{
    private readonly SharedMemoryFrameRingWriter _ring;
    private readonly int _maxWidth;
    private readonly int _maxHeight;
    private bool _loggedFirstPaintSize;
    private (int X, int Y, int W, int H)? _contentRect;

    public string MapName { get; }
    public long TotalSize => _ring.TotalSize;

    public FrameWriter(string mapName, ushort slotCount, int maxWidth, int maxHeight)
    {
        MapName = mapName;
        _maxWidth = maxWidth;
        _maxHeight = maxHeight;
        _ring = SharedMemoryFrameRingWriter.Create(mapName, slotCount, maxWidth, maxHeight);
    }

    /// <summary>Updates the content rect stamped into subsequent frames (from the page agent's reported rect, §5.5).</summary>
    public void SetContentRect(int x, int y, int w, int h) => _contentRect = (x, y, w, h);

    /// <summary>
    /// Updates the ring's liveness heartbeat without publishing a frame. The watchdog
    /// (RendererWatchdog.StallThreshold, 5s) treats a stale heartbeat as a hung renderer — but
    /// the heartbeat otherwise only advances on OnPaint, and a page can easily take longer than
    /// 5s to produce its first paint (CEF/Chromium cold start under Wine, first navigation).
    /// Without an independent heartbeat, the watchdog would kill and restart a renderer that's
    /// simply still loading, over and over, before the page ever finishes — call this
    /// periodically regardless of paint activity to keep "still loading" from looking "hung".
    /// </summary>
    public void Heartbeat() => _ring.Ring.Heartbeat();

    public void OnPaint(object? sender, OnPaintEventArgs e)
    {
        if (e.IsPopup)
            return; // only the main view is published; popups aren't shown in Phase 1

        // The reader's scratch buffer and the shared ring's slots are sized for maxWidth x
        // maxHeight (the negotiated FrameRingInfo handshake) — a paint callback bigger than
        // that (an unexpected browser resize) must be dropped here rather than publishing a
        // header whose Width/Height overstate what actually fit in the slot.
        if (e.Width <= 0 || e.Height <= 0 || e.Width > _maxWidth || e.Height > _maxHeight)
            return;

        // TEMP DIAGNOSTIC: confirms whether ChromiumWebBrowser.Size (set to maxWidth x maxHeight in
        // KosmiBrowser's constructor) actually took effect on the real captured paint, or whether
        // the browser is still painting at some other size (e.g. CefSharp's own 1366x768 default).
        if (!_loggedFirstPaintSize)
        {
            _loggedFirstPaintSize = true;
            // stdout, not stderr: this is a routine one-time diagnostic, not a failure — on
            // stderr it showed up as an ERR-level plugin log line despite everything working.
            Console.WriteLine($"WatchAlong.Renderer: first paint size {e.Width}x{e.Height} (max {_maxWidth}x{_maxHeight})");
        }

        var pixels = new ReadOnlySpan<byte>((void*)e.BufferHandle, e.Width * e.Height * 4);

        // Crop to the page agent's detected content rect (e.g. to strip a page header above the
        // player that theater mode doesn't always suppress — see MaxHeight's comment). Clamped to
        // the actual paint bounds: MaxHeight carries intentional headroom above the player's own
        // 1080, so a rect reported before that headroom is confirmed present, or one that's stale
        // from a previous smaller page, could otherwise claim pixels outside this buffer.
        var (x, y, w, h) = _contentRect ?? (0, 0, e.Width, e.Height);
        x = Math.Clamp(x, 0, e.Width);
        y = Math.Clamp(y, 0, e.Height);
        w = Math.Clamp(w, 0, e.Width - x);
        h = Math.Clamp(h, 0, e.Height - y);
        if (w <= 0 || h <= 0)
        {
            x = 0; y = 0; w = e.Width; h = e.Height;
        }

        _ring.Ring.PublishFrame(pixels, e.Width, e.Height, e.Width * 4, x, y, w, h);
    }

    public void Dispose() => _ring.Dispose();
}
