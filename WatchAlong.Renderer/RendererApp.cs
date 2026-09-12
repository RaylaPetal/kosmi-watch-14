using System.IO.Pipes;
using CefSharp;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Runtime;

namespace WatchAlong.Renderer;

public sealed record RendererAppArgs(
    int ParentPid,
    string PipeName,
    string ShmemBaseName,
    string CefDir,
    string CacheDir,
    string LogLevel,
    bool IsWine,
    int ProtocolVersion);

/// <summary>
/// The IPC-driven mode (task 4.10): connects to the plugin's named pipe, performs the
/// handshake, watches the parent PID, and dispatches control messages to a
/// <see cref="KosmiBrowser"/>. Only one browser/room exists at a time
/// (specs/renderer-process/spec.md "Single active renderer").
/// </summary>
public sealed class RendererApp(RendererAppArgs args)
{
    private const int CurrentProtocolVersion = 1;
    private const ushort FrameSlotCount = 3;
    private const int MaxWidth = 1920;
    // Taller than the 1080 the player itself needs: confirmed via kosmi-agent.js's own geometry
    // report that Kosmi's page renders a header/toolbar (~59px observed) above the player that
    // theater mode's position:fixed;inset:0 isn't actually suppressing for this element — capturing
    // at exactly 1080 meant that offset pushed the real content's bottom (controls bar) past the
    // capture's own bottom edge, with no way to recover pixels that were never captured. The extra
    // headroom here lets KosmiBrowser's content-rect crop (x,y,w,h from the page agent) remove
    // whatever sits above the real content without losing anything real off the bottom.
    private const int MaxHeight = 1200;

    private KosmiBrowser? _browser;
    private InputRouter? _inputRouter;
    // IpcPipe.SendAsync isn't safe to call concurrently (it writes the length prefix and the
    // payload as two separate awaits) — the agent's page-state/media events arrive on CEF's UI
    // thread while the main Dispatch loop also sends, so every send goes through this lock.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task<int> RunAsync(CancellationToken shutdownRequested)
    {
        if (args.ProtocolVersion != CurrentProtocolVersion)
        {
            Console.Error.WriteLine($"Protocol mismatch: plugin wants {args.ProtocolVersion}, renderer is {CurrentProtocolVersion}.");
            return 3; // per design.md §7: mismatched major version exits with code 3
        }

        var watcher = new ParentProcessWatcher(args.ParentPid, IsProcessAlive);
        using var parentGoneCts = new CancellationTokenSource();
        watcher.ParentGone += () => parentGoneCts.Cancel();

        // A Timer callback runs on a ThreadPool thread: anything it throws terminates the
        // *entire process* immediately, regardless of exception type, so this must never let
        // anything through.
        using var watchdogTimer = new Timer(_ =>
        {
            try
            {
                watcher.Tick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WatchAlong.Renderer: parent-process watchdog tick failed: {ex}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // The plugin's watchdog treats a stale shmem heartbeat as a hung renderer, but the
        // heartbeat otherwise only advances when CEF actually paints a frame — a page can
        // easily take longer than the watchdog's stall threshold to produce its first paint
        // (cold CEF/Chromium start under Wine, first navigation), which would otherwise get
        // the still-loading renderer killed and restarted in a loop before it ever finishes.
        using var heartbeatTimer = new Timer(_ =>
        {
            try
            {
                _browser?.Heartbeat();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WatchAlong.Renderer: heartbeat tick failed: {ex}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownRequested, parentGoneCts.Token);

        await using var clientStream = new NamedPipeClientStream(".", args.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(10_000, linkedCts.Token);
        await using var pipe = new IpcPipe(clientStream);

        var settings = CefBootstrap.BuildSettings(new CefBootstrapOptions(args.CefDir, args.CacheDir, args.IsWine, "0.0.0.1"));
        Cef.Initialize(settings, performDependencyCheck: false, browserProcessHandler: null);

        try
        {
            await SendAsync(pipe, new HelloMessage(
                CurrentProtocolVersion, Cef.CefSharpVersion, Cef.CefVersion,
                new CodecSupport(H264: false, Aac: true, Vp9: true, Av1: true)), linkedCts.Token);

            while (!linkedCts.IsCancellationRequested)
            {
                var message = await pipe.ReceiveAsync(linkedCts.Token);
                if (message is null)
                    break; // pipe closed

                await Dispatch(message, pipe, linkedCts.Token);
            }
        }
        finally
        {
            _browser?.Dispose();
            Cef.Shutdown();
        }

        return 0;
    }

    private async Task Dispatch(IpcMessage message, IpcPipe pipe, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case OpenRoomMessage openRoom:
                _browser?.Dispose();
                if (KosmiBrowser.TryCreate(openRoom, args.ShmemBaseName, FrameSlotCount, MaxWidth, MaxHeight, out var browser))
                {
                    _browser = browser;
                    browser!.PageStateChanged += state => _ = SendAsync(pipe, new PageStateMessage(state, null), cancellationToken);
                    browser.MediaStateChanged += media => _ = SendAsync(pipe, media, cancellationToken);
                    browser.ChatMemberAnnounced += name => _ = SendAsync(pipe, new ChatMemberAnnouncedMessage(name), cancellationToken);
                    // Task 7.3: the plugin opens this exact mapping once it receives this message.
                    await SendAsync(pipe, new FrameRingInfoMessage(args.ShmemBaseName, FrameSlotCount, MaxWidth, MaxHeight), cancellationToken);

                    // ChromiumWebBrowser creates the underlying CEF browser asynchronously;
                    // GetBrowser() (inside CreateInputRouter) throws until this completes.
                    await browser.WaitForInitialLoadAsync();
                    _inputRouter = browser.CreateInputRouter();
                }
                break;

            case CloseRoomMessage:
                _browser?.Dispose();
                _browser = null;
                _inputRouter = null;
                break;

            case SetAudioMessage setAudio:
                _browser?.SetAudio(setAudio.Volume, setAudio.Muted);
                break;

            case Shared.Ipc.InputMessage input:
                _inputRouter?.Handle(input);
                break;

            case ShutdownMessage:
                _browser?.Dispose();
                _browser = null;
                break;

            case DebugSnapshotMessage:
                if (_browser is not null)
                {
                    var outputDir = Path.Combine(args.CacheDir, "debug-snapshots");
                    try
                    {
                        await _browser.SaveDebugSnapshotAsync(outputDir);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"WatchAlong.Renderer: debug snapshot failed: {ex.Message}");
                    }
                }
                break;

            // SetViewMode/SetViewport/SetFrameRate/SetVoiceMode/SetProfile/Reload land alongside
            // the control-mode input work (tasks 5.x/8.x).
        }
    }

    /// <summary>
    /// Fire-and-forget callers (the browser's PageStateChanged/MediaStateChanged events) discard
    /// the returned task, so a send failure here (e.g. the pipe closing mid-shutdown) must not
    /// become an unobserved task exception — it's just best-effort telemetry at that point; the
    /// main Dispatch loop already handles the pipe actually going away.
    /// </summary>
    private async Task SendAsync(IpcPipe pipe, IpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await pipe.SendAsync(message, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WatchAlong.Renderer: failed to send {message.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            // This runs on a Timer callback (a ThreadPool thread): an unhandled exception
            // here terminates the *entire process* immediately, regardless of type, so this
            // must never let anything through — not just the documented ArgumentException.
            return false;
        }
    }
}
