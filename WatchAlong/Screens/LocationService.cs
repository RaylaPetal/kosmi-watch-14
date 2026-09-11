using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using WatchAlong.Shared.Screens;

namespace WatchAlong.Screens;

/// <summary>
/// Ported from XivMediaPlayer's <c>Plugin.cs</c> (<c>GetLocationKey</c>/<c>GetCurrentLocationKeys</c>/
/// <c>GetDetailedLocationInfo</c>, design.md §8.5, Appendix C) — same key formats
/// (<see cref="LocationKeyFormat"/>), extracted into a standalone service with a
/// <see cref="LocationChanged"/> event instead of being read ad hoc. Raises on territory change
/// and on a Framework-tick poll of the housing ward/plot/room (there's no housing-specific
/// Dalamud event to hook), per world-screens spec "Placement is saved and restored per location".
/// </summary>
public sealed unsafe class LocationService : IDisposable, ILocationSource
{
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private string? _lastKey;

    public LocationService(IClientState clientState, IObjectTable objectTable, IFramework framework)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Fires whenever <see cref="CurrentLocationKey"/> changes (territory, or housing ward/plot/room).</summary>
    public event Action<string>? LocationChanged;

    /// <summary>The current location key, or null if the player isn't in a resolvable location (e.g. not logged in).</summary>
    public string? CurrentLocationKey => ComputeLocationKey();

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnTerritoryChanged(uint territoryType) => CheckForChange();

    private void OnFrameworkUpdate(IFramework framework) => CheckForChange();

    private void CheckForChange()
    {
        var key = ComputeLocationKey();
        if (key is not null && key != _lastKey)
        {
            _lastKey = key;
            LocationChanged?.Invoke(key);
        }
    }

    private string? ComputeLocationKey()
    {
        if (!_clientState.IsLoggedIn)
            return null;

        var territoryId = _clientState.TerritoryType;
        if (territoryId == 0)
            return null;

        var localPlayer = _objectTable.LocalPlayer;
        var worldId = localPlayer is not null && localPlayer.CurrentWorld.IsValid
            ? (int)localPlayer.CurrentWorld.RowId
            : 0;

        var housingMgr = HousingManager.Instance();
        var ward = housingMgr is not null ? housingMgr->GetCurrentWard() : (short)-1;
        var plot = housingMgr is not null ? housingMgr->GetCurrentPlot() : (short)-1;
        var room = housingMgr is not null ? housingMgr->GetCurrentRoom() : (short)-1;

        if (housingMgr is not null && housingMgr->IsInside())
        {
            var indoorHouseId = housingMgr->GetCurrentIndoorHouseId().Id;
            return LocationKeyFormat.House(worldId, territoryId, ward, plot, room, indoorHouseId);
        }

        if (housingMgr is not null && plot >= 0 && ward >= 0)
            return LocationKeyFormat.Plot(worldId, ward, territoryId, plot);

        // XivMediaPlayer's GetLocationKey has a third branch for the player island sanctuary
        // (territory 1055) that guesses the island's owner from the party/object table when
        // visiting someone else's — that's inherently a group concept, out of scope for the
        // Location/Personal-only anchors this phase supports (design.md D1). Falls through to
        // the zone grid below, same as any other outdoor zone.
        if (localPlayer is not null)
            return LocationKeyFormat.ZoneGrid(worldId, ward, territoryId, localPlayer.Position.X, localPlayer.Position.Z);

        return LocationKeyFormat.Zone(worldId, ward, territoryId);
    }
}
