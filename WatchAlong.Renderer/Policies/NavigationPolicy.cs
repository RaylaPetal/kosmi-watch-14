using CefSharp;
using CefSharp.Handler;
using WatchAlong.Shared.Kosmi;

namespace WatchAlong.Renderer.Policies;

/// <summary>
/// Enforces "renderer navigation restricted to Kosmi" (specs/renderer-process/spec.md):
/// top-level navigation only to the Kosmi domain, and never to <c>file://</c>. Subresources
/// (CDNs, YouTube iframes, SFU websockets) are untouched — CEF only calls
/// <see cref="OnBeforeBrowse"/> for frame navigations, not for every resource.
/// </summary>
public sealed class NavigationPolicy : RequestHandler
{
    private readonly FileSchemeBlockingResourceHandler _fileSchemeBlocker = new();

    public event Action<string>? NavigationBlocked;

    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain)
            return false; // only top-level navigation is restricted; sub-frames follow the page's own iframes

        if (NavigationAllowlist.IsAllowedTopLevelNavigation(request.Url))
            return false; // allow

        NavigationBlocked?.Invoke(request.Url);
        return true; // cancel
    }

    protected override IResourceRequestHandler GetResourceRequestHandler(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
        bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
    {
        // Blocks file:// at the resource level too (e.g. an <iframe src="file:///..."> the
        // top-level navigation check above never sees), per design.md §6.3 "file://: blocked".
        return request.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            ? _fileSchemeBlocker
            : null!;
    }

    private sealed class FileSchemeBlockingResourceHandler : ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback) =>
            CefReturnValue.Cancel;
    }
}
