using System.Diagnostics;
using WatchAlong.Shared.Runtime;
using Xunit;

namespace WatchAlong.Tests.Runtime;

public class RendererProcessHostTests
{
    [Fact]
    public void BuildArguments_matches_the_documented_flag_set()
    {
        var options = new RendererLaunchOptions(
            ExecutablePath: "WatchAlong.Renderer.exe",
            ParentPid: 4242,
            PipeName: "WatchAlong-4242-abcd",
            ShmemBaseName: "WatchAlong-frames-4242-abcd",
            CefDir: "/cef",
            CacheDir: "/cache",
            LogLevel: "Info",
            IsWine: true,
            ProtocolVersion: 1);

        var args = RendererProcessHost.BuildArguments(options);

        Assert.Equal([
            "--parent-pid", "4242",
            "--pipe", "WatchAlong-4242-abcd",
            "--shmem", "WatchAlong-frames-4242-abcd",
            "--cef-dir", "/cef",
            "--cache-dir", "/cache",
            "--log-level", "Info",
            "--protocol", "1",
            "--wine",
        ], args);
    }

    [Fact]
    public void BuildArguments_omits_wine_flag_when_not_under_wine()
    {
        var options = new RendererLaunchOptions("exe", 1, "p", "s", "c", "d", "Info", IsWine: false, ProtocolVersion: 1);

        var args = RendererProcessHost.BuildArguments(options);

        Assert.DoesNotContain("--wine", args);
    }

    [Fact]
    public void Start_launches_the_real_renderer_executable_hidden_and_captures_its_output()
    {
        var rendererDll = FindRendererDll();

        using var host = new RendererProcessHost();
        var outputLines = new List<string>();
        host.OutputReceived += outputLines.Add;

        var exited = new ManualResetEventSlim();
        int? exitCode = null;
        host.Exited += code => { exitCode = code; exited.Set(); };

        // Spawn via the `dotnet` host so this works whether or not the renderer's own exe
        // stub is runnable on this OS; StartRaw exercises the same hidden/redirected-output
        // plumbing that Start(RendererLaunchOptions) uses.
        host.StartRaw("dotnet", rendererDll, "--help");

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "Renderer process did not exit in time.");
        Assert.Equal(0, exitCode);
        Assert.Contains(outputLines, line => line.Contains("Usage:"));
    }

    [Fact]
    public void Failed_start_leaves_IsRunning_safely_reportable_instead_of_throwing()
    {
        using var host = new RendererProcessHost();

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            host.StartRaw("/definitely/does/not/exist/watchalong-renderer-nope"));

        // Before the fix, a failed Start() left a constructed-but-never-started Process
        // behind, and this next call to IsRunning threw InvalidOperationException
        // ("No process is associated with this object") instead of returning false.
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Start_can_be_retried_after_a_failed_start()
    {
        var rendererDll = FindRendererDll();
        using var host = new RendererProcessHost();

        Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            host.StartRaw("/definitely/does/not/exist/watchalong-renderer-nope"));

        var exited = new ManualResetEventSlim();
        host.Exited += _ => exited.Set();

        host.StartRaw("dotnet", rendererDll, "--help");

        Assert.True(exited.Wait(TimeSpan.FromSeconds(20)), "Renderer process did not exit in time after retrying Start.");
    }

    private static string FindRendererDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SamplePlugin.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);

        var candidates = Directory.GetFiles(
            Path.Combine(dir!.FullName, "WatchAlong.Renderer", "bin"),
            "WatchAlong.Renderer.dll",
            SearchOption.AllDirectories);

        Assert.NotEmpty(candidates);
        return candidates[0];
    }
}
