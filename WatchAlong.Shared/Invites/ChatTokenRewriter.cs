using System.Text;
using System.Text.RegularExpressions;

namespace WatchAlong.Shared.Invites;

/// <summary>
/// Pure text-rewriting logic behind <c>InviteChatDetector</c>'s local chat display, kept free of
/// any Dalamud/SeString dependency so it can be unit tested directly. Given the original chat
/// text and the token matches that decoded successfully (already in the order their labels
/// should appear), removes each matched token substring from the text and returns the labels to
/// append after it, in the given order.
/// </summary>
public static class ChatTokenRewriter
{
    /// <summary>A single successfully-decoded token match paired with the display label it should produce.</summary>
    public readonly record struct DecodedMatch(Match Match, string Label);

    /// <summary>
    /// Strips every match's substring out of <paramref name="originalText"/> and returns the
    /// remaining text alongside the labels to append, in the order <paramref name="decodedMatches"/>
    /// was given (not the order matches occur in the text) — callers group matches by token type
    /// (e.g. position-shares before invites) before calling this.
    /// </summary>
    public static (string StrippedText, IReadOnlyList<string> Labels) Strip(string originalText, IReadOnlyList<DecodedMatch> decodedMatches)
    {
        if (decodedMatches.Count == 0)
            return (originalText, []);

        var orderedByPosition = decodedMatches.OrderBy(m => m.Match.Index).ToList();
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var (match, _) in orderedByPosition)
        {
            builder.Append(originalText, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;
        }
        builder.Append(originalText, cursor, originalText.Length - cursor);

        var labels = decodedMatches.Select(m => m.Label).ToList();
        return (builder.ToString(), labels);
    }
}
