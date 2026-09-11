using System.Diagnostics;

namespace WatchAlong.Shared.Runtime;

/// <summary>Everything needed to spawn the renderer, per design.md §6.1.2.</summary>
/// <param name="ExecutablePath">
/// Path to <c>WatchAlong.Renderer.dll</c> (or a native <c>.exe</c>, if one was published with
/// a Windows RID — building with plain <c>dotnet build</c> never produces one, only the
/// framework-dependent DLL).
/// </param>
/// <param name="HostExecutable">
/// When set, <paramref name="ExecutablePath"/> is launched as <c>&lt;HostExecutable&gt;
/// &lt;ExecutablePath&gt; &lt;...args&gt;</c> instead of being executed directly — i.e. the
/// path to the <c>dotnet</c> muxer that hosts a framework-dependent DLL (see
/// <c>DotnetRootResolver</c> for locating the one Dalamud itself runs on).
/// </param>
public sealed record RendererLaunchOptions(
    string ExecutablePath,
    int ParentPid,
    string PipeName,
    string ShmemBaseName,
    string CefDir,
    string CacheDir,
    string LogLevel,
    bool IsWine,
    int ProtocolVersion,
    string? HostExecutable = null);

/// <summary>
/// Spawns and supervises <c>WatchAlong.Renderer.exe</c> as a hidden child process with
/// redirected output, per specs/renderer-process/spec.md "Renderer process lifecycle". Has no
/// CEF or Dalamud dependency: the plugin composition root wires <see cref="OutputReceived"/>/
/// <see cref="ErrorReceived"/> into <c>IPluginLog</c>.
/// </summary>
public sealed class RendererProcessHost : IDisposable
{
    private Process? _process;

    public event Action<string>? OutputReceived;
    public event Action<string>? ErrorReceived;

    /// <summary>Raised when the process exits, on whatever thread .NET's Process class uses to report it.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => _process?.Id;

    public static string[] BuildArguments(RendererLaunchOptions options)
    {
        var args = new List<string>
        {
            "--parent-pid", options.ParentPid.ToString(),
            "--pipe", options.PipeName,
            "--shmem", options.ShmemBaseName,
            "--cef-dir", options.CefDir,
            "--cache-dir", options.CacheDir,
            "--log-level", options.LogLevel,
            "--protocol", options.ProtocolVersion.ToString(),
        };

        if (options.IsWine)
            args.Add("--wine");

        return [.. args];
    }

    public void Start(RendererLaunchOptions options)
    {
        var args = BuildArguments(options);
        // CEF resolves its own resources (locales, .pak files, the subprocess exe) relative
        // to the working directory, which otherwise defaults to whatever the CURRENT process
        // (the game) is running from — not where WatchAlong.Renderer.dll and its CEF runtime
        // files actually live. Without this, CEF fails to initialize and the renderer crashes
        // immediately after every launch.
        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ExecutablePath));

        if (options.HostExecutable is { } host)
            StartCore(host, workingDirectory, [options.ExecutablePath, .. args]);
        else
            StartCore(options.ExecutablePath, workingDirectory, args);
    }

    /// <summary>
    /// Starts an arbitrary executable with the same hidden/redirected-output plumbing as
    /// <see cref="Start"/>. Exists so tests (and, on platforms where the renderer's apphost
    /// stub isn't directly runnable, callers) can launch it via <c>dotnet &lt;dll&gt;</c>.
    /// No working directory is set — use <see cref="Start"/> when that matters (it does for CEF).
    /// </summary>
    public void StartRaw(string executablePath, params string[] arguments) =>
        StartCore(executablePath, workingDirectory: null, arguments);

    private void StartCore(string executablePath, string? workingDirectory, string[] arguments)
    {
        if (IsRunning)
            throw new InvalidOperationException("Renderer process is already running; call Stop() first.");

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ErrorReceived?.Invoke(e.Data); };
        process.Exited += (_, _) => Exited?.Invoke(process.ExitCode);

        try
        {
            process.Start();
        }
        catch
        {
            // Never leave a constructed-but-never-started Process in `_process` — accessing
            // .HasExited on one throws InvalidOperationException ("No process is associated
            // with this object"), which would otherwise make every later IsRunning check
            // throw too, instead of correctly reporting "not running".
            process.Dispose();
            throw;
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>Kills the whole process tree — the renderer must not survive a forced stop (watchdog restarts).</summary>
    public void Stop()
    {
        if (_process is not { HasExited: false })
            return;

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Process.Kill(entireProcessTree: true) walks the tree via Process.StartTime on
            // every descendant (the CEF subprocesses included) to confirm they're actually
            // descendants before killing them. Under Wine, a process's creation-time FILETIME
            // can come back invalid/zero, which that internal check rejects with this
            // exception — thrown from Dispose()/plugin unload, it broke reload entirely.
            // Falling back to killing just the renderer itself: CefSharp already launches its
            // subprocesses with --cefsharpexitsub, which makes them exit on their own once
            // their parent (this process) is gone, so the tree doesn't outlive it either way.
            _process.Kill();
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
