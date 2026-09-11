using System;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// Owns the single active <see cref="ScreenAnchor"/> for the current location: restores it when
/// <see cref="ILocationSource.LocationChanged"/> fires, and lets the placement gizmo create,
/// live-preview, commit, or remove it. There is at most one placed screen at a time (Phase 2
/// has no groups/multiple screens), per world-screens spec "Placement is saved and restored per
/// location".
/// </summary>
public sealed class ScreenController : IDisposable
{
    private readonly ILocationSource _locationSource;
    private readonly AnchorStore _store;

    public ScreenController(ILocationSource locationSource, AnchorStore store)
    {
        _locationSource = locationSource;
        _store = store;
        _locationSource.LocationChanged += OnLocationChanged;

        // Deliberately no eager CurrentLocationKey read here: for the real LocationService,
        // that touches IObjectTable.LocalPlayer, which Dalamud only allows on the framework
        // thread — plugin construction runs off it, so this crashed on load ("Not on main
        // thread!"). The first LocationChanged from LocationService's own Framework.Update tick
        // populates ActiveAnchor a moment later instead.
    }

    /// <summary>The screen placed at the current location, or null (world-screens spec's <c>Personal</c> fallback: no world screen).</summary>
    public ScreenAnchor? ActiveAnchor { get; private set; }

    public event Action? ActiveAnchorChanged;

    /// <summary>Places a new screen at the current location and saves it immediately (world-screens spec "Placing a screen for the first time").</summary>
    public bool Place(ScreenTransform transform)
    {
        if (_locationSource.CurrentLocationKey is not { } key)
            return false;

        var anchor = new ScreenAnchor(key, transform);
        _store.Save(anchor);
        ActiveAnchor = anchor;
        ActiveAnchorChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Updates the active anchor's transform without persisting — used for live gizmo dragging
    /// (world-screens spec "Adjusting an existing placement"). Call <see cref="Commit"/> or
    /// <see cref="Cancel"/> to end the edit.
    /// </summary>
    public void Preview(ScreenTransform transform)
    {
        if (ActiveAnchor is null)
            return;

        ActiveAnchor = ActiveAnchor with { Transform = transform };
        ActiveAnchorChanged?.Invoke();
    }

    /// <summary>Persists the active anchor's current (possibly previewed) transform.</summary>
    public void Commit()
    {
        if (ActiveAnchor is { } anchor)
            _store.Save(anchor);
    }

    /// <summary>Reverts a live preview back to the last saved transform (world-screens spec "Confirming or cancelling a placement change").</summary>
    public void Cancel()
    {
        if (_locationSource.CurrentLocationKey is { } key)
            ActiveAnchor = _store.TryLoad(key);
        ActiveAnchorChanged?.Invoke();
    }

    /// <summary>
    /// Saves an anchor that came from somewhere other than the placement gizmo — an accepted
    /// group invite or position-share message (world-screens spec "Anchor set by accepting an
    /// invite"). Applies immediately only if it's for the user's current location; otherwise it
    /// simply activates later via the normal <see cref="OnLocationChanged"/> restore, exactly
    /// like any other saved anchor.
    /// </summary>
    public void ApplyExternalAnchor(ScreenAnchor anchor)
    {
        _store.Save(anchor);
        if (_locationSource.CurrentLocationKey != anchor.LocationKey)
            return;

        ActiveAnchor = anchor;
        ActiveAnchorChanged?.Invoke();
    }

    /// <summary>Removes the screen from the current location entirely (world-screens spec "Removing a placement").</summary>
    public void Remove()
    {
        if (ActiveAnchor is not { } anchor)
            return;

        _store.Delete(anchor.LocationKey);
        ActiveAnchor = null;
        ActiveAnchorChanged?.Invoke();
    }

    public void Dispose() => _locationSource.LocationChanged -= OnLocationChanged;

    private void OnLocationChanged(string key)
    {
        ActiveAnchor = _store.TryLoad(key);
        ActiveAnchorChanged?.Invoke();
    }
}
