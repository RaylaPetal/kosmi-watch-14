namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// Rejects anything in a <see cref="SelectorProfile"/> that isn't shaped like a plain CSS
/// selector or text pattern, per specs/kosmi-session/spec.md "Malformed or malicious profile
/// content": a profile is data, and must never carry anything that looks like it's meant to
/// be executed.
/// </summary>
public static class SelectorProfileValidator
{
    private static readonly string[] DisallowedSubstrings =
    [
        "<script", "javascript:", "eval(", "function(", "function (", "=>", "${", "`",
    ];

    public static bool TryValidate(SelectorProfile profile, out string? error)
    {
        if (profile.Schema != 1)
        {
            error = $"Unsupported schema version {profile.Schema}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileVersion))
        {
            error = "profileVersion is required.";
            return false;
        }

        foreach (var value in EnumerateStrings(profile))
        {
            if (!IsPlainSelectorOrPattern(value, out error))
                return false;
        }

        error = null;
        return true;
    }

    private static bool IsPlainSelectorOrPattern(string value, out string? error)
    {
        foreach (var bad in DisallowedSubstrings)
        {
            if (value.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Value '{value}' looks executable (contains '{bad}'), not a plain selector or text pattern.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static IEnumerable<string> EnumerateStrings(SelectorProfile profile)
    {
        foreach (var s in profile.StateProbes.JoinGate.Any ?? [])
            yield return s;
        foreach (var s in profile.StateProbes.JoinGate.TextMatches ?? [])
            yield return s;
        foreach (var s in profile.StateProbes.RoomNotFound.Any ?? [])
            yield return s;
        foreach (var s in profile.StateProbes.RoomNotFound.TextMatches ?? [])
            yield return s;
        foreach (var s in profile.StateProbes.LoginRequired.Any ?? [])
            yield return s;
        foreach (var s in profile.StateProbes.LoginRequired.TextMatches ?? [])
            yield return s;

        yield return profile.JoinGate.NameInput;
        yield return profile.JoinGate.Submit;

        foreach (var s in profile.ParticipantTileSelectors)
            yield return s;
        foreach (var s in profile.PrimaryOverrides)
            yield return s;

        if (profile.FullscreenButton is not null)
            yield return profile.FullscreenButton;

        if (profile.Chat.List is not null)
            yield return profile.Chat.List;
        if (profile.Chat.Input is not null)
            yield return profile.Chat.Input;
        if (profile.Chat.Send is not null)
            yield return profile.Chat.Send;

        if (profile.ParticipantNameSelector is not null)
            yield return profile.ParticipantNameSelector;
    }
}
