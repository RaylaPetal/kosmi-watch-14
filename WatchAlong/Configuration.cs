using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using WatchAlong.Shared.Ipc;

namespace WatchAlong;

public enum AudioBackendPreference { Auto, Wasapi, WaveOut }

public enum QualityPreset { P720, P1080 }

/// <summary>
/// Phase 1+2 subset of design.md §10 — group/venue fields aren't used until Phase 3/4 and are
/// intentionally omitted here rather than stubbed out.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Identity
    public Guid InstallId { get; set; } = Guid.NewGuid();
    public string KosmiDisplayName { get; set; } = "";
    public bool UseCharacterNameInKosmi { get; set; } = false;

    // Playback
    public float MasterVolume { get; set; } = 0.8f;
    public bool Muted { get; set; } = false;
    public VoiceMode VoiceMode { get; set; } = VoiceMode.Off;
    public AudioBackendPreference AudioBackend { get; set; } = AudioBackendPreference.Auto;

    // Video
    public QualityPreset Quality { get; set; } = QualityPreset.P720;
    public int MaxFps { get; set; } = 30;

    // World screens / spatial audio (Phase 2, design.md D3/§8.4)
    /// <summary>Max distance (yalms) at which a placed screen's audio is audible. Default/range per design doc: 40, 10-100.</summary>
    public float SpatialMaxDistance { get; set; } = 40f;

    /// <summary>What a duck lowers the game's BGM slider to, as a percent of its current level.</summary>
    public uint BgmDuckToPercent { get; set; } = 20;

    /// <summary>"Show screens in GPose" — off by default (depth-tested-rendering spec).</summary>
    public bool ShowScreensInGPose { get; set; } = false;

    /// <summary>Non-occluded quad (Phase 2a) vs. depth-tested, occluded rendering (Phase 2b). Default: quad, since the depth-tested path is new and unverified against a live game.</summary>
    public ScreenRenderMode ScreenRenderMode { get; set; } = ScreenRenderMode.Quad;

    // Debug
    public int CefRemoteDebugPort { get; set; } = 0;
    public bool VerboseRendererLog { get; set; } = false;

    public List<string> RecentRooms { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
