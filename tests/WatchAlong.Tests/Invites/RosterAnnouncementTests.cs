using WatchAlong.Shared.Invites;
using Xunit;

namespace WatchAlong.Tests.Invites;

public class RosterAnnouncementTests
{
    [Fact]
    public void Build_produces_a_line_TryParse_recognizes()
    {
        var text = RosterAnnouncement.Build("Kaede");

        Assert.Equal("[WatchAlong] Kaede joined", text);
        Assert.True(RosterAnnouncement.TryParse(text, out var name));
        Assert.Equal("Kaede", name);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("[WatchAlong] joined")] // empty name
    [InlineData("some other text [WatchAlong] Kaede joined and more")] // not an exact match
    [InlineData("")]
    public void TryParse_rejects_anything_that_is_not_exactly_the_announcement_shape(string text) =>
        Assert.False(RosterAnnouncement.TryParse(text, out _));
}
