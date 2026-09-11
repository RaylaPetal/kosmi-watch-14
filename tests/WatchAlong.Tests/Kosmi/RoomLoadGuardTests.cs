using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class RoomLoadGuardTests
{
    [Fact]
    public void Valid_room_url_produces_the_canonical_load_url()
    {
        var ok = RoomLoadGuard.TryGetSafeLoadUrl("https://app.kosmi.io/room/sulync", out var safeUrl);

        Assert.True(ok);
        Assert.Equal("https://app.kosmi.io/room/sulync", safeUrl);
    }

    [Theory]
    [InlineData("https://evil.example/phishing")]
    [InlineData("https://app.kosmi.io.evil.tld/room/sulync")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url at all")]
    public void Renderer_refuses_to_load_a_url_that_bypassed_the_plugin_side_check(string maliciousUrlConstructedDirectly)
    {
        // Simulates an OpenRoom message whose RoomUrl field was never validated upstream
        // (e.g. constructed directly in a test, or a compromised sender) — the renderer must
        // still refuse it independently before it would ever reach CEF's LoadUrl.
        var ok = RoomLoadGuard.TryGetSafeLoadUrl(maliciousUrlConstructedDirectly, out var safeUrl);

        Assert.False(ok);
        Assert.Equal("", safeUrl);
    }
}
