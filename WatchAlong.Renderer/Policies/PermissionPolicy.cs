using CefSharp;
using CefSharp.Handler;

namespace WatchAlong.Renderer.Policies;

/// <summary>
/// Always denies camera, microphone, screen/display capture, geolocation, notifications,
/// clipboard-read, and MIDI, per design.md §6.3 and specs/renderer-process/spec.md
/// "Renderer permission and capability restrictions" — the renderer never publishes local
/// media into a Kosmi room.
/// </summary>
public sealed class PermissionPolicy : PermissionHandler
{
    protected override bool OnRequestMediaAccessPermission(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string requestingOrigin,
        MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
    {
        callback.Cancel();
        return true;
    }

    protected override bool OnShowPermissionPrompt(
        IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId, string requestingOrigin,
        PermissionRequestType requestedPermissions, IPermissionPromptCallback callback)
    {
        callback.Continue(PermissionRequestResult.Deny);
        return true;
    }
}
