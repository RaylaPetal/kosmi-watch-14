using System.Text.RegularExpressions;

namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// Validates and normalizes Kosmi room addresses, per design.md Appendix A and
/// specs/renderer-process/spec.md "Renderer navigation restricted to Kosmi". Only the room
/// code is ever stored or passed onward — never the raw URL — so query-string and fragment
/// tricks (and lookalike domains) can't smuggle a different destination through later.
/// </summary>
public static partial class KosmiRoomUrl
{
    private const string Host = "app.kosmi.io";

    [GeneratedRegex(@"^(@?[A-Za-z0-9_\-]{2,64})$")]
    private static partial Regex RoomCodePattern();

    /// <summary>Parses a user-supplied room address into a bare room code, or fails closed.</summary>
    public static bool TryParse(string input, out string roomCode)
    {
        roomCode = "";

        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // Exact host match only: rejects lookalikes such as "app.kosmi.io.evil.tld" or
        // "evil.tld/app.kosmi.io", which Host.Equals alone would not catch if compared loosely.
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || !string.Equals(segments[0], "room", StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = segments[1];
        if (!RoomCodePattern().IsMatch(candidate))
            return false;

        roomCode = candidate;
        return true;
    }

    public static string ToUrl(string roomCode) => $"https://{Host}/room/{roomCode}";
}
