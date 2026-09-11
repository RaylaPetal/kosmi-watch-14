using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class SelectorProfileTests
{
    private const string ValidProfileJson = """
    {
      "schema": 1,
      "profileVersion": "2026.09.10-1",
      "minAgentVersion": 1,
      "stateProbes": {
        "joinGate":      { "any": ["input[name='nickname']", "button:has-text('Join')"] },
        "roomNotFound":  { "textMatches": ["room (was )?not found", "doesn't exist"] },
        "loginRequired": { "any": ["[data-testid='login-modal']"] }
      },
      "joinGate": { "nameInput": "input[name='nickname']", "submit": "button[type='submit']" },
      "participantTileSelectors": [".participant", "[class*='webcam']"],
      "primaryOverrides": [],
      "fullscreenButton": null,
      "chat": { "list": null, "input": null, "send": null }
    }
    """;

    [Fact]
    public void Valid_default_profile_parses_and_validates()
    {
        var ok = SelectorProfileCodec.TryParse(ValidProfileJson, out var profile, out var error);

        Assert.True(ok, error);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.Schema);
        Assert.Equal(2, profile.StateProbes.JoinGate.Any!.Count);
        Assert.Equal("input[name='nickname']", profile.JoinGate.NameInput);
    }

    [Fact]
    public void Schema_invalid_json_is_rejected()
    {
        var ok = SelectorProfileCodec.TryParse("{ this is not json", out var profile, out var error);

        Assert.False(ok);
        Assert.Null(profile);
        Assert.NotNull(error);
    }

    [Fact]
    public void Unsupported_schema_version_is_rejected()
    {
        var json = ValidProfileJson.Replace("\"schema\": 1", "\"schema\": 2");

        var ok = SelectorProfileCodec.TryParse(json, out var profile, out var error);

        Assert.False(ok);
        Assert.Null(profile);
        Assert.Contains("schema", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("eval(document.cookie)")]
    [InlineData("x => fetch('https://evil.example')")]
    public void Executable_looking_selector_value_is_rejected(string malicious)
    {
        var json = ValidProfileJson.Replace("input[name='nickname']", malicious);

        var ok = SelectorProfileCodec.TryParse(json, out var profile, out var error);

        Assert.False(ok);
        Assert.Null(profile);
        Assert.NotNull(error);
    }
}
