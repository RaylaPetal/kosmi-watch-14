using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class NavigationAllowlistTests
{
    [Theory]
    [InlineData("https://app.kosmi.io/room/sulync")]
    [InlineData("https://app.kosmi.io/room/@raypetal")]
    public void Kosmi_room_urls_are_allowed(string url) =>
        Assert.True(NavigationAllowlist.IsAllowedTopLevelNavigation(url));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://app.kosmi.io.evil.tld/room/sulync")]
    [InlineData("https://app.kosmi.io/")]
    [InlineData("not a url")]
    public void Anything_else_is_blocked(string url) =>
        Assert.False(NavigationAllowlist.IsAllowedTopLevelNavigation(url));
}
