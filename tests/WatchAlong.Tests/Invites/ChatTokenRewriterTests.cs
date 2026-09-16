using System.Text.RegularExpressions;
using WatchAlong.Shared.Invites;
using Xunit;

namespace WatchAlong.Tests.Invites;

public class ChatTokenRewriterTests
{
    private static Regex TokenPattern() => new(@"WA1P?:[A-Za-z0-9_-]+");

    [Fact]
    public void Single_token_is_removed_and_its_label_returned()
    {
        const string text = "WA1:abc123";
        var match = TokenPattern().Match(text);

        var (stripped, labels) = ChatTokenRewriter.Strip(text, [new ChatTokenRewriter.DecodedMatch(match, "Join watch-along: Kaede")]);

        Assert.Equal("", stripped);
        Assert.Equal(["Join watch-along: Kaede"], labels);
    }

    [Fact]
    public void No_decoded_matches_leaves_text_unchanged()
    {
        // Mirrors InviteChatDetector never calling Strip for a token-shaped match that failed to
        // decode — this asserts the empty-input case that behavior reduces to.
        const string text = "WA1:not-actually-decodable";

        var (stripped, labels) = ChatTokenRewriter.Strip(text, []);

        Assert.Equal(text, stripped);
        Assert.Empty(labels);
    }

    [Fact]
    public void Surrounding_text_is_preserved_around_a_stripped_token()
    {
        const string text = "check this out WA1:abc123 cool right?";
        var match = TokenPattern().Match(text);

        var (stripped, labels) = ChatTokenRewriter.Strip(text, [new ChatTokenRewriter.DecodedMatch(match, "Join watch-along: Kaede")]);

        Assert.Equal("check this out  cool right?", stripped);
        Assert.Equal(["Join watch-along: Kaede"], labels);
    }

    [Fact]
    public void Multiple_tokens_are_stripped_and_labels_returned_in_given_order()
    {
        // The invite token comes first in the text, but callers group matches by type (shares
        // before invites) before calling Strip, so the label order must follow the order given
        // here, not the order the tokens occur in the text.
        const string text = "WA1:invitetoken then WA1P:sharetoken";
        var matches = TokenPattern().Matches(text);
        var inviteMatch = matches[0];
        var shareMatch = matches[1];

        var (stripped, labels) = ChatTokenRewriter.Strip(text, [
            new ChatTokenRewriter.DecodedMatch(shareMatch, "Sync screen to Kaede's placement"),
            new ChatTokenRewriter.DecodedMatch(inviteMatch, "Join watch-along: Kaede"),
        ]);

        Assert.Equal(" then ", stripped);
        Assert.Equal(["Sync screen to Kaede's placement", "Join watch-along: Kaede"], labels);
    }
}
