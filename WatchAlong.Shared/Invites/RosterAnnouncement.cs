namespace WatchAlong.Shared.Invites;

/// <summary>
/// The fixed "[WatchAlong] &lt;name&gt; joined" line sent via a `/tell` back to whoever's invite
/// was just accepted, and recognized on the inviter's side to populate their session roster.
/// Pure text formatting/parsing — no chat, network, or Dalamud dependency, so both the sending and
/// listening sides can share one definition of the wire shape.
/// </summary>
public static class RosterAnnouncement
{
    private const string Prefix = "[WatchAlong] ";
    private const string Suffix = " joined";

    /// <summary>Builds the announcement text for <paramref name="name"/>.</summary>
    public static string Build(string name) => Prefix + name + Suffix;

    /// <summary>Recognizes a message that is exactly a roster announcement, extracting the announced name. Anything else (including a message that merely contains the pattern amid other text) fails to parse.</summary>
    public static bool TryParse(string text, out string name)
    {
        name = "";
        if (!text.StartsWith(Prefix, System.StringComparison.Ordinal) || !text.EndsWith(Suffix, System.StringComparison.Ordinal))
            return false;

        // An empty-name message ("[WatchAlong]  joined" minus the name) is exactly
        // Prefix.Length + Suffix.Length long — below that, the prefix and suffix overlap
        // (sharing the same space character) and slicing below would throw instead of just
        // failing to parse.
        if (text.Length < Prefix.Length + Suffix.Length)
            return false;

        var inner = text[Prefix.Length..^Suffix.Length];
        if (inner.Length == 0)
            return false;

        name = inner;
        return true;
    }
}
