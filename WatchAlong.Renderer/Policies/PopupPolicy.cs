using CefSharp;
using CefSharp.Handler;

namespace WatchAlong.Renderer.Policies;

/// <summary>
/// Denies all popups, per design.md §6.3 ("Popups: denied in theater mode") — Phase 1 has no
/// control-mode OAuth allowlist, so popups are simply never allowed.
/// </summary>
public sealed class PopupPolicy : LifeSpanHandler
{
    protected override bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName,
        WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo,
        IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        return true; // cancel
    }
}
