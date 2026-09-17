using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// File-backed persistence for "the last room joined at this location", one plain-text file per
/// location key (venue-memory spec "Accepting an anchored invite remembers its room for that
/// location"). Mirrors <see cref="AnchorStore"/>'s hashed-filename-per-key shape exactly, but is
/// deliberately a separate store in its own directory: clearing remembered rooms (venue-memory
/// spec "Remembered rooms can be cleared") must never touch saved screen placements, since those
/// are a different thing to a user ("where my screen sits" vs. "what room auto-opens there").
/// </summary>
public sealed class VenueMembershipStore(string directory)
{
    /// <summary>Remembers <paramref name="roomCode"/> as the room to auto-rejoin at <paramref name="locationKey"/>, replacing whatever was remembered there before.</summary>
    public void Remember(string locationKey, string roomCode)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathFor(locationKey), roomCode);
    }

    /// <summary>The room remembered for <paramref name="locationKey"/>, or null if none is.</summary>
    public string? TryLoad(string locationKey)
    {
        var path = PathFor(locationKey);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Forgets every remembered room on this install.</summary>
    public void ClearAll()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private string PathFor(string locationKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(locationKey)));
        return Path.Combine(directory, $"{hash}.txt");
    }
}
