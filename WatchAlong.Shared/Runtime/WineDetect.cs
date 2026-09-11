using System.Runtime.InteropServices;

namespace WatchAlong.Shared.Runtime;

/// <summary>
/// Detects whether the current process is running under Wine/Proton, ported from
/// XivMediaPlayer's `DetectWineRuntime` (design.md D6/Appendix C). Used to force
/// software rendering, `no-sandbox`, and shared-memory-only frame transport.
/// </summary>
public interface IWineEnvironment
{
    string? GetEnvironmentVariable(string name);

    /// <summary>Probes ntdll for the Wine-only `wine_get_version` export.</summary>
    bool TryGetWineVersion(out string? version);
}

public sealed class SystemWineEnvironment : IWineEnvironment
{
    public static readonly SystemWineEnvironment Instance = new();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool TryGetWineVersion(out string? version)
    {
        version = null;

        if (!NativeLibrary.TryLoad("ntdll.dll", out var handle))
            return false;

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "wine_get_version", out var proc))
                return false;

            var versionPtr = Marshal.GetDelegateForFunctionPointer<WineGetVersionDelegate>(proc)();
            version = Marshal.PtrToStringAnsi(versionPtr);
            return version is not null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WineGetVersionDelegate();
}

public static class WineDetect
{
    private static readonly string[] WineEnvironmentVariables =
    [
        "WINEPREFIX", "WINELOADER", "STEAM_COMPAT_DATA_PATH", "STEAM_COMPAT_CLIENT_INSTALL_PATH",
    ];

    public static bool IsRunningUnderWine(IWineEnvironment? environment = null)
    {
        environment ??= SystemWineEnvironment.Instance;

        foreach (var variable in WineEnvironmentVariables)
        {
            if (environment.GetEnvironmentVariable(variable) is not null)
                return true;
        }

        return environment.TryGetWineVersion(out _);
    }
}
