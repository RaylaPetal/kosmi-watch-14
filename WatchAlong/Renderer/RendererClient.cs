using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;
using WatchAlong.Shared.Runtime;

namespace WatchAlong.Renderer;

/// <summary>
/// Hosts the plugin's end of the control-channel named pipe (design.md §7: the plugin side is
/// the server, the renderer connects as client) and pumps received messages into
/// <see cref="KosmiSessionController"/>. Also owns the frame-ring reader once the renderer
/// announces it via <c>FrameRingInfo</c> (task 7.3).
/// </summary>
public sealed class RendererClient : IAsyncDisposable
{
    private readonly KosmiSessionController _sessionController;
    private readonly RendererWatchdog _watchdog;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private IpcPipe? _pipe;

    public event Action<FrameRingInfoMessage>? FrameRingAnnounced;
    public event Action<string>? Log;

    public RendererClient(KosmiSessionController sessionController, RendererWatchdog watchdog)
    {
        _sessionController = sessionController;
        _watchdog = watchdog;
    }

    public void Start(string pipeName)
    {
        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(pipeName, _loopCts.Token);
    }

    public async Task SendAsync(IpcMessage message)
    {
        if (_pipe is not null)
            await _pipe.SendAsync(message);
    }

    /// <summary>
    /// The renderer process is expected to crash and restart (that's what
    /// RendererSupervisor/RendererWatchdog exist for) — each restart is a brand-new connection
    /// on this same named pipe, so this must keep re-listening for as long as the plugin is
    /// alive, not just serve the first connection and stop. Without this loop, every reconnect
    /// after the first renderer crash timed out waiting for a server that was no longer there.
    /// </summary>
    private async Task RunLoopAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            await RunOnceAsync(pipeName, cancellationToken);
    }

    private async Task RunOnceAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await serverStream.WaitForConnectionAsync(cancellationToken);

            _pipe = new IpcPipe(serverStream);
            _sessionController.ReportRendererStarted();

            var hello = await _pipe.ReceiveAsync(cancellationToken);
            if (hello is HelloMessage)
                await _pipe.SendAsync(new HelloAckMessage(1, "Info", IsWine: WineDetect.IsRunningUnderWine()), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _pipe.ReceiveAsync(cancellationToken);
                if (message is null)
                    break;

                _watchdog.ReportHealthy();

                if (message is FrameRingInfoMessage frameRingInfo)
                    FrameRingAnnounced?.Invoke(frameRingInfo);
                else
                    _sessionController.HandleRendererMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log?.Invoke($"RendererClient loop ended: {ex.Message}");
        }
        finally
        {
            _pipe = null;
            _sessionController.ReportRendererDisconnected();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        if (_loopTask is not null)
            await _loopTask.ContinueWith(_ => { }, TaskScheduler.Default);

        if (_pipe is not null)
            await _pipe.DisposeAsync();
    }
}
