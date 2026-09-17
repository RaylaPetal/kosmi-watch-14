using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ECommons;
// Aliased: WatchAlong already has a sibling namespace WatchAlong.Chat (InviteChatDetector,
// TellRosterListener) that shadows the unqualified ECommons.Automation.Chat class name.
using EcChat = ECommons.Automation.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Config;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WatchAlong.Camera;
using WatchAlong.Chat;
using WatchAlong.Core;
using WatchAlong.Renderer;
using WatchAlong.Screens;
using WatchAlong.Screens.Ported;
using WatchAlong.Shared.Audio;
using WatchAlong.Shared.Invites;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Kosmi;
using WatchAlong.Shared.Runtime;
using WatchAlong.Shared.Screens;
using WatchAlong.UI;
using Dalamud.Bindings.ImGui;

namespace WatchAlong;

/// <summary>Composition root: wires the renderer supervisor, session controller, frame pipeline, and UI together.</summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly WindowSystem _windowSystem = new("WatchAlong");
    private readonly RendererProcessHost _rendererProcess = new();
    private readonly RendererWatchdog _watchdog = new();
    private readonly RendererSupervisor _supervisor;
    private readonly KosmiSessionController _sessionController;

    /// <summary>Display names of WatchAlong users seen this session via an accepted invite or position sync — not a live/authoritative roster, just who's been in contact (group-invites spec).</summary>
    private readonly HashSet<string> _sessionMembers = [];
    private readonly RendererClient _rendererClient;
    private readonly Direct3D11VideoTexture _texture;
    private readonly ViewerWindow _viewerWindow;
    private readonly ConfigWindow _configWindow;
    private readonly DiagnosticsWindow _diagnosticsWindow;
    private readonly LocationService _locationService;
    private readonly ScreenController _screenController;
    private readonly VenueMembershipStore _venueMembership;
    private readonly WorldVideoRenderer _worldVideoRenderer;
    private readonly BgmDucker _bgmDucker;
    private readonly SpatialAudioController _spatialAudio;
    private readonly PlacementWindow _placementWindow;
    private readonly ConfirmationWindow _confirmationWindow = new();
    private readonly InviteChatDetector _inviteChatDetector;
    private readonly TellRosterListener _tellRosterListener;
    private readonly ScreenFocusService _screenFocus;

    private FrameReader? _frameReader;
    // _frameReader is disposed/replaced from OnFrameRingAnnounced, which fires on RendererClient's
    // async receive-loop thread — not the main thread OnFrameworkUpdate runs on. Without this lock,
    // the main thread could call TryUpload on a FrameReader mid-Dispose() on another thread: Dispose
    // unmaps the shared memory-mapped view underneath a concurrent raw-pointer read into that same
    // memory — an access violation in the game's own process.
    private readonly object _frameReaderLock = new();
    private readonly string _pipeName = $"WatchAlong-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Only dependency so far that needs it: Chat.SendMessage (tell-relay roster's outgoing
        // /tell) resolves its native signature lazily through ECommons' own service container,
        // which this populates.
        ECommonsMain.Init(PluginInterface, this);

        var isWine = WineDetect.IsRunningUnderWine();
        var dependencyDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "Dependencies");
        var cacheDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "cef-profile");
        // WatchAlong.Renderer is our own code, shipped alongside the plugin DLL (see the
        // CopyRendererOutput target in WatchAlong.csproj) — not something DependencyManager
        // downloads. Only the CEF/Chromium runtime itself goes in dependencyDir.
        //
        // A plain `dotnet build` never produces a native WatchAlong.Renderer.exe (that needs
        // publishing with a Windows RID) — only the framework-dependent .dll. So prefer a
        // real .exe if one exists (a RID-published build), and otherwise fall back to running
        // the .dll through the same `dotnet` muxer Dalamud itself is running on.
        var pluginDir = PluginInterface.AssemblyLocation.Directory!.FullName;
        var rendererExe = Path.Combine(pluginDir, "WatchAlong.Renderer.exe");
        var rendererDll = Path.Combine(pluginDir, "WatchAlong.Renderer.dll");
        var useNativeExe = File.Exists(rendererExe);
        var rendererPath = useNativeExe ? rendererExe : rendererDll;

        string? dotnetHost = null;
        if (!useNativeExe)
        {
            var dotnetRoot = DotnetRootResolver.Resolve(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            dotnetHost = Path.Combine(dotnetRoot, "dotnet.exe");
        }

        Log.Information($"WatchAlong: renderer path '{rendererPath}' (exists: {File.Exists(rendererPath)}), host '{dotnetHost ?? "(native exe)"}'"
            + (dotnetHost is not null ? $" (exists: {File.Exists(dotnetHost)})" : ""));

        _rendererProcess.OutputReceived += line => Log.Information($"[Renderer] {line}");
        _rendererProcess.ErrorReceived += line =>
        {
            // Chromium's net stack logs this whenever the address-sorting socket ioctl isn't
            // supported (WSAEOPNOTSUPP/10045) — routine under Wine, harmless, and not something
            // WatchAlong can do anything about. Downgrade instead of alarming as an error.
            if (IsBenignRendererStderrLine(line))
                Log.Verbose($"[Renderer] {line}");
            else
                Log.Error($"[Renderer] {line}");
        };
        _rendererProcess.Exited += code => Log.Information($"WatchAlong: renderer process exited with code {code}.");

        var process = new RendererProcessAdapter(_rendererProcess, () => new RendererLaunchOptions(
            ExecutablePath: rendererPath,
            ParentPid: Environment.ProcessId,
            PipeName: _pipeName,
            // A fresh name per launch, not a fixed field: Process.Kill() on the previous
            // renderer doesn't block until it has actually exited (especially slow under
            // Wine), so a restart can race the old process's shared-memory mapping still being
            // held open. Reusing the same name then fails with "File already exists" and
            // crashes immediately, which just restarts the loop — the plugin-side FrameReader
            // always learns the current name from the renderer's own FrameRingInfo handshake,
            // so nothing here depends on the name staying stable across restarts.
            ShmemBaseName: $"WatchAlong-frames-{Environment.ProcessId}-{Guid.NewGuid():N}",
            CefDir: dependencyDir,
            CacheDir: cacheDir,
            LogLevel: Configuration.VerboseRendererLog ? "Debug" : "Info",
            IsWine: isWine,
            ProtocolVersion: 1,
            HostExecutable: dotnetHost));

        _supervisor = new RendererSupervisor(process, _watchdog);
        _sessionController = new KosmiSessionController(_supervisor);
        _sessionController.RendererSpawnFailed += ex => Log.Error(ex, "WatchAlong: failed to start the renderer process.");
        _rendererClient = new RendererClient(_sessionController, _watchdog);
        _rendererClient.FrameRingAnnounced += OnFrameRingAnnounced;
        _rendererClient.Log += msg => Log.Information(msg);

        _supervisor.SendOpenRoom += msg => _rendererClient.SendAsync(msg).ConfigureAwait(false);
        _supervisor.SendCloseRoom += () => _rendererClient.SendAsync(new CloseRoomMessage()).ConfigureAwait(false);

        var (device, context) = GameDevice.GetD3D11DeviceAndContext();
        _texture = new Direct3D11VideoTexture(device, context);

        _viewerWindow = new ViewerWindow(
            _sessionController.Session,
            _texture,
            msg => _rendererClient.SendAsync(msg).ConfigureAwait(false),
            openSettings: () => _configWindow.IsOpen = true);
        _diagnosticsWindow = new DiagnosticsWindow(_rendererProcess, _watchdog, _sessionController.Session, WipeAndRedownload, () => _worldVideoRenderer.DepthRendererError);

        // World screens / spatial audio (Phase 2, design.md D1/D3/D5). A screen is drawn and its
        // audio scaled entirely in this process (game device, game camera) — WatchAlong.Renderer
        // never needs to know about world placement, so this wiring stays in-process rather than
        // round-tripping over IPC (tasks.md 3.5's documented alternative).
        _locationService = new LocationService(ClientState, ObjectTable, Framework);
        var anchorDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "screens");
        _screenController = new ScreenController(_locationService, new AnchorStore(anchorDir));
        var venueDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "venues");
        _venueMembership = new VenueMembershipStore(venueDir);
        _screenFocus = new ScreenFocusService(_screenController);
        _worldVideoRenderer = new WorldVideoRenderer(GameGui);
        _bgmDucker = new BgmDucker(
            () => { GameConfig.TryGet(SystemConfigOption.SoundBgm, out uint v); return v; },
            v => GameConfig.Set(SystemConfigOption.SoundBgm, v),
            Configuration.BgmDuckToPercent);
        _spatialAudio = new SpatialAudioController(
            _screenController,
            msg => _rendererClient.SendAsync(msg).ConfigureAwait(false),
            _bgmDucker)
        {
            MaxDistance = Configuration.SpatialMaxDistance,
            MasterVolume = Configuration.MasterVolume,
            Muted = Configuration.Muted,
        };
        _placementWindow = new PlacementWindow(
            _screenController,
            () => (CameraInfo.GetPlayerPosition(ObjectTable), CameraInfo.GetCamera().Forward),
            onCommit: () => { },
            onCancel: () => { },
            getDisplayName: () => Configuration.KosmiDisplayName,
            copyToClipboard: CopyToClipboard);

        // No more control-mode split (the video window handles input directly, see
        // ViewerWindow) — every other session action lives in one settings window instead of
        // being scattered across overlay buttons.
        _configWindow = new ConfigWindow(
            Configuration,
            SetAudio,
            HandleJoinInput,
            CloseRoom,
            openPlacement: () => _placementWindow.IsOpen = true,
            copyInvite: CopyInvite,
            toggleFocus: _screenFocus.Toggle,
            // CurrentRoomCode (not Session.State != Idle) is the "did the user actually ask to
            // join something" signal — it's only ever set by a successful TryOpenRoom and
            // cleared by CloseRoom, so it can't show a stray "Leave Room" button before anyone
            // has joined anything.
            hasActiveSession: () => _sessionController.CurrentRoomCode is not null,
            hasActiveScreen: () => _screenController.ActiveAnchor is not null,
            getSessionMembers: () => _sessionMembers);

        // Groups (Phase 3a, design.md §9.3): a chat-detected WA1:/WA1P: token becomes a
        // clickable link, routed through the same confirmation the /wa join and /wa sync
        // commands use (group-invites spec "Joining an invite always requires explicit
        // confirmation").
        _inviteChatDetector = new InviteChatDetector(ChatGui, ShowJoinConfirmation, ShowSyncConfirmation);

        // Tell-relay session roster: Kosmi's own room chat can't carry a join announcement
        // (WatchAlong joins Kosmi anonymously, so its member list never has a real display name,
        // and there's no reliable way to actually submit a message into Kosmi's own chat UI from
        // here). Instead, accepting an invite tells its sender directly (see ShowJoinConfirmation);
        // this listens for that tell on the sender's side to populate their own roster.
        _tellRosterListener = new TellRosterListener(ChatGui, name => _sessionMembers.Add(name));

        // viewer-playback spec "Placement removed": revert to flat audio the moment the anchor
        // goes away, rather than waiting for the next 50ms spatial-audio tick.
        _screenController.ActiveAnchorChanged += () =>
        {
            if (_screenController.ActiveAnchor is null)
                _spatialAudio.RevertToFlat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        };

        // venue-memory spec "Arriving at a remembered location with no active session
        // automatically rejoins it" — independent of ScreenController's own anchor-restore
        // subscription, since this cares about session state, not placement.
        _locationService.LocationChanged += OnLocationChangedForVenueMembership;

        _windowSystem.AddWindow(_viewerWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_diagnosticsWindow);
        _windowSystem.AddWindow(_placementWindow);
        _windowSystem.AddWindow(_confirmationWindow);

        CommandManager.AddHandler("/wa", new CommandInfo(OnCommand) { HelpMessage = "Toggle the WatchAlong viewer, or run a subcommand (join/invite/sync/settings/place/focus/forgetvenues/vol/mute/snapshot)." });

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
        Framework.Update += OnFrameworkUpdate;

        _rendererClient.Start(_pipeName);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
        CommandManager.RemoveHandler("/wa");
        _locationService.LocationChanged -= OnLocationChangedForVenueMembership;
        _inviteChatDetector.Dispose();
        _tellRosterListener.Dispose();
        _screenFocus.Dispose();

        // design.md D5: every lifecycle exit that can end a duck restores BGM explicitly — never
        // rely on a timer/finalizer to un-duck the player's music.
        _spatialAudio.RevertToFlat(0);
        _worldVideoRenderer.Dispose();
        _screenController.Dispose();
        _locationService.Dispose();

        _windowSystem.RemoveAllWindows();
        _rendererClient.DisposeAsync().AsTask().Wait();
        lock (_frameReaderLock)
            _frameReader?.Dispose();
        _texture.Dispose();
        _rendererProcess.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Deliberately no D3D11 work here. FFXIVClientStructs documents the game's device
        // context as tied to its own multi-threaded command-recording pipeline ("Threads have
        // their own Context... commands are executed later by the ImmediateContext") — the
        // Framework::Update tick is not part of that pipeline's timing, but UiBuilder.Draw is
        // (it runs inside Dalamud's own D3D11 Present hook). Calling CreateTexture2D/Map/Unmap
        // from here was the likely cause of a native access-violation crash in d3d11.dll on
        // DXVK's async command-stream thread, appearing the first time a frame was uploaded.
        // The renderer only keeps writing its shmem heartbeat while a room's browser is open
        // (RendererApp disposes it on CloseRoom) — so once the session goes Idle, the heartbeat
        // is permanently frozen at its last value, not actually stale. Feeding that frozen
        // timestamp to the watchdog after leaving a room made it think the (perfectly healthy,
        // just idle) renderer process had hung, and force-restart it every ~5s forever. Only
        // treat the heartbeat as meaningful while a room is actually expected to be producing one.
        DateTimeOffset? heartbeat = null;
        if (_sessionController.Session.State != KosmiSessionState.Idle)
        {
            lock (_frameReaderLock)
                heartbeat = _frameReader?.WriterHeartbeat;
        }

        _watchdog.Tick(DateTimeOffset.UtcNow, _rendererProcess.IsRunning, heartbeat);

        var (cameraPos, cameraForward) = CameraInfo.GetCamera();
        var playerPos = CameraInfo.GetPlayerPosition(ObjectTable);
        var isMediaActive = _sessionController.Session.State == KosmiSessionState.InRoomPlaying;
        _spatialAudio.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cameraPos, cameraForward, playerPos, isMediaActive);

        _screenFocus.Tick((float)framework.UpdateDelta.TotalSeconds);
    }

    private void OnDraw()
    {
        var hasPlacedScreen = _screenController.ActiveAnchor is not null;
        var showPlacedScreen = hasPlacedScreen && ShouldShowPlacedScreen();

        // Depth/UI capture (Screens/Ported/DepthTested) must run before any ImGui rendering this
        // frame, per DepthBufferCapture/UILayerCapture's "call at the very start of OnDraw"
        // contract — a no-op unless a screen is both placed, visible, and in depth-tested mode.
        if (showPlacedScreen)
            _worldVideoRenderer.BeginFrame();

        lock (_frameReaderLock)
        {
            if (_frameReader is not null && (_viewerWindow.IsOpen || hasPlacedScreen))
                _frameReader.TryUpload(_texture);
        }

        if (_screenController.ActiveAnchor is { } anchor && showPlacedScreen)
        {
            // world-screens spec: shows a waiting placeholder rather than a stale/blank frame
            // when there's no active media (mirrors ViewerWindow's own no-media state).
            if (_texture.HasTexture && _sessionController.Session.State == KosmiSessionState.InRoomPlaying)
            {
                _worldVideoRenderer.Render(anchor.Transform, _texture.ShaderResourceView, _texture.ContentUv);
            }
            else
            {
                _worldVideoRenderer.RenderPlaceholder(anchor.Transform, "Waiting for the host to start something…");
            }
        }

        _windowSystem.Draw();
    }

    private void OnFrameRingAnnounced(FrameRingInfoMessage info)
    {
        var next = FrameReader.FromHandshake(info);
        FrameReader? previous;

        lock (_frameReaderLock)
        {
            previous = _frameReader;
            _frameReader = next;
        }

        previous?.Dispose();
    }

    /// <summary>
    /// Chromium source files that log at ERROR severity for optional Windows integrations they
    /// then gracefully fall back from — routine under Wine (the APIs genuinely aren't there:
    /// WinRT geolocation capabilities, Network Location Awareness, DirectComposition), not
    /// something WatchAlong can fix or should alarm on. Matched by source file so new log lines
    /// from the same known-benign subsystems don't need adding one at a time.
    /// </summary>
    private static readonly string[] BenignRendererStderrSources =
    [
        "address_sorter_win.cc", // SIO_ADDRESS_LIST_SORT (WSAEOPNOTSUPP) — address list sorting
        "system_geolocation_source_win.cc", // IAppCapability statics unavailable under Wine
        "network_change_notifier_win.cc", // WSALookupServiceBegin (NLA) unsupported under Wine
        "direct_composition_support.cc", // DCompositionCreateDevice3 not implemented under Wine
        "socket_manager.cc", // WebRTC ICE gathering trying (and failing) unreachable/mDNS STUN/TURN candidates — routine, not a call failure
    ];

    private static bool IsBenignRendererStderrLine(string line) =>
        Array.Exists(BenignRendererStderrSources, source => line.Contains(source, StringComparison.Ordinal));

    private bool ShouldShowPlacedScreen()
    {
        var isInCutscene = Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent]
            || Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene]
            || Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene78];

        return ScreenVisibilityGate.ShouldShow(isInCutscene, ClientState.IsGPosing, Configuration.ShowScreensInGPose);
    }

    private void SetAudio(double volume, bool muted)
    {
        Configuration.MasterVolume = (float)volume;
        Configuration.Muted = muted;
        _spatialAudio.MasterVolume = (float)volume;
        _spatialAudio.Muted = muted;

        // Flat audio only applies when no screen is world-placed (viewer-playback spec) — once
        // one is, SpatialAudioController's own Tick loop is what sends SetAudio.
        if (_screenController.ActiveAnchor is null)
            _rendererClient.SendAsync(new SetAudioMessage(volume, muted)).ConfigureAwait(false);
    }

    private bool JoinRoom(string roomUrlOrCode)
    {
        if (!_sessionController.TryOpenRoom(roomUrlOrCode, Configuration.KosmiDisplayName, 1280, 720, Configuration.MaxFps, Configuration.VoiceMode, false))
            return false;

        _sessionMembers.Clear();
        return true;
    }

    /// <summary>
    /// venue-memory spec: silently rejoins a room previously joined at this location, when idle.
    /// Deliberately does not set <c>_viewerWindow.IsOpen</c> — unlike a manual join, this fires
    /// ambiently while just walking around, and should only make the world screen (if one is
    /// anchored here) appear, not pop a window open uninvited. A failed rejoin (stale room) is
    /// left to <see cref="JoinRoom"/>'s existing no-throw, bool-return contract — no new error UI,
    /// and no retry until this fires again for a new location change.
    /// </summary>
    private void OnLocationChangedForVenueMembership(string locationKey)
    {
        if (!Configuration.AutoRejoinRememberedVenues)
            return;
        if (_sessionController.CurrentRoomCode is not null)
            return;
        if (_venueMembership.TryLoad(locationKey) is not { } roomCode)
            return;

        JoinRoom(KosmiRoomUrl.ToUrl(roomCode));
    }

    /// <summary>Shared by `/wa join` and the Settings window's join field: a `WA1:` invite goes through the confirmation flow, anything else joins directly like before.</summary>
    private bool HandleJoinInput(string input)
    {
        if (input.StartsWith(InviteCodec.InvitePrefix, StringComparison.Ordinal))
        {
            if (!InviteCodec.TryDecodeInvite(input, out var invite))
                return false;

            ShowJoinConfirmation(invite);
            return true;
        }

        if (!JoinRoom(input))
            return false;

        _viewerWindow.IsOpen = true;
        return true;
    }

    private void OnOpenMainUi() => _viewerWindow.IsOpen = true;

    private void OnOpenConfigUi() => _configWindow.IsOpen = true;

    /// <summary>
    /// Leave-then-rejoin used to land in a restart loop: RendererApp disposes the frame ring's
    /// writer on CloseRoom, but nothing ever cleared <see cref="_frameReader"/> here, so it kept
    /// pointing at that now-frozen reader. OnFrameworkUpdate only skips feeding the watchdog a
    /// heartbeat while Session.State is Idle — the instant TryOpenRoom moves it out of Idle again
    /// (immediately, before any new FrameRingInfoMessage/OnFrameRingAnnounced can replace this
    /// field), the stale, long-frozen heartbeat got fed straight to the watchdog, which read it as
    /// a hung renderer and force-restarted the very process that was mid-reconnect — aborting the
    /// join and repeating forever. Clearing it here closes that window: it stays null (heartbeat
    /// skipped entirely, same as the Idle case) until the new room's genuine ring is announced.
    /// </summary>
    private void CloseRoom()
    {
        _sessionController.CloseRoom();
        _sessionMembers.Clear();
        lock (_frameReaderLock)
        {
            _frameReader?.Dispose();
            _frameReader = null;
        }
    }

    private static void CopyToClipboard(string text) => ImGui.SetClipboardText(text);

    /// <summary>"Copy invite" (tasks.md 5.1/5.2): encodes the active session's room and (if present) placed screen, then copies it — never transmitted anywhere else.</summary>
    private void CopyInvite()
    {
        if (_sessionController.CurrentRoomCode is not { } roomCode)
        {
            Log.Warning("WatchAlong: no active Kosmi session to invite from.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(Configuration.KosmiDisplayName) ? "Watch-along" : Configuration.KosmiDisplayName;
        CopyToClipboard(InviteCodec.EncodeInvite(roomCode, name, _screenController.ActiveAnchor));
        Log.Information("WatchAlong: invite copied to clipboard.");
    }

    /// <summary>
    /// group-invites spec "Joining an invite always requires explicit confirmation" — the one
    /// path both a clicked chat link and a typed <c>/wa join</c> invite route through (tasks.md
    /// 3.3, 4.2, 4.3). <paramref name="senderCharacterName"/> is the FFXIV character who posted
    /// the invite in chat (null for a typed <c>/wa join</c>, which has no chat line to attribute)
    /// — on success it's told directly so their own client learns we joined (tell-relay roster;
    /// see <see cref="TellRosterListener"/>).
    /// </summary>
    private void ShowJoinConfirmation(DecodedInvite invite, string? senderCharacterName = null)
    {
        _confirmationWindow.Show(
            $"Join Kosmi room '{invite.Name}'? You'll appear there as '{Configuration.KosmiDisplayName}'.",
            () =>
            {
                if (!JoinRoom(KosmiRoomUrl.ToUrl(invite.RoomCode)))
                    return;

                _viewerWindow.IsOpen = true;
                if (invite.Anchor is not null)
                {
                    _screenController.ApplyExternalAnchor(invite.Anchor);
                    // venue-memory spec "Accepting an anchored invite remembers its room for that
                    // location": no anchor means no location to key this by, so this stays inside
                    // the same guard ApplyExternalAnchor already needs.
                    _venueMembership.Remember(invite.Anchor.LocationKey, invite.RoomCode);
                }
                _sessionMembers.Add(invite.Name);

                if (senderCharacterName is not null)
                    AnnounceJoinTo(senderCharacterName);
            });
    }

    /// <summary>Tells <paramref name="characterName"/> that we accepted their invite, so the tell-relay roster listener on their end can add us (see <see cref="TellRosterListener"/>). Best-effort: a failure here (e.g. a stale/unresolvable signature) only costs that one roster entry, never the join itself.</summary>
    private void AnnounceJoinTo(string characterName)
    {
        var displayName = string.IsNullOrWhiteSpace(Configuration.KosmiDisplayName) ? "Watch-along" : Configuration.KosmiDisplayName;
        try
        {
            EcChat.SendMessage($"/tell {characterName} {RosterAnnouncement.Build(displayName)}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"WatchAlong: failed to send the join tell to {characterName}.");
        }
    }

    /// <summary>group-invites spec "Confirming a position sync applies the anchor without touching the room" — never opens/closes a room (tasks.md 3.3, 6.3).</summary>
    private void ShowSyncConfirmation(DecodedPositionShare share)
    {
        _confirmationWindow.Show(
            $"Sync screen to {share.Name}'s placement?",
            () =>
            {
                _screenController.ApplyExternalAnchor(share.Anchor);
                _sessionMembers.Add(share.Name);
            });
    }

    private void WipeAndRedownload()
    {
        // Real download wiring (DependencyManager + an IRuntimeSource for CEF) lands with the
        // dependency-acquisition task; this button already reaches the right place to call it.
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "":
                _viewerWindow.IsOpen = !_viewerWindow.IsOpen;
                break;

            case "join" when parts.Length > 1:
                // group-invites spec "Confirming from a typed command": a pasted invite goes
                // through the same confirmation as a clicked chat link; a plain Kosmi URL/code
                // still joins directly with no confirmation (regression 7.4).
                if (!HandleJoinInput(parts[1].Trim()))
                    Log.Warning($"WatchAlong: '{parts[1]}' is not a valid Kosmi room link or invite.");
                break;

            case "invite":
                CopyInvite();
                break;

            case "sync" when parts.Length > 1:
                if (InviteCodec.TryDecodePositionShare(parts[1].Trim(), out var share))
                    ShowSyncConfirmation(share);
                else
                    Log.Warning("WatchAlong: that position-share message is malformed or invalid.");
                break;

            case "view":
                _viewerWindow.IsOpen = true;
                break;

            case "settings":
                _configWindow.IsOpen = true;
                break;

            case "place":
                _placementWindow.IsOpen = true;
                break;

            case "focus":
                _screenFocus.Toggle();
                Log.Information($"WatchAlong: {_screenFocus.Status}");
                break;

            case "forgetvenues":
                _venueMembership.ClearAll();
                Log.Information("WatchAlong: forgot every remembered watch-along room.");
                break;

            case "vol" when parts.Length > 1 && float.TryParse(parts[1], out var vol):
                SetAudio(Math.Clamp(vol / 100.0, 0, 1.5), Configuration.Muted);
                break;

            case "mute":
                SetAudio(Configuration.MasterVolume, !Configuration.Muted);
                break;

            case "snapshot":
                var snapshotDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "cef-profile", "debug-snapshots");
                Log.Information($"WatchAlong: requesting a debug snapshot (screenshot + DOM outline) -> {snapshotDir}");
                _rendererClient.SendAsync(new DebugSnapshotMessage()).ConfigureAwait(false);
                break;

            default:
                Log.Warning($"WatchAlong: unrecognized /wa subcommand '{sub}'.");
                break;
        }
    }
}
