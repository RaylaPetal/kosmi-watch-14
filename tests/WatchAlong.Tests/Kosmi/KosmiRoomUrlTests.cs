using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class KosmiRoomUrlTests
{
    [Theory]
    [InlineData("https://app.kosmi.io/room/sulync", "sulync")]
    [InlineData("https://app.kosmi.io/room/sulync/", "sulync")]
    [InlineData("https://APP.KOSMI.IO/room/sulync", "sulync")]
    [InlineData("https://app.kosmi.io/room/@raypetal", "@raypetal")]
    [InlineData("  https://app.kosmi.io/room/ab  ", "ab")]
    public void Valid_room_urls_parse_to_the_expected_code(string input, string expectedCode)
    {
        var ok = KosmiRoomUrl.TryParse(input, out var code);

        Assert.True(ok);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://app.kosmi.io/room/sulync")]
    [InlineData("http://app.kosmi.io/room/sulync")] // not https
    [InlineData("https://app.kosmi.io.evil.tld/room/sulync")] // lookalike suffix domain
    [InlineData("https://evil.tld/app.kosmi.io/room/sulync")] // lookalike path
    [InlineData("https://app.kosmi.io/notroom/sulync")]
    [InlineData("https://app.kosmi.io/room/")]
    [InlineData("https://app.kosmi.io/room/a")] // too short (min length 2)
    [InlineData("https://app.kosmi.io/room/has space")]
    [InlineData("https://app.kosmi.io/room/sulync/extra")]
    public void Invalid_or_malicious_input_is_rejected(string input)
    {
        var ok = KosmiRoomUrl.TryParse(input, out var code);

        Assert.False(ok);
        Assert.Equal("", code);
    }

    [Fact]
    public void ToUrl_rebuilds_the_canonical_address_from_a_code()
    {
        Assert.Equal("https://app.kosmi.io/room/sulync", KosmiRoomUrl.ToUrl("sulync"));
    }

    [Fact]
    public void Query_string_and_fragment_are_discarded_and_never_stored()
    {
        // Only the bare code is kept (per Appendix A: "store the code, never the raw URL, and
        // rebuild the URL from it") — a query string or fragment on the input has no effect
        // on the stored value, so it can't smuggle anything through to ToUrl later.
        var ok = KosmiRoomUrl.TryParse("https://app.kosmi.io/room/sulync?x=https://evil.tld#frag", out var code);

        Assert.True(ok);
        Assert.Equal("sulync", code);
        Assert.Equal("https://app.kosmi.io/room/sulync", KosmiRoomUrl.ToUrl(code));
    }
}
