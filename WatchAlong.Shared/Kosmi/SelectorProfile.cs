namespace WatchAlong.Shared.Kosmi;

/// <summary>
/// Data-only description of how to find things on the Kosmi page (design.md §6.4/D6,
/// specs/kosmi-session/spec.md "Selector-driven behavior is data, never executable code").
/// Every string here is either a CSS selector or a `:has-text()`-style text pattern — never
/// evaluated as script.
/// </summary>
public sealed record SelectorProfile(
    int Schema,
    string ProfileVersion,
    int MinAgentVersion,
    StateProbes StateProbes,
    JoinGateSelectors JoinGate,
    IReadOnlyList<string> ParticipantTileSelectors,
    IReadOnlyList<string> PrimaryOverrides,
    string? FullscreenButton,
    ChatSelectors Chat,
    // Queried within each ParticipantTileSelectors match (not page-wide) to read a member's
    // display name for the room roster. Null/absent means member names can't be detected.
    string? ParticipantNameSelector = null);

public sealed record StateProbes(
    SelectorGroup JoinGate,
    SelectorGroup RoomNotFound,
    SelectorGroup LoginRequired);

/// <summary>A probe matches if any selector matches, or any text pattern is found on the page.</summary>
public sealed record SelectorGroup(IReadOnlyList<string>? Any = null, IReadOnlyList<string>? TextMatches = null);

public sealed record JoinGateSelectors(string NameInput, string Submit);

public sealed record ChatSelectors(string? List, string? Input, string? Send);
