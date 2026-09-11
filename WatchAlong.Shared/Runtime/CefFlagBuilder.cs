namespace WatchAlong.Shared.Runtime;

/// <summary>
/// Pure, CefSharp-independent construction of the CEF command-line switches and preferences
/// from design.md §6.2, so the security-critical parts (never enabling
/// <c>disable-web-security</c>) can be unit tested without a real Chromium runtime.
/// <see cref="WatchAlong.Renderer.CefBootstrap"/> (in the renderer project) applies these to
/// an actual <c>CefSettings</c>.
/// </summary>
public static class CefFlagBuilder
{
    /// <summary>Flags that must NEVER appear, regardless of options — the renderer holds a real Kosmi session (§6.3).</summary>
    public static readonly IReadOnlyList<string> ForbiddenFlags =
    [
        "disable-web-security", "allow-running-insecure-content", "mute-audio",
    ];

    public const string AutoplayPolicyFlag = "autoplay-policy";
    public const string AutoplayPolicyValue = "no-user-gesture-required";

    public static IReadOnlyDictionary<string, string> BuildCommandLineArgs(bool isWine)
    {
        var args = new Dictionary<string, string>
        {
            ["disable-background-media-suspend"] = "1",
            ["disable-renderer-backgrounding"] = "1",
            ["disable-background-timer-throttling"] = "1",
            [AutoplayPolicyFlag] = AutoplayPolicyValue,
        };

        // "no-sandbox" only under Wine — Chromium's sandbox doesn't work there (§6.2/§12).
        if (isWine)
            args["no-sandbox"] = "1";

        foreach (var forbidden in ForbiddenFlags)
        {
            if (args.ContainsKey(forbidden))
                throw new InvalidOperationException($"'{forbidden}' must never be set (design.md §6.3).");
        }

        return args;
    }

    /// <summary>Best-performance offscreen rendering args are used always under Wine (software compositing).</summary>
    public static bool ShouldUseBestPerformanceOffscreenArgs(bool isWine) => isWine;

    public static IReadOnlyDictionary<string, object> BuildWebRtcPreferences(bool hideIpFromPeers)
    {
        var prefs = new Dictionary<string, object>
        {
            ["webrtc.ip_handling_policy"] = "default_public_interface_only",
        };

        if (hideIpFromPeers)
            prefs["webrtc.disable_non_proxied_udp"] = true;

        return prefs;
    }

    public static string BuildUserAgent(string stockChromeUserAgent, string pluginVersion) =>
        $"{stockChromeUserAgent} WatchAlong/{pluginVersion}";
}
