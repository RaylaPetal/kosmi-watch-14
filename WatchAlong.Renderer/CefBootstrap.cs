using CefSharp;
using CefSharp.OffScreen;
using WatchAlong.Shared.Runtime;

namespace WatchAlong.Renderer;

public sealed record CefBootstrapOptions(
    string CefDir,
    string CacheDir,
    bool IsWine,
    string PluginVersion);

/// <summary>
/// Builds the real <see cref="CefSettings"/> from design.md §6.2, delegating every
/// security-relevant flag/preference decision to <see cref="CefFlagBuilder"/> so that logic
/// stays unit-testable without a Chromium runtime. See design.md §6.3 for why
/// `disable-web-security`/`allow-running-insecure-content` must never appear here.
/// </summary>
public static class CefBootstrap
{
    public static CefSettings BuildSettings(CefBootstrapOptions options)
    {
        var settings = new CefSettings
        {
            RootCachePath = options.CacheDir,
            CachePath = options.CacheDir,
            WindowlessRenderingEnabled = true,
        };

        settings.EnableAudio();

        if (CefFlagBuilder.ShouldUseBestPerformanceOffscreenArgs(options.IsWine))
            settings.SetOffScreenRenderingBestPerformanceArgs();

        foreach (var (flag, value) in CefFlagBuilder.BuildCommandLineArgs(options.IsWine))
            settings.CefCommandLineArgs[flag] = value;

        return settings;
    }

    /// <summary>
    /// Resolves and sets <c>DOTNET_ROOT</c> so the framework-dependent renderer finds
    /// Dalamud's own .NET, per design.md Appendix C (ported from `CefSharpResolver`).
    /// </summary>
    public static void ConfigureDotnetRoot(string dalamudRuntimeDirectory)
    {
        var root = DotnetRootResolver.Resolve(dalamudRuntimeDirectory);
        Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
    }

    public static bool Initialize(CefBootstrapOptions options)
    {
        var settings = BuildSettings(options);
        return Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null);
    }
}
