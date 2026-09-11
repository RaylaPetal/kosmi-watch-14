using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class WineDetectTests
{
    private sealed class FakeWineEnvironment(Dictionary<string, string>? env = null, bool wineVersionAvailable = false) : IWineEnvironment
    {
        public string? GetEnvironmentVariable(string name) => env?.GetValueOrDefault(name);

        public bool TryGetWineVersion(out string? version)
        {
            version = wineVersionAvailable ? "9.0" : null;
            return wineVersionAvailable;
        }
    }

    [Fact]
    public void No_wine_signals_reports_not_wine()
    {
        var env = new FakeWineEnvironment();

        Assert.False(WineDetect.IsRunningUnderWine(env));
    }

    [Theory]
    [InlineData("WINEPREFIX")]
    [InlineData("WINELOADER")]
    [InlineData("STEAM_COMPAT_DATA_PATH")]
    [InlineData("STEAM_COMPAT_CLIENT_INSTALL_PATH")]
    public void Wine_related_environment_variable_present_reports_wine(string variable)
    {
        var env = new FakeWineEnvironment(new Dictionary<string, string> { [variable] = "/some/path" });

        Assert.True(WineDetect.IsRunningUnderWine(env));
    }

    [Fact]
    public void No_env_vars_but_ntdll_probe_succeeds_reports_wine()
    {
        var env = new FakeWineEnvironment(wineVersionAvailable: true);

        Assert.True(WineDetect.IsRunningUnderWine(env));
    }

    [Fact]
    public void Real_environment_on_this_linux_dev_box_reports_not_wine()
    {
        // Sanity check for SystemWineEnvironment itself: this test runs directly on Linux
        // (not under Wine), so both probes should correctly say "no".
        Assert.False(WineDetect.IsRunningUnderWine());
    }
}
