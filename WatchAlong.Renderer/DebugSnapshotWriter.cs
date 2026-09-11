using CefSharp.OffScreen;

namespace WatchAlong.Renderer;

/// <summary>
/// Saves a PNG plus the DOM-outline JSON (produced by <c>kosmi-agent.js</c>'s
/// <c>buildDomOutline</c>, text already stripped) when the page state is unrecognized for too
/// long or on an explicit <c>DebugSnapshot</c> command, per design.md §6.4 item 1 and
/// specs/kosmi-session/spec.md "Unrecognized page state is diagnosable".
/// </summary>
public sealed class DebugSnapshotWriter(string outputDirectory)
{
    public async Task SaveAsync(ChromiumWebBrowser browser, string domOutlineJson, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");

        var pngBytes = await browser.CaptureScreenshotAsync();
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"snapshot-{stamp}.png"), pngBytes, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"snapshot-{stamp}.dom.json"), domOutlineJson, cancellationToken);
    }
}
