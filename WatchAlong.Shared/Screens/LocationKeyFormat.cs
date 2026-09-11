using System;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// Builds the location-key strings a screen anchor is saved under, in the exact string formats
/// XivMediaPlayer's <c>GetLocationKey</c> uses (design.md §8.5, Appendix C), so anchors carry
/// over if a user migrates from it. Pure string formatting — no FFXIVClientStructs dependency —
/// so it's directly testable with plain values (tasks.md 2.1).
/// </summary>
public static class LocationKeyFormat
{
    /// <summary>Inside a house/apartment/FC room: identical rooms across visits share this key.</summary>
    public static string House(int worldId, uint territoryId, int ward, int plot, int room, ulong indoorHouseId) =>
        $"house_{worldId}_{territoryId}_{ward}_{plot}_{room}_{indoorHouseId}";

    /// <summary>Standing on an owned plot outdoors (not yet inside): shared by the whole plot.</summary>
    public static string Plot(int worldId, int ward, uint territoryId, int plot) =>
        $"zone_{worldId}_{ward}_{territoryId}_plot_{plot}";

    /// <summary>The player's own island sanctuary (territory 1055 while inside their own).</summary>
    public static string Island(int worldId, string ownerName) =>
        $"island_{worldId}_{ownerName}";

    /// <summary>A regular outdoor zone, bucketed into a coarse grid so a screen stays put within roughly 60 yalms.</summary>
    public static string ZoneGrid(int worldId, int ward, uint territoryId, float playerX, float playerZ)
    {
        var gridX = (int)Math.Floor(playerX / 60.0);
        var gridZ = (int)Math.Floor(playerZ / 60.0);
        return $"zone_{worldId}_{ward}_{territoryId}_grid_{gridX}_{gridZ}";
    }

    /// <summary>Fallback when the player's position isn't known yet.</summary>
    public static string Zone(int worldId, int ward, uint territoryId) =>
        $"zone_{worldId}_{ward}_{territoryId}";
}
