using WatchAlong.Shared.Kosmi;
using Xunit;

namespace WatchAlong.Tests.Kosmi;

public class EmbeddedDefaultProfileTests
{
    private static string FindDefaultProfilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SamplePlugin.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "WatchAlong.Renderer", "Assets", "default-profile.json");
        Assert.True(File.Exists(path), $"Expected default profile at {path}");
        return path;
    }

    [Fact]
    public void Embedded_default_profile_passes_schema_validation()
    {
        var json = File.ReadAllText(FindDefaultProfilePath());

        var ok = SelectorProfileCodec.TryParse(json, out var profile, out var error);

        Assert.True(ok, error);
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.Schema);
        Assert.Equal("input[name='nickname']", profile.JoinGate.NameInput);
    }
}
