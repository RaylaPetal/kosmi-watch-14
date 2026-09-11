namespace WatchAlong.Shared.Runtime;

/// <summary>
/// Computes <c>DOTNET_ROOT</c> for the framework-dependent renderer process from Dalamud's own
/// runtime directory, ported from XivMediaPlayer's <c>CefSharpResolver</c> (design.md
/// Appendix C) so the renderer finds Dalamud's private .NET instead of a system-wide install.
/// </summary>
public static class DotnetRootResolver
{
    /// <param name="dalamudRuntimeDirectory">
    /// A directory inside the runtime Dalamud itself is running on, e.g.
    /// ".../runtime/host/fxr/9.0.0" or ".../runtime/shared/Microsoft.NETCore.App/9.0.0".
    /// </param>
    public static string Resolve(string dalamudRuntimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(dalamudRuntimeDirectory))
            throw new ArgumentException("Dalamud runtime directory must not be empty.", nameof(dalamudRuntimeDirectory));

        var dir = new DirectoryInfo(dalamudRuntimeDirectory);

        // Walk up to the "runtime" directory itself (DOTNET_ROOT must point at the folder that
        // directly contains "dotnet"/"dotnet.exe" and the "shared"/"host" subfolders), not at
        // a version-specific leaf like ".../shared/Microsoft.NETCore.App/9.0.0".
        while (dir is not null && !string.Equals(dir.Name, "runtime", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;

        if (dir is null)
            throw new ArgumentException(
                $"Could not find a 'runtime' directory in the path '{dalamudRuntimeDirectory}'.", nameof(dalamudRuntimeDirectory));

        return dir.FullName;
    }
}
