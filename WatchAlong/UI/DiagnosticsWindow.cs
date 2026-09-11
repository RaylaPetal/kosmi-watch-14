using System;
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

    public DiagnosticsWindow(RendererProcessHost process, RendererWatchdog watchdog, KosmiSession session, Action wipeAndRedownload)
        : base("WatchAlong Diagnostics##WatchAlongDiagnostics")
    {
        _process = process;
        _watchdog = watchdog;
        _session = session;
        _wipeAndRedownload = wipeAndRedownload;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Renderer PID: {(_process.IsRunning ? _process.ProcessId?.ToString() : "not running")}");
        ImGui.TextUnformatted($"Renderer state: {_watchdog.State}");
        ImGui.TextUnformatted($"Session state: {_session.State}");

        if (_session.ErrorCode is { } code)
            ImGui.TextUnformatted($"Last error: {code} — {KosmiErrorMessages.Describe(code)}");

        ImGui.Separator();

        if (ImGui.Button("Wipe CEF & re-download"))
            _wipeAndRedownload();
    }
}
