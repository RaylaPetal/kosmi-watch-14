using System;

namespace WatchAlong.Shared.Audio;

/// <summary>
/// Ported from XivMediaPlayer's <c>Plugin.cs</c> <c>MuteBgm</c>/<c>RestoreBgm</c> (design.md
/// Appendix C/§8.4), extended with a duck-to-percent mode using the game's BGM volume slider
/// (<c>SystemConfigOption.SoundBgm</c>, 0-100) instead of the boolean mute XivMediaPlayer used —
/// so nearby non-screen game audio (ambience, sound effects) isn't affected. The caller supplies
/// get/set delegates for that config value so this class has no Dalamud dependency and is
/// directly testable (spatial-audio spec "In-game background music ducks near an active screen").
/// </summary>
public sealed class BgmDucker(Func<uint> getBgmVolume, Action<uint> setBgmVolume, uint duckToPercent = 20)
{
    private uint? _savedVolume;

    public bool IsDucked => _savedVolume.HasValue;

    /// <summary>Lowers BGM to <c>duckToPercent</c>% of its current level, remembering the original. A no-op if already ducked.</summary>
    public void Duck()
    {
        if (_savedVolume.HasValue)
            return;

        var current = getBgmVolume();
        _savedVolume = current;
        setBgmVolume((uint)Math.Clamp(current * duckToPercent / 100.0, 0, 100));
    }

    /// <summary>
    /// Restores the saved BGM level. Safe to call whether or not we're currently ducked (design.md
    /// D5: every lifecycle exit — unload, zone change, session close — calls this unconditionally).
    /// </summary>
    public void Restore()
    {
        if (_savedVolume is not { } saved)
            return;

        setBgmVolume(saved);
        _savedVolume = null;
    }
}
