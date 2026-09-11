using CefSharp;
using CefSharp.Handler;

namespace WatchAlong.Renderer.Policies;

/// <summary>Always cancels downloads, per design.md §6.3 ("Downloads: always cancelled").</summary>
public sealed class DownloadPolicy : DownloadHandler
{
    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) => false;

    protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback) => false;
}
