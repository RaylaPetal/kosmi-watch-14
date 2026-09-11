using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class DotnetRootResolverTests
{
    // Windows-style backslash paths aren't exercised here: DirectoryInfo parses path
    // separators per the host OS, so a `C:\...` path only round-trips correctly on Windows
    // itself — this dev box is Linux. The forward-slash cases below exercise the same
    // "walk up to the directory named 'runtime'" logic that Resolve() uses on any OS.
    [Theory]
    [InlineData("/home/user/.xlcore/runtime/host/fxr/9.0.0", "/home/user/.xlcore/runtime")]
    [InlineData("/home/user/.xlcore/runtime/shared/Microsoft.NETCore.App/9.0.0", "/home/user/.xlcore/runtime")]
    public void Resolves_the_runtime_root_from_a_nested_leaf_path(string leaf, string expectedRoot)
    {
        var resolved = DotnetRootResolver.Resolve(leaf);

        Assert.Equal(new DirectoryInfo(expectedRoot).FullName, resolved);
    }

    [Fact]
    public void Throws_when_no_runtime_directory_is_found_in_the_path()
    {
        Assert.Throws<ArgumentException>(() => DotnetRootResolver.Resolve("/home/user/.xlcore/dalamud/Hooks/dev"));
    }

    [Fact]
    public void Throws_for_empty_input()
    {
        Assert.Throws<ArgumentException>(() => DotnetRootResolver.Resolve(""));
    }
}
