using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchAlong.Shared.Kosmi;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SelectorProfile))]
internal partial class SelectorProfileJsonContext : JsonSerializerContext;

/// <summary>Parses and schema-validates a selector profile in one step (never returns an unvalidated profile).</summary>
public static class SelectorProfileCodec
{
    public static bool TryParse(string json, out SelectorProfile? profile, out string? error)
    {
        try
        {
            profile = JsonSerializer.Deserialize(json, SelectorProfileJsonContext.Default.SelectorProfile);
        }
        catch (JsonException ex)
        {
            profile = null;
            error = ex.Message;
            return false;
        }

        if (profile is null)
        {
            error = "Profile JSON deserialized to null.";
            return false;
        }

        if (!SelectorProfileValidator.TryValidate(profile, out error))
        {
            profile = null;
            return false;
        }

        return true;
    }
}
