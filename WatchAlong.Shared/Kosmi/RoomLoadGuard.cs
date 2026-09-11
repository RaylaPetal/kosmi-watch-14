namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// The renderer's own, independent re-validation of a room URL before it's ever handed to
/// CEF's <c>LoadUrl</c> — per specs/renderer-process/spec.md "User-provided room URL
/// validated before use": even if an <c>OpenRoom</c> IPC message somehow carries an invalid
/// or malicious URL (a compromised plugin process, a hand-crafted message bypassing the
/// plugin-side check in KosmiSession), the renderer never trusts it blindly.
/// </summary>
public static class RoomLoadGuard
{
    public static bool TryGetSafeLoadUrl(string requestedRoomUrl, out string safeUrl)
    {
        safeUrl = "";

        if (!KosmiRoomUrl.TryParse(requestedRoomUrl, out var code))
            return false;

        safeUrl = KosmiRoomUrl.ToUrl(code);
        return true;
    }
}
