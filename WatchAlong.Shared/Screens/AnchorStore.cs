using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// File-backed persistence for screen anchors, one JSON file per location key, per world-screens
/// spec "Placement is saved and restored per location". Location keys can contain arbitrary
/// characters (character/world names in island keys), so the filename is a hash of the key
/// rather than the key itself.
/// </summary>
public sealed class AnchorStore(string directory)
{
    public void Save(ScreenAnchor anchor)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(AnchorRecord.FromAnchor(anchor));
        File.WriteAllText(PathFor(anchor.LocationKey), json);
    }

    public ScreenAnchor? TryLoad(string locationKey)
    {
        var path = PathFor(locationKey);
        if (!File.Exists(path))
            return null;

        var record = JsonSerializer.Deserialize<AnchorRecord>(File.ReadAllText(path));
        return record?.ToAnchor();
    }

    public void Delete(string locationKey)
    {
        var path = PathFor(locationKey);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(string locationKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(locationKey)));
        return Path.Combine(directory, $"{hash}.json");
    }
}
