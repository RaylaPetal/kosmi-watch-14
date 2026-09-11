using CefSharp;
using WatchAlong.Renderer.Harness;

namespace WatchAlong.Renderer;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("WatchAlong.Renderer — out-of-process Kosmi browser host.");
            Console.WriteLine("Usage: WatchAlong.Renderer.exe --parent-pid <pid> --pipe <name> --shmem <name> --cef-dir <path> --cache-dir <path> --protocol <n> [--wine] [--harness <url> --out <dir>]");
            return 0;
        }

        var harnessIndex = Array.IndexOf(args, "--harness");
        var outIndex = Array.IndexOf(args, "--out");
        if (harnessIndex >= 0 && outIndex >= 0 && harnessIndex + 1 < args.Length && outIndex + 1 < args.Length)
            return RunHarness(args[harnessIndex + 1], args[outIndex + 1]);

        if (TryParseRendererArgs(args, out var rendererArgs))
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            // Every await in RendererApp.RunAsync must resume on this exact thread — CEF
            // requires Cef.Initialize and Cef.Shutdown to run on the same OS thread, and a
            // console app has no SynchronizationContext by default to guarantee that (see
            // SingleThreadSynchronizationContext).
            var syncContext = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(syncContext);

            try
            {
                var appTask = new RendererApp(rendererArgs!).RunAsync(cts.Token);
                appTask.ContinueWith(_ => syncContext.Complete(), TaskScheduler.Default);
                syncContext.RunOnCurrentThread();
                return appTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Without this, any failure here (most likely Cef.Initialize itself, e.g.
                // under Wine) is an unhandled exception that crashes the whole process
                // natively — on Wine that means a backtrace.txt popup with zero managed
                // stack info, and nothing at all reaches the plugin's log. Print the full
                // exception to stderr, which the plugin already redirects into its own log.
                Console.Error.WriteLine("WatchAlong.Renderer: fatal error during startup:");
                Console.Error.WriteLine(ex);
                return 2;
            }
        }

        Console.WriteLine("WatchAlong.Renderer: pass --harness <url> --out <dir>, or the full --parent-pid/--pipe/... set (see --help).");
        return 1;
    }

    private static int RunHarness(string url, string outDir)
    {
        var settings = CefBootstrap.BuildSettings(new CefBootstrapOptions(CefDir: "", CacheDir: outDir, IsWine: false, PluginVersion: "harness"));
        Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        HarnessMode.RunAsync(url, outDir, cts.Token).GetAwaiter().GetResult();
        Cef.Shutdown();
        return 0;
    }

    private static bool TryParseRendererArgs(string[] args, out RendererAppArgs? rendererArgs)
    {
        rendererArgs = null;

        var parentPid = FindIntArg(args, "--parent-pid");
        var pipe = FindStringArg(args, "--pipe");
        var shmem = FindStringArg(args, "--shmem");
        var cefDir = FindStringArg(args, "--cef-dir");
        var cacheDir = FindStringArg(args, "--cache-dir");
        var protocol = FindIntArg(args, "--protocol") ?? 1;
        var logLevel = FindStringArg(args, "--log-level") ?? "Info";
        var isWine = args.Contains("--wine");

        if (parentPid is null || pipe is null || shmem is null || cefDir is null || cacheDir is null)
            return false;

        rendererArgs = new RendererAppArgs(parentPid.Value, pipe, shmem, cefDir, cacheDir, logLevel, isWine, protocol);
        return true;
    }

    private static string? FindStringArg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int? FindIntArg(string[] args, string name) =>
        int.TryParse(FindStringArg(args, name), out var value) ? value : null;
}
