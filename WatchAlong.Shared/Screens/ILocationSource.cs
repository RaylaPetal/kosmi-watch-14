using System;

namespace WatchAlong.Shared.Screens;

/// <summary>Abstraction over <c>LocationService</c> so <see cref="ScreenController"/> is testable without the game running.</summary>
public interface ILocationSource
{
    string? CurrentLocationKey { get; }

    event Action<string> LocationChanged;
}
