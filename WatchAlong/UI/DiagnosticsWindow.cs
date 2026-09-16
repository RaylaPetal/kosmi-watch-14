using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WatchAlong.Renderer;
using WatchAlong.Shared.Kosmi;
using WatchAlong.Shared.Runtime;

namespace WatchAlong.UI;

/// <summary>Minimal Settings &gt; Diagnostics section (design.md §11), sourced from live renderer/session state.</summary>
public sealed class DiagnosticsWindow : Window, IDisposable
{
    private readonly RendererProcessHost _process;
    private readonly RendererWatchdog _watchdog;
    private readonly KosmiSession _session;
    private readonly Action _wipeAndRedownload;
    private readonly Func<string?> _getDepthRendererError;

    public DiagnosticsWindow(RendererProcessHost process, RendererWatchdog watchdog, KosmiSession session, Action wipeAndRedownload, Func<string?> getDepthRendererError)
        : base("WatchAlong Diagnostics##WatchAlongDiagnostics")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 160),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        _process = process;
        _watchdog = watchdog;
        _session = session;
        _wipeAndRedownload = wipeAndRedownload;
        _getDepthRendererError = getDepthRendererError;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        using var theme = WatchAlongTheme.Push();

        ImGui.TextUnformatted($"Renderer PID: {(_process.IsRunning ? _process.ProcessId?.ToString() : "not running")}");
        ImGui.TextUnformatted($"Renderer state: {_watchdog.State}");
        ImGui.TextUnformatted($"Session state: {_session.State}");

        if (_session.ErrorCode is { } code)
            ImGui.TextUnformatted($"Last error: {code} — {KosmiErrorMessages.Describe(code)}");

        if (_getDepthRendererError() is { } depthError)
            ImGui.TextUnformatted($"Depth-tested renderer: {depthError}");

        ImGui.Separator();

        if (ImGui.Button("Wipe CEF & re-download"))
            _wipeAndRedownload();
    }
}
