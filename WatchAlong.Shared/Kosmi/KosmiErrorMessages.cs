namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// Maps error codes (from <c>Error</c> IPC messages and <see cref="KosmiSession"/> room-level
/// failures) to a distinct, human-readable message, per specs/kosmi-session/spec.md
/// "Unrecognized page state is diagnosable" and design.md §7's documented <c>Error</c> codes.
/// </summary>
public static class KosmiErrorMessages
{
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["CefInitFailed"] = "The built-in browser failed to start. Try \"Wipe CEF & re-download\" in Settings > Diagnostics.",
        ["NavigationBlocked"] = "Blocked an attempt to navigate outside Kosmi. This is expected — WatchAlong only ever loads app.kosmi.io.",
        ["CodecUnsupported"] = "This link uses a format the built-in browser can't play (H.264/MP4). Ask the host to use YouTube, screen share, or the virtual browser.",
        ["AudioInitFailed"] = "Audio playback failed to start. Try switching the audio backend in Settings.",
        ["RendererStartFailed"] = "The renderer process failed to start after several attempts. Click Retry to try again.",
        ["RoomNotFound"] = "This Kosmi room doesn't exist, or the link has expired.",
        ["LoginRequired"] = "This room requires signing in to Kosmi. Open control mode to sign in (note: \"Sign in with Google\" doesn't work in the built-in browser).",
        ["Kicked"] = "You were removed from this Kosmi room.",
        ["Full"] = "This Kosmi room is full.",
    };

    private const string Fallback = "Something went wrong. Check the diagnostics tab for details.";

    public static string Describe(string code) => Messages.GetValueOrDefault(code, Fallback);
}
