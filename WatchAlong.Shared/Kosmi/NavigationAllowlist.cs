namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// The single decision "is this URL allowed for top-level navigation" — shared by the
/// renderer's live navigation handler and its double-validation before `LoadUrl` (design.md
/// §6.3, specs/renderer-process/spec.md "Renderer navigation restricted to Kosmi").
/// Subresource loads (CDNs, YouTube iframes, SFU websockets) are never checked against this;
/// only top-level frame navigation is.
/// </summary>
public static class NavigationAllowlist
{
    public static bool IsAllowedTopLevelNavigation(string url) => KosmiRoomUrl.TryParse(url, out _);
}
