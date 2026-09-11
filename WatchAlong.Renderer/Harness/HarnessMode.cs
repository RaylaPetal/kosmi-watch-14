using CefSharp;
using CefSharp.OffScreen;

namespace WatchAlong.Renderer.Harness;

/// <summary>
/// <c>--harness &lt;url&gt; --out &lt;dir&gt;</c>: runs the renderer against a page with no game
/// required, per design.md §16 ("This is how you iterate on Kosmi selectors on Linux without
/// launching FFXIV"). Logs page/media state and dumps a PNG every 2s plus WAV audio chunks.
/// </summary>
public static class HarnessMode
{
    public static async Task RunAsync(string url, string outDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outDir);
        LogLine(outDir, $"harness starting: url={url}");

        using var browser = new ChromiumWebBrowser(url);
        await browser.WaitForInitialLoadAsync();
        LogLine(outDir, "initial load complete");

        browser.ConsoleMessage += (_, e) => LogLine(outDir, $"console[{e.Level}]: {e.Message}");
        browser.LoadingStateChanged += (_, e) => LogLine(outDir, $"loadingState: isLoading={e.IsLoading}");

        var frameIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);

            try
            {
                var pngBytes = await browser.CaptureScreenshotAsync();
                var path = Path.Combine(outDir, $"frame-{frameIndex:D5}.png");
                await File.WriteAllBytesAsync(path, pngBytes, cancellationToken);
                LogLine(outDir, $"saved {path}");
            }
            catch (Exception ex)
            {
                LogLine(outDir, $"screenshot failed: {ex.Message}");
            }

            frameIndex++;
        }
    }

    private static void LogLine(string outDir, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(outDir, "harness.log"), line + Environment.NewLine);
    }
}
