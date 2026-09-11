using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class CefFlagBuilderTests
{
    [Fact]
    public void Command_line_args_never_contain_the_forbidden_insecure_flags()
    {
        foreach (var isWine in new[] { true, false })
        {
            var args = CefFlagBuilder.BuildCommandLineArgs(isWine);

            foreach (var forbidden in CefFlagBuilder.ForbiddenFlags)
                Assert.DoesNotContain(forbidden, args.Keys);
        }
    }

    [Fact]
    public void No_sandbox_is_set_only_under_wine()
    {
        Assert.True(CefFlagBuilder.BuildCommandLineArgs(isWine: true).ContainsKey("no-sandbox"));
        Assert.False(CefFlagBuilder.BuildCommandLineArgs(isWine: false).ContainsKey("no-sandbox"));
    }

    [Fact]
    public void Autoplay_policy_is_always_no_user_gesture_required()
    {
        var args = CefFlagBuilder.BuildCommandLineArgs(isWine: false);

        Assert.Equal("no-user-gesture-required", args[CefFlagBuilder.AutoplayPolicyFlag]);
    }

    [Fact]
    public void Best_performance_offscreen_args_are_used_only_under_wine()
    {
        Assert.True(CefFlagBuilder.ShouldUseBestPerformanceOffscreenArgs(isWine: true));
        Assert.False(CefFlagBuilder.ShouldUseBestPerformanceOffscreenArgs(isWine: false));
    }

    [Fact]
    public void WebRtc_preferences_always_restrict_to_public_interfaces()
    {
        var prefs = CefFlagBuilder.BuildWebRtcPreferences(hideIpFromPeers: false);

        Assert.Equal("default_public_interface_only", prefs["webrtc.ip_handling_policy"]);
        Assert.DoesNotContain("webrtc.disable_non_proxied_udp", prefs.Keys);
    }

    [Fact]
    public void Hide_ip_from_peers_disables_non_proxied_udp()
    {
        var prefs = CefFlagBuilder.BuildWebRtcPreferences(hideIpFromPeers: true);

        Assert.Equal(true, prefs["webrtc.disable_non_proxied_udp"]);
    }

    [Fact]
    public void User_agent_appends_the_honest_product_suffix()
    {
        var ua = CefFlagBuilder.BuildUserAgent("Mozilla/5.0 Chrome/149.0.0.0", "0.0.0.1");

        Assert.Equal("Mozilla/5.0 Chrome/149.0.0.0 WatchAlong/0.0.0.1", ua);
    }
}
