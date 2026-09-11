# WatchAlong — Kosmi Watch-Along Screens for FFXIV

**Design Document — Draft v0.1 — 2026-09-10**
Author: Thiago (RayPetal) · Target: Dalamud plugin + helper renderer process + small group server
Working name only. Don't ship with "Kosmi" in the plugin name without asking Kosmi first; it's their trademark.

Reference code studied for this document:

| Source | Revision | Why it matters |
|---|---|---|
| `Sebane1/XivMediaPlayer` | `1c3baac` (2026-08-30) | The feature we're cloning: in-game video windows, world-space TVs, spatial audio, room sync |
| `Styr1x/Browsingway` | HEAD (2026-05) | Proven pattern for running Chromium (CEF) *outside* the game process, including on Linux |
| `cefsharp/CefSharp` | master (~v149) | Exact offscreen rendering, audio capture, and permission APIs we'll rely on |

Items marked **[VERIFY]** are assumptions I could not confirm from outside (mainly Kosmi internals — `app.kosmi.io` blocks automated fetches, so its DOM could not be inspected). Phase 0 exists specifically to close those.

---

## 1. Summary
1
WatchAlong puts a Kosmi room on a screen inside FFXIV — either a floating window or a TV placed in the world — and lets a group of players watch it together.

The core idea is simple: **every viewer's plugin joins the Kosmi room as a real Kosmi guest**, using a headless Chromium running in a helper process. Kosmi already keeps all room members in sync (and relays screen shares through its own media servers), so we don't write any sync logic for playback. We capture the pixels and the audio from that headless browser and put them on an in-game screen with spatial audio.

A **group** is one Kosmi room plus the FFXIV-side layer Kosmi can't provide: in-game invites, a shared screen position (so everyone sees the TV in the same spot), member presence/status, host controls, and venue binding for houses.

What gets reused from XivMediaPlayer (AGPL-3.0, so WatchAlong is AGPL too): the D3D11 texture upload, the world-screen renderer and placement gizmo, location keys, spatial-audio math, BGM ducking, and Wine detection. What gets dropped: VLC, yt-dlp, ffmpeg, Twitch/RTSP/SABR handling, and the stream proxy. Those may return later only as a codec fallback (§13).

The three risks that can sink the project, all tested in Phase 0 before any real code: Kosmi's page structure (we depend on it and it can change), missing H.264 support in stock CEF builds, and CEF stability under Wine on your CachyOS setup.

---

## 2. Goals and non-goals

### Goals

- **G1 — Watch any Kosmi room in-game**, in a resizable window or on a world-placed screen, with the same content everyone in the Kosmi room sees (YouTube in Kosmi, screen shares, Kosmi's virtual browser, shared links).
- **G2 — Groups**: create a watch-along, invite players (cross-world is fine), everyone sees the same screen in the same place, host can switch rooms and move the screen, members' load status is visible.
- **G3 — Linux is first-class**: XIVLauncher.Core on Wine/Proton must work, not "experimental".
- **G4 — Spatial audio** that attenuates and pans with distance to the world screen, plus optional BGM ducking.
- **G5 — Control Kosmi from inside the game**: a "control mode" that shows Kosmi's full UI with mouse/keyboard forwarding, so the host can pick videos, start the virtual browser, etc. without alt-tabbing.

### Non-goals

- Any source other than Kosmi. No direct YouTube/Twitch URLs (that's XivMediaPlayer's job).
- Reimplementing Kosmi's protocol (GraphQL/WebRTC signaling). See D1.
- Re-streaming Kosmi content from one host to other players through our own infrastructure.
- DRM content. CEF has no Widevine; we don't add any.
- Publishing voice, webcam, or screen share *from* the game into Kosmi.
- Playing Kosmi's built-in games well. Control mode will technically allow it, but nothing is optimized for it.

---

## 3. Research findings

### 3.1 How XivMediaPlayer actually works

The repo is about 36k lines across four projects: `XivMediaPlayer` (the Dalamud plugin; `Plugin.cs` alone is ~7k lines), `MediaPlayerCore` (playback), `XivMediaPlayer.Server` (ASP.NET Core + EF/SQLite), and a legacy `MediaPlayer.cs`. It targets `net10.0-windows` with `Dalamud.NET.Sdk/15.0.0`.

**Frame path.** LibVLC decodes with video callbacks (`SetVideoFormatCallbacks` / `SetVideoCallbacks(Lock, null, Display)`) into a BGRA buffer. `Display` copies it into `MediaManager.LastFrame` under `FrameLock` and bumps `LastFrameCount`. On the draw thread, `VideoWindow` notices a new `LastFrameCount`, (re)creates a `Direct3D11VideoTexture` if the size changed, and uploads with `Map(WriteDiscard)` onto a `Dynamic` `B8G8R8A8_UNorm` texture created on **the game's own D3D11 device** (obtained via `FFXIVClientStructs...Kernel.Device.Instance()->D3D11DeviceContext`). The SRV pointer is handed to ImGui or to the world renderer.

This is the seam we plug into: anything that can produce a BGRA frame plus a sequence counter can drive the whole rendering stack.

**World screens.** `WorldVideoRenderer` has two modes: an ImGui mode that projects the quad corners with `WorldToScreen` and draws `AddImageQuad` (no occlusion), and a D3D11 depth-tested mode (`DepthTestedRenderer`, ~1.8k lines) that reads the game's depth buffer for per-pixel occlusion, plus glow, vignette, and UI-layer culling. `PlacementManipulator` is the in-world gizmo. In houses, placement controls only appear while the furnishing menu (`HousingGoods`/`MJIFurnishing` addons) is open — a sensible anti-griefing rule.

**Location keys.** `GetLocationKey()` produces `house_{world}_{territory}_{ward}_{plot}_{room}_{indoorHouseId}` indoors, `zone_{world}_{ward}_{territory}_plot_{plot}` on a plot, `island_{world}_{owner}` on Island Sanctuary, and a 60-yalm grid key for outdoor public screens.

**Sync.** Despite a `SyncClient.cs` using SignalR, it isn't wired into `Plugin.cs` and the server has no hub — it's dead code. Real sync is HTTP: clients poll `GET /api/rooms/{locationKey}/media` about every 10 s, the server stamps `dataAgeMs` so clients never trust their own clocks (`effectiveTime = timecode + dataAgeMs` while playing), and seekable VODs get corrected when drift exceeds ~2.5 s. Control is "last foreground writer wins" with an `ownerId` lease, `403` for venue locks, `409` for stale background heartbeats. Watch-party *events* (a directory of scheduled parties at venues) also exist.

**Spatial audio.** `MediaManager.UpdateVolumes` computes a listening position (a lerp between camera and player), pans with `AngleDir(cameraForward, dirToSource, cameraUp)`, and attenuates with a squared linear falloff over a 100-unit max distance. It also mutes the game's BGM through `SystemConfigOption.IsSndBgm` and restores it only if it was the one that muted it.

**CEF.** XivMediaPlayer already ships CefSharp.OffScreen 148, but **in-process** and only as a *URL resolver*: it loads a page, intercepts the first `.m3u8`/`.mp4` request, and hands that to VLC. Its CEF is initialized with `mute-audio`, `no-sandbox`, `disable-web-security`, and `allow-running-insecure-content`, plus workarounds for loading CefSharp under Dalamud (disabling CefSharp's module initializer, a custom `AssemblyResolve`, `SetDllDirectory`, and computing `DOTNET_ROOT` from the runtime directory so the subprocess can find Dalamud's private .NET). Those workarounds are hard-won knowledge worth keeping. The security flags are **not** acceptable for a browser that will hold Kosmi cookies (§6.3).

**Wine.** `StreamProxy.DetectWineRuntime()` checks `WINEPREFIX`/`WINELOADER`/`STEAM_COMPAT_DATA_PATH`/etc. and falls back to probing `ntdll!wine_get_version`. It avoids `HttpListener` under Wine because Wine aborts on `HttpCancelHttpRequest`. So the author does test on Linux, and that detection helper is directly reusable.

**Dependencies** (CEF, LibVLC, ffmpeg) are downloaded at runtime into the plugin config directory rather than shipped in the plugin zip.

**License: AGPL-3.0.** Anything we port makes WatchAlong AGPL. If the group server is derived from `XivMediaPlayer.Server`, the AGPL network clause means you must offer its source to users. Simplest path: make everything AGPL and public.

### 3.2 What to take and what to leave

| XivMediaPlayer component | Decision | Notes |
|---|---|---|
| `Direct3D11VideoTexture` | **Port** | Add device-lost handling and a UV content rect |
| `WorldVideoRenderer` (ImGui quad mode) | **Port in Phase 2a** | Easy, no occlusion |
| `DepthTestedRenderer`, `GlowRenderer`, `UILayerCapture`, etc. | **Port in Phase 2b** | Largest and riskiest chunk; port as a block, don't rewrite |
| `PlacementManipulator`, `WorldScreenTransform`, `MathUtils` | **Port** | |
| Location-key logic from `Plugin.cs` | **Extract** into `LocationService` | Keep key formats identical so venue keys stay compatible |
| Spatial-audio math, BGM mute/restore | **Port** | Moves to the plugin-side audio controller |
| `DetectWineRuntime`, CEF loader workarounds, `DependencyManager` pattern | **Port** | Loader workarounds move to the renderer process |
| LibVLC, yt-dlp, ffmpeg, `StreamProxy`, Twitch, SABR, Matroska | **Drop** | Optional return in Phase 5 as codec fallback only |
| HTTP media-state sync, `ownerId` leases, drift correction | **Drop** | Kosmi is the clock (D5). Drift logic returns only in the Phase 5 fallback |
| Watch-party directory concept | **Reuse the idea** | Rebuilt around groups in Phase 4 |
| In-process CEF with `disable-web-security` | **Replace** | Out-of-process renderer (D2) with a strict security policy |
| `Plugin.cs` god-class structure | **Don't copy** | Thin composition root + services |

### 3.3 Browsingway: the precedent for out-of-process CEF

Browsingway (a fork of ackwell's BrowserHost) renders web overlays in-game and has already solved the problem we face. The plugin launches `Browsingway.Renderer.exe`, a framework-dependent `net10.0-windows` executable that hosts CefSharp.OffScreen, and passes it the parent PID, a keep-alive handle name, the DXGI adapter LUID, and an IPC channel name. The renderer uses its own D3D11 device on the same adapter, copies each `OnPaint` buffer into a **legacy shared texture** (`D3D11_RESOURCE_MISC_SHARED` + `GetSharedHandle`), and the plugin opens it with `OpenSharedResource`. IPC runs over the `SharedMemory` package's `RpcBuffer` with FlatBuffers messages. The plugin watches the renderer and restarts it if it crashes.

Its CEF setup calls `settings.EnableAudio()` (CefSharp.OffScreen mutes audio by default), sets `autoplay-policy=no-user-gesture-required`, uses `SetOffScreenRenderingBestPerformanceArgs()`, and reports an honest user-agent suffix.

Browsingway documents Linux as working but "experimental and not supported". Its notes: newer XIVLauncher.Core ships the needed .NET, so don't install your own into the prefix; Proton-GE 10.27 performs well; wiping the CEF download fixes most breakage after Wine/Proton version changes.

### 3.4 Kosmi: what matters technically

From Kosmi's own pages:

- Guests join from a link with no account. Rooms are private by default and reachable only by link.
- Screen sharing uses the browser's built-in capture and carries system audio. It's free up to 720p; 1080p needs Kosmi Premium. **Once a room has more than two people, streams are relayed through Kosmi's media servers.** With exactly two people it may be peer-to-peer. [VERIFY]
- The shared **virtual browser streams to members as video**, and Kosmi syncs playback of YouTube, links, and files for all room members.
- Room URLs look like `https://app.kosmi.io/room/<code>` and `https://app.kosmi.io/room/@<username>`. [VERIFY exact format]
- Kosmi's GitHub org has a fork of `absinthe_graphql_ws`, which suggests an Elixir/Absinthe GraphQL-over-WebSocket backend. This is irrelevant to the design because we don't talk to their API directly, but it confirms that the alternative (reverse-engineering their protocol) would be a real project.
- Terms: Kosmi's T&C is hosted on iubenda, which blocked my fetch, so I couldn't read it. **Read it yourself before Phase 1**, looking specifically for clauses on automated clients, bots, and embedding. Also consider emailing support@kosmi.io. A friendly "we're building an FFXIV integration" message costs nothing and could get you a heads-up on UI changes or even an embed path.

**What this means for the design.** Everything worth showing ends up in the page as one of three things: a `<video>` element fed by WebRTC (screen share, virtual browser, probably local-file share), a `<video>` element with a normal source (links), or an `<iframe>` player (YouTube, Twitch). A real browser that joins the room gets all of it, already synced.

### 3.5 CEF constraints that shape the design

- **One CEF per process, once.** CEF can be initialized only once per process and can't be re-initialized after `Cef.Shutdown()`. In-process CEF breaks Dalamud plugin reloads, and it collides with any other plugin that loads CEF into the game (XivMediaPlayer does, lazily). → D2.
- **No H.264/AAC in stock builds.** CefSharp's release notes state that default builds lack proprietary codecs for licensing reasons: MP4 files don't play, MP3 audio does. This matters for three Kosmi scenarios:
  - YouTube serves VP9/AV1 + Opus to Chromium. **Fine.**
  - WebRTC (screen share, virtual browser): Chromium negotiates a codec with Kosmi's SFU. If the SFU accepts VP8/VP9, **fine**. If it insists on H.264, we can't decode it. **[VERIFY — highest-priority spike]**
  - Twitch embeds (H.264 HLS) and MP4 links: **won't play** in stock CEF. → §13.
- **Audio capture exists.** `IAudioHandler` gives `GetAudioParameters`, `OnAudioStreamStarted`, and `OnAudioStreamPacket(IntPtr data, int frames, long pts)` with planar float PCM. Once a handler is set, audio goes to us instead of the speakers.
- **Frames.** `OnPaint` delivers a full BGRA view buffer at up to `WindowlessFrameRate` (max 60). If `SharedTextureEnabled` is set, `OnAcceleratedPaint` delivers a pooled D3D11 shared handle instead; it must be copied inside the callback because the handle goes back to the pool.
- **Permissions.** `IPermissionHandler.OnRequestMediaAccessPermission` / `OnShowPermissionPrompt` let us deny camera, microphone, and screen capture.

---

## 4. Core design decisions

### D1 — Join Kosmi as a real browser client, don't reverse-engineer the protocol

A native client would need Kosmi's GraphQL schema, their WebRTC signaling, a .NET WebRTC stack (e.g. SIPSorcery) with VP8/VP9 decoders, and YouTube/iframe handling. It would break on every backend change and is the kind of access terms of service usually forbid. A headless Chromium joining the room looks to Kosmi exactly like one more guest. Every Kosmi feature works on day one, including ones they add later.

*Rejected alternative:* the host captures Kosmi and re-streams to the group through our own SFU. That's redistribution, costs bandwidth and money, and removes Kosmi from the loop.

### D2 — CEF runs in a separate renderer process

`WatchAlong.Renderer.exe` hosts CEF. The plugin talks to it over IPC. This gives crash isolation (a Chromium crash doesn't take down the game), clean plugin reloads, no conflict with other CEF-using plugins, no Dalamud assembly-loading hacks inside the game, and the ability to develop and test the renderer on its own (§16). Browsingway proves this works, including under Wine.

### D3 — Frames travel through shared memory by default, shared texture as an optional fast path

Baseline: the renderer writes BGRA frames into a triple-buffered memory-mapped ring. The plugin copies the newest frame into its dynamic texture using the ported `Direct3D11VideoTexture`. This costs one extra memcpy (about 110 MB/s at 720p30) but works anywhere Wine supports named file mappings, which is everywhere.

Fast path (Windows, opt-in, Phase 5): Browsingway-style legacy shared texture opened on the game device. It's auto-disabled under Wine because cross-process D3D11 sharing through DXVK is the least reliable piece. [VERIFY on Proton-GE]

### D4 — Audio stays in the renderer; the plugin sends only spatial parameters

The renderer captures CEF audio with `IAudioHandler` and plays it through NAudio in its own process. The plugin computes volume and pan from game positions at ~20 Hz and sends two floats over IPC. Audio never crosses the process boundary, A/V latency stays inside one process, and a renderer crash silences cleanly.

### D5 — Kosmi is the playback clock; our server only coordinates the group

We never seek, pause, or drift-correct Kosmi content. Every member is an independent Kosmi client kept in sync by Kosmi. The group server knows *who* is watching, *which room*, and *where the screen is* — nothing about playback. (The single exception is the Phase 5 codec fallback.)

### D6 — Theater mode through injected CSS, driven by a data-only selector profile

An injected script (`kosmi-agent.js`) finds the main media element, pins it full-viewport with a stylesheet, and reports its content rectangle so the plugin can crop by UV if the CSS approach fails. All Kosmi-specific selectors and heuristics live in a JSON **selector profile** that can be updated remotely without a plugin release. **The profile is data only — never code** — so a compromised profile host can't inject script. If Kosmi has its own fullscreen/theater toggle, prefer triggering that over CSS surgery: it's less fragile and closer to how the service is meant to be used.

### D7 — License: AGPL-3.0

This follows from porting XivMediaPlayer code. Credit Sebane1/XivMediaPlayer in the README and in the About tab.

---

## 5. Architecture

```
┌──────────────────────────── ffxiv_dx11.exe (game process) ────────────────────────────┐
│  WatchAlong (Dalamud plugin)                                                          │
│                                                                                       │
│  ┌──────────────┐  ┌───────────────┐  ┌─────────────────┐  ┌───────────────────────┐  │
│  │ GroupService │  │ KosmiSession  │  │ ScreenManager   │  │ SpatialAudioController│  │
│  │ (hub client, │─▶│ (state machine│─▶│ viewer window,  │  │ positions → vol/pan   │  │
│  │  invites)    │  │  per room)    │  │ world screens   │  │ BGM ducking           │  │
│  └──────┬───────┘  └──────┬────────┘  └────────▲────────┘  └──────────┬────────────┘  │
│         │                 │ IPC (pipe)         │ texture              │ IPC            │
│         │          ┌──────▼────────┐    ┌──────┴────────┐             │                │
│         │          │ RendererHost  │    │ FrameReader   │◀── shmem ───┼──┐             │
│         │          │ spawn/watchdog│    │ → D3D11 tex   │   ring      │  │             │
│         │          └──────┬────────┘    └───────────────┘             │  │             │
└─────────┼─────────────────┼───────────────────────────────────────────┼──┼─────────────┘
          │ SignalR/WSS     │ named pipe (control)                      │  │ frames
          │                 ▼                                           ▼  │
          │   ┌─────────────────── WatchAlong.Renderer.exe ───────────────────────────┐
          │   │  CefSharp.OffScreen  ── KosmiBrowser ── kosmi-agent.js (in page)      │
          │   │     │ OnPaint ─▶ FrameWriter ─▶ shmem ring ────────────────────────────┘
          │   │     │ IAudioHandler ─▶ AudioSink (NAudio, vol/pan from plugin) ─▶ speakers
          │   │     │ Permission/Navigation/Download policies                         │
          │   └─────┼─────────────────────────────────────────────────────────────────┘
          │         │ HTTPS / WSS / WebRTC
          ▼         ▼
 ┌─────────────────┐   ┌─────────────────────────┐
 │ WatchAlong.Server│   │ Kosmi (app.kosmi.io,    │
 │ groups, invites, │   │ SFU relays, YouTube...) │
 │ anchors, presence│   └─────────────────────────┘
 └─────────────────┘
```

### 5.1 Solution layout

```
WatchAlong.sln
├─ WatchAlong/                     Dalamud plugin (net10.0-windows, Dalamud.NET.Sdk 15.x)
│  ├─ Plugin.cs                    composition root only (<200 lines)
│  ├─ Core/                        Services.cs (Dalamud DI), Configuration.cs, WineDetect.cs
│  ├─ Renderer/                    RendererHost.cs, IpcClient.cs, FrameReader.cs
│  ├─ Kosmi/                       KosmiSession.cs, KosmiRoomUrl.cs, PageState.cs, MediaState.cs
│  ├─ Groups/                      GroupService.cs, GroupHubClient.cs, InviteCodec.cs,
│  │                               InviteChatDetector.cs, Models/
│  ├─ Screens/                     ScreenManager.cs, ScreenAnchor.cs, LocationService.cs,
│  │   └─ Ported/                  Direct3D11VideoTexture, WorldVideoRenderer, DepthTestedRenderer,
│  │                               PlacementManipulator, WorldScreenTransform, MathUtils (AGPL, credited)
│  ├─ Audio/                       SpatialAudioController.cs, BgmDucker.cs
│  └─ UI/                          MainWindow, ViewerWindow, ControlModeWindow, GroupTab,
│                                  SettingsTab, Toasts
├─ WatchAlong.Renderer/            exe (net10.0-windows, framework-dependent, CefSharp.OffScreen.NETCore)
│  ├─ Program.cs                   arg parsing, keep-alive, IPC server loop
│  ├─ CefBootstrap.cs              settings/flags, loader workarounds
│  ├─ KosmiBrowser.cs              one ChromiumWebBrowser + handlers
│  ├─ Policies/                    NavigationPolicy, PermissionPolicy, DownloadPolicy, PopupPolicy
│  ├─ FrameWriter.cs               shmem ring writer
│  ├─ AudioSink.cs                 IAudioHandler → ring → NAudio
│  ├─ InputRouter.cs               mouse/key/text → CEF host events
│  ├─ Harness/                     --harness mode (PNG dumps, no game required)
│  └─ Assets/                      kosmi-agent.js, theater.css, default-profile.json
├─ WatchAlong.Shared/              IPC message contracts, shmem layouts, InviteCodec (shared with server)
├─ WatchAlong.Server/              ASP.NET Core minimal API + SignalR hub, SQLite
└─ tests/                          WatchAlong.Tests (unit), fixtures/fake-kosmi/ (static test pages)
```

### 5.2 Threading model (plugin side)

| Thread | Work |
|---|---|
| `IFramework.Update` (game thread) | Location/anchor resolution, object-table reads, spatial-audio math (throttled to 20 Hz), config dirty flush |
| `UiBuilder.Draw` (render thread) | `FrameReader.TryUpload()` (at most one memcpy per frame), world-screen rendering, ImGui windows |
| Background tasks | IPC read loop, SignalR hub, renderer watchdog. They **only** post into lock-free queues that the two threads above drain. |

Rule inherited from experience with XivMediaPlayer's `Plugin.cs`: no game-memory access (object table, `HousingManager`) off the game thread, and no D3D calls off the render thread.

---

## 6. Renderer process

### 6.1 Lifecycle

1. The plugin resolves the dependency directory (CEF runtime downloaded to `pluginConfigs/WatchAlong/Dependencies/cef-<version>/`, same pattern as XivMediaPlayer and Browsingway).
2. `RendererHost` spawns `WatchAlong.Renderer.exe` with `UseShellExecute=false`, `CreateNoWindow=true`, redirected stdout/stderr into `IPluginLog`, and arguments: parent PID, IPC pipe name, shmem base name, CEF dir, cache dir, log level, Wine flag, protocol version. It sets `DOTNET_ROOT` exactly like XivMediaPlayer's `CefSharpResolver` does, so the framework-dependent renderer finds Dalamud's .NET.
3. **Keep-alive:** the renderer polls the parent PID every second and exits if it's gone, so a game crash never leaves a zombie Chromium playing audio.
4. **Watchdog:** the plugin checks `HasExited` about once a second (off the game thread) and watches a heartbeat timestamp in the shmem header. On a crash or stall over 5 s it restarts with backoff (1 s, 2 s, 5 s, 15 s, then stop and show an error with a "Retry" button). After a restart, `KosmiSession` replays its last `OpenRoom`.
5. Only one renderer instance and one Kosmi browser for v1. Multiple world screens show the same frame.

### 6.2 CEF settings

| Setting / flag | Value | Why |
|---|---|---|
| `RootCachePath` / `CachePath` | `pluginConfigs/WatchAlong/cef-profile/` | Persistent cookies so a Kosmi login (if used) survives restarts. Separate from any other plugin's CEF. |
| `settings.EnableAudio()` | on | CefSharp.OffScreen mutes by default |
| `autoplay-policy` | `no-user-gesture-required` | No real clicks happen in theater mode |
| `SetOffScreenRenderingBestPerformanceArgs()` | on (always under Wine) | Software compositing; GPU under Wine is a crash risk |
| `disable-background-media-suspend`, `disable-renderer-backgrounding`, `disable-background-timer-throttling` | on | We throttle frame rate ourselves; Chromium must never pause media because it thinks the page is hidden |
| `no-sandbox` | on under Wine only | Chromium's sandbox doesn't work under Wine. On Windows keep the sandbox if it cooperates with Dalamud's environment [VERIFY]; otherwise document the tradeoff. |
| `disable-web-security`, `allow-running-insecure-content` | **never** | This browser holds Kosmi session cookies |
| `WindowlessFrameRate` | 30 default, 60 max, 1 when no screen is visible | Keeps audio flowing while hidden |
| WebRTC IP handling (`webrtc.ip_handling_policy` pref) | `default_public_interface_only`; `disable_non_proxied_udp` when "Hide my IP from peers" is on | Two-person rooms may be P2P |
| User-agent | Stock Chrome UA + ` WatchAlong/<ver>` product suffix | Honest, like Browsingway. If Kosmi rejects it, drop the suffix and note it in known issues. |
| Viewport | 1280×720 default (1920×1080 if "High quality") | Matches Kosmi's free 720p cap; saves CPU |

### 6.3 Security policy

The renderer runs a full browser, possibly unsandboxed under Wine, holding a Kosmi session. It's locked down accordingly:

- **Top-level navigation allowlist** (`IRequestHandler.OnBeforeBrowse`): only `https://app.kosmi.io/*` plus, in control mode only, the OAuth hosts Kosmi's login needs (populated during Phase 0). Everything else is cancelled. Subresources (CDNs, YouTube iframes, SFU WebSockets) load normally.
- **Popups** (`ILifeSpanHandler.OnBeforePopup`): denied in theater mode. In control mode, allowlisted OAuth popups open in a second offscreen browser shown in its own ImGui window. Note that Google blocks sign-in from embedded browsers, so "Sign in with Google" will fail; guest mode or another login method is the answer.
- **Permissions:** deny camera, microphone, screen capture (`getDisplayMedia`), geolocation, notifications, clipboard-read, and MIDI. Mic stays off in v1.
- **Downloads:** always cancelled. **JS dialogs:** suppressed and logged. **`file://`:** blocked.
- **URL validation** happens twice: in the plugin (`KosmiRoomUrl.TryParse`) and again in the renderer before `LoadUrl`. A malicious invite or compromised group server can therefore never make a client load a non-Kosmi page.
- **Selector profile** is parsed as JSON with a strict schema. Strings are used only as CSS selectors or text patterns and are never `eval`'d.

### 6.4 The Kosmi page agent (`kosmi-agent.js`)

It's injected at document start (via the render-process context-created hook, re-injected on every navigation) and talks to .NET through `CefSharp.PostMessage` → `JavascriptMessageReceived`. It has five jobs.

**1. Page-state detection**, driven by the profile's `stateProbes`:

```
Loading → JoinGate (name prompt / "Join room" button) → InRoom
                  ↘ LoginRequired / RoomNotFound / Kicked / Full / Unknown
```

`Unknown` persisting for more than 10 s triggers a debug snapshot (PNG plus a DOM outline with text stripped, saved locally) so selector breakage can be diagnosed from a user report.

**2. Join-gate automation.** Fill the display-name input and click join. Kosmi is presumably a React app [VERIFY], so the input must be set through the native value setter followed by a bubbling `input` event, or React won't register it. If the automation fails twice, the session goes to `NeedsUser`, and the plugin offers control mode with a banner telling the user what to click.

**3. Primary-media location** (heuristic plus profile overrides). Candidates are all `video`, `iframe`, and `canvas` elements. Each is scored:

- visible area (`getBoundingClientRect` ∩ viewport) is the dominant term;
- bonus for a `video` with a live video track (`srcObject.getVideoTracks()[0].readyState === "live"`) or `videoWidth ≥ 640`;
- bonus if an iframe `src` matches a known player host (youtube, twitch, vimeo);
- **penalty** if the element is inside anything matching the profile's `participantTileSelectors` (webcam grid) or is near-square and small.

The winner needs a 1.5 s hysteresis before replacing the current primary, so a webcam flicker never steals the screen.

**4. Theater mode.** Mark the winner `data-wa-primary`, mark its ancestors `data-wa-ancestor`, and toggle `html.wa-theater`:

```css
html.wa-theater body * { visibility: hidden !important; }
html.wa-theater [data-wa-primary],
html.wa-theater [data-wa-primary] * { visibility: visible !important; }
html.wa-theater [data-wa-ancestor] { transform: none !important; filter: none !important;
                                     contain: none !important; overflow: visible !important; }
html.wa-theater [data-wa-primary] { position: fixed !important; inset: 0 !important;
  width: 100vw !important; height: 100vh !important; max-width: none !important;
  z-index: 2147483647 !important; object-fit: contain !important; background: #000 !important; }
html.wa-theater, html.wa-theater body { background: #000 !important; }
```

The ancestor rule matters because `position: fixed` is trapped by any ancestor with a `transform`. DOM nodes are **never moved** because that breaks React. Regardless of whether the CSS worked, the agent reports the element's content rectangle (accounting for `object-fit` letterboxing via `videoWidth`/`videoHeight`). The FrameWriter stamps that rectangle into each frame so the plugin crops by UV. If the CSS approach breaks after a Kosmi update, the crop still produces a correct picture — just at lower resolution.

**5. Media state and room info**, at 2 Hz and on change:

```json
{ "type": "media", "kind": "webrtc|html5|iframe|none", "w": 1280, "h": 720,
  "paused": false, "currentTime": 812.4, "src": "https://...", "error": null,
  "rect": [0, 0, 1280, 720] }
{ "type": "room", "title": "Movie night", "members": ["Ray", "Aya (in-game)"], "presenter": "Ray" }
```

`currentTime` and `src` are only filled for `html5`; for `iframe` only the `src` is readable (cross-origin). Member list and presenter are best-effort through profile selectors.

**Voice/cam muting.** In `RoomVoiceMode.Off` (the default), the agent sets `muted = true` on every media element except the primary and re-applies through a `MutationObserver`. **[VERIFY]** If Kosmi plays voice chat through WebAudio instead of media elements, per-element muting won't catch it. In that case the fallback is to offer only "voice included" or "all muted", and to document it.

**Selector profile example** (`default-profile.json`, shipped embedded; a newer version is fetched from GitHub raw or the group server, schema-validated, and cached):

```json
{
  "schema": 1,
  "profileVersion": "2026.09.10-1",
  "minAgentVersion": 1,
  "stateProbes": {
    "joinGate":      { "any": ["input[name='nickname']", "button:has-text('Join')"] },
    "roomNotFound":  { "textMatches": ["room (was )?not found", "doesn't exist"] },
    "loginRequired": { "any": ["[data-testid='login-modal']"] }
  },
  "joinGate": { "nameInput": "input[name='nickname']", "submit": "button[type='submit']" },
  "participantTileSelectors": [".participant", "[class*='webcam']"],
  "primaryOverrides": [],
  "fullscreenButton": null,
  "chat": { "list": null, "input": null, "send": null }
}
```

All selectors above are **placeholders** until Phase 0 (S6) replaces them with real ones. `:has-text()` isn't valid CSS, so the agent implements it as a tiny selector extension (tag selector + text regex).

### 6.5 FrameWriter: shared-memory ring

```csharp
// WatchAlong.Shared/FrameRing.cs — layout v1, all little-endian, 8-byte aligned
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameRingHeader
{
    public uint  Magic;              // 0x52464157 'WAFR'
    public ushort Version;           // 1
    public ushort SlotCount;         // 3
    public int   MaxWidth, MaxHeight;// e.g. 1920x1080 — ring sized for max
    public long  SlotStride;         // sizeof(FrameSlotHeader) + MaxWidth*MaxHeight*4, 64-byte aligned
    public long  PublishedSeq;       // last complete frame; slot = seq % SlotCount
    public long  WriterHeartbeatTicks; // updated even with no new frames (watchdog)
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameSlotHeader
{
    public long SeqBegin;            // written first
    public int  Width, Height, Stride;
    public int  ContentX, ContentY, ContentW, ContentH; // video rect inside viewport → UV crop
    public long CaptureTicks;
    public long SeqEnd;              // written last (after pixels)
}
```

**Writer** (CEF UI thread, in `OnPaint`): `seq = PublishedSeq + 1`, `slot = seq % 3`. Write `SeqBegin = seq`, copy the BGRA buffer (full frame; dirty-rect tracking isn't worth it with triple buffering), fill dimensions and content rect, write `SeqEnd = seq`, then `Volatile.Write(ref PublishedSeq, seq)`.

**Reader** (plugin render thread): `s = Volatile.Read(PublishedSeq)`. If `s == lastSeen`, skip. Otherwise read the slot and verify `SeqBegin == SeqEnd == s` after copying into the mapped D3D texture; on mismatch (the writer lapped us), discard that frame and try again next frame. With three slots, a 30 fps writer, and a 60 fps reader, laps are rare.

The ring is sized once for the max resolution (3 × 8.3 MB ≈ 25 MB at 1080p). A viewport resize changes only the slot headers, never the mapping.

### 6.6 AudioSink

- `GetAudioParameters`: request 48 kHz, stereo layout, 1024 frames per buffer.
- `OnAudioStreamPacket`: interleave CEF's planar floats into a lock-free SPSC ring holding about 250 ms.
- Output: NAudio `WasapiOut` in shared mode with 60–100 ms latency; fall back to `WaveOutEvent` if WASAPI init fails or crackles under Wine (configurable).
- DSP in the provider: constant-power stereo balance for pan (NAudio's `PanningSampleProvider` is mono-only), then gain. Parameters arriving from the plugin are **ramped over 50 ms** to avoid zipper noise when walking past the screen.
- Underruns output silence and bump a counter shown on the diagnostics tab.

### 6.7 InputRouter (control mode only)

- **Mouse:** the plugin maps ImGui cursor position in the viewer image to CEF view coordinates (accounting for UV crop and window scale) and sends `SendMouseMoveEvent`, `SendMouseClickEvent`, `SendMouseWheelEvent`.
- **Keyboard:** while the control window is focused, the plugin calls `ImGui.SetNextFrameWantCaptureKeyboard(true)` so Dalamud stops the game from receiving keys. It then forwards `io.InputQueueCharacters` as `KeyEvent` chars and a mapped subset of `ImGuiKey` (Enter, Backspace, Tab, arrows, Esc, Ctrl+A/C/V/X/Z) as raw key down/up with Windows VK codes.
- **Paste:** read the clipboard in the plugin, send it as text. The renderer never gets clipboard-read permission.
- **IME:** not supported in v1. Document it.

---

## 7. IPC protocol

**Transport:** one duplex named pipe `\\.\pipe\WatchAlong-<gamePid>-<nonce>`, carrying 4-byte length-prefixed UTF-8 JSON (System.Text.Json with source generation). Message rates are tiny (≤ 50/s), so JSON is fine and easy to debug. If named pipes misbehave under Wine in spike S5, switch to Browsingway's `SharedMemory` `RpcBuffer`, which is proven there. Frames and audio never go over the pipe.

**Handshake:** the renderer connects and sends `Hello`. The plugin replies with `HelloAck`. A mismatched `protocol` major version makes the renderer exit with code 3 and the plugin tell the user to wipe dependencies.

| Direction | Message | Payload |
|---|---|---|
| P→R | `HelloAck` | `protocol`, `logLevel`, `isWine` |
| P→R | `OpenRoom` | `roomUrl`, `displayName`, `viewport {w,h}`, `fps`, `voiceMode`, `hideIp` |
| P→R | `CloseRoom` | — |
| P→R | `SetViewMode` | `Theater` \| `Full` (control mode) |
| P→R | `SetViewport` / `SetFrameRate` | `w,h` / `fps` |
| P→R | `SetAudio` | `volume 0..1.5`, `pan -1..1`, `muted` (≤ 20 Hz, change-only) |
| P→R | `SetVoiceMode` | `Off` \| `Flat` |
| P→R | `Input` | `mouseMove {x,y}` \| `mouseButton {x,y,btn,up,clicks}` \| `wheel {x,y,dx,dy}` \| `key {vk,up,mods}` \| `text {s}` |
| P→R | `SetProfile` | full profile JSON (already validated by the plugin) |
| P→R | `Reload` / `DebugSnapshot` / `Shutdown` | — |
| R→P | `Hello` | `protocol`, `cefVersion`, `chromiumVersion`, `codecs {h264, aac, vp9, av1}` (probed with `MediaSource.isTypeSupported` on `about:blank`) |
| R→P | `FrameRingInfo` | `mapName`, `slots`, `maxW`, `maxH` |
| R→P | `PageState` | `state`, `detail` |
| R→P | `MediaState` | as in §6.4 |
| R→P | `RoomInfo` | `title`, `members[]`, `presenter?` |
| R→P | `ChatMessage` (Phase 4) | `from`, `text`, `ts` |
| R→P | `AudioStats` | `underruns`, `latencyMs` (1 Hz) |
| R→P | `Error` | `code` (`CefInitFailed`, `NavigationBlocked`, `CodecUnsupported`, `AudioInitFailed`, …), `message` |
| R→P | `Log` | `level`, `msg` (mirrors to `IPluginLog`) |

---

## 8. Plugin side

### 8.1 `KosmiSession` state machine

```
          OpenRoom(url)
  Idle ───────────────▶ StartingRenderer ──▶ Loading ──▶ JoinGate ──auto──▶ InRoom.NoMedia
   ▲                          │                 │            │                  │  ▲
   │ Close                    │ fail            │            └─fail x2─▶ NeedsUser (control mode)
   │                          ▼                 ▼                               ▼  │
   └──────────────────── Error(kind) ◀── RoomNotFound / Kicked / LoginRequired  InRoom.Playing(kind)
                              │
                              └── transient (renderer crash, network) ──▶ Reconnecting (backoff) ──▶ Loading
```

`InRoom.Playing(kind)` carries a `MediaState`. When `kind == html5` and `error == 4` (source not supported), the session raises `CodecUnsupported`, the UI shows "This link uses a format the built-in browser can't play (H.264/MP4). Ask the host to use YouTube, screen share, or the virtual browser.", and the Phase 5 fallback hooks in here.

### 8.2 FrameReader and texture

`FrameReader.TryUpload(Direct3D11VideoTexture tex)` runs once per `UiBuilder.Draw`. It (re)creates the texture on size change, performs the seqlock-guarded copy directly from the mapped view into the `Map(WriteDiscard)` pointer (no intermediate `byte[]`, unlike XivMediaPlayer), and exposes `ContentUv` (min/max UV from the content rect). All screens share this one texture.

### 8.3 Screens

**ViewerWindow:** resizable ImGui window drawing the texture aspect-correct with `ContentUv`. It has a small overlay bar (volume, mute, pop-out borderless "cinema" mode, open control mode) and shows the page state when there's no media ("Waiting for the host to start something…").

**ControlModeWindow:** switches the renderer to `SetViewMode(Full)` and shows the entire Kosmi page with input forwarding and a yellow banner reading "You're controlling Kosmi — keyboard goes to the page". Closing it returns to theater mode. This is where the host picks videos and starts the virtual browser, and where anyone clears a join gate the automation couldn't.

**World screens** — `ScreenAnchor` variants:

| Anchor | Resolves to | Use |
|---|---|---|
| `Location { locationKey, transform }` | Visible only when the local `LocationService` key matches | Houses, apartments, island, outdoor grid (keys identical to XivMediaPlayer) |
| `Actor { hostTag, offset, yawOffset }` | Transform relative to the host's character, found in `IObjectTable` by name@world | "Screen floats behind the host" — works anywhere, e.g. a tavern |
| `Personal` | No world screen, viewer window only | Default for solo, and the fallback when you're not where the anchor is |

**Fallback rule:** if a group's anchor isn't resolvable for you (different zone or world), the content automatically shows in your viewer window. Nobody in the group is ever left without a picture.

**Placement:** only the host or co-hosts can move a group screen. In houses, keep XivMediaPlayer's rule (placement only while the furnishing menu is open). The ported `PlacementManipulator` gizmo, plus numeric fields and a "Place in front of me" button, are the tools.

**Rendering plan:** Phase 2a ports the ImGui quad path (`AddImageQuad`, no occlusion). Phase 2b ports the depth-tested path as a block with glow and UI culling. GPose and cutscenes are handled by a setting "Show screens in GPose" (`UiBuilder.DisableGposeUiHide`) and "Hide during cutscenes".

**Visibility-based throttling:** if no world screen is on screen and the viewer window is closed or collapsed, send `SetFrameRate(1)`. Audio keeps playing. At more than 40 yalms from the screen, drop to 15 fps.

### 8.4 SpatialAudioController

Every 50 ms on `IFramework.Update`: if the active screen is a world screen, compute the listener position (camera/player lerp, as in `MediaManager.GetListeningPosition`), then `pan = clamp(AngleDir(camFwd, dir, camUp))` and `gain = masterVolume × (clamp((maxDist − d) / maxDist))²`. Defaults: `maxDist` 40 yalms (XivMediaPlayer uses 100, which is very generous indoors), configurable 10–100. Viewer-window-only playback is flat: pan 0, gain = master volume. `SetAudio` is sent only when a value changes by more than 0.01.

**BgmDucker:** ported mute/restore logic, plus an optional "duck to X%" mode that uses `SystemConfigOption.SoundBgm` instead of full mute. Always restore on unload, zone change, and session close.

**Presenter feedback-loop guard.** If the host screen-shares their whole desktop *with system audio*, the in-game Kosmi audio gets captured and sent back to everyone as an echo. Detect it with the "I'm presenting from this PC" toggle (and suggest it automatically when `RoomInfo.presenter` matches the host's Kosmi name). When on, the in-game audio for that user is muted and the screen still shows. Also document: share a *tab* rather than the whole screen when possible.

### 8.5 LocationService

This is XivMediaPlayer's `GetLocationKey` / `GetCurrentLocationKeys` / `GetDetailedLocationInfo` extracted into a service, with identical string formats. It raises `LocationChanged` on territory change and on housing-manager ward/plot/room change, and caches the local player's world ID and position on the game thread.

---

## 9. Groups

### 9.1 Concept

A **group** = one Kosmi room + FFXIV-side coordination.

| Kosmi already provides | WatchAlong adds |
|---|---|
| Synced playback for all members | In-game invites (party/tell/linkshell text, clickable) |
| Media relay (SFU) | Shared screen anchor, so the TV is in the same place for everyone |
| Member list, chat, room moderation | Group roster with **load status** (Loading / Watching / Needs action / Error) |
| | Host controls: switch Kosmi room (everyone follows), move screen, ready check, kick from *group* |
| | Venue binding (house TV → room), public directory |

Kicking someone from the *group* removes the group's screen and prompts on their client. It does **not** remove them from the Kosmi room. That's Kosmi's moderation, done in control mode or Kosmi itself. The UI must say so plainly.

### 9.2 Roles and permissions

| Action | Host | Co-host | Member |
|---|---|---|---|
| Change Kosmi room, rename, visibility, lock | ✔ | ✔ | |
| Move/resize screen anchor | ✔ | ✔ | |
| Start ready check | ✔ | ✔ | |
| Kick from group, promote/demote co-host | ✔ | | |
| Transfer host, close group | ✔ | | |
| Local-only: volume, hide screen, viewer window, leave | ✔ | ✔ | ✔ |

All enforcement is server-side. The client UI just hides buttons.

### 9.3 Invites

**Phase 3a — serverless (ship first, zero infrastructure).** An invite is a self-contained string:

```
WA1:<base64url(deflate(json))>
json = { "v":1, "r":"sulync", "n":"Movie night",
         "a": { "t":"loc", "k":"house_57_1376_7_27_0_1604...", "p":[x,y,z], "q":[rx,ry,rz], "s":[w,h] } }
```

That's roughly 120–200 characters, well under the 500-byte chat limit. The host clicks **Copy invite** and pastes it into party, tell, linkshell, or even Discord. The plugin **never sends chat messages by itself**; that stays a user action.

`InviteChatDetector` hooks `IChatGui.ChatMessage`, finds `WA1:[A-Za-z0-9_-]+`, decodes and validates it (room code regex, anchor sanity limits), and appends a local clickable **[Join watch-along: Movie night]** link (a Dalamud link payload registered through the chat link handler). Clicking shows a confirmation toast ("Join Kosmi room *sulync*? You'll appear there as '<name>'"). Only after that is anything loaded. `/wa join <invite-or-kosmi-url>` does the same thing.

In 3a, "group presence" is simply the Kosmi member list read by the agent. The anchor is frozen at invite time; moving the screen means re-sharing the invite. That's acceptable for a first release.

**Phase 3b — with the group server.** Invites become short codes, `WA2:K7M2QX9P` (8 characters of Crockford base32, about 40 bits, never reused). The server resolves them, so live anchors, roster, and host tools work. The chat detector recognizes both formats.

### 9.4 Group server (`WatchAlong.Server`)

ASP.NET Core minimal API + SignalR hub `/hub/groups` + SQLite (in-memory dictionaries are fine at first; SQLite only for venues and the directory). It runs in Docker on a small VPS. It stores **no media and no chat**.

**Identity (no accounts):** each install generates `InstallId` (GUID) and `InstallSecret` (32 random bytes) and keeps them in the plugin config. The hub connection sends both; the server stores a hash on first sight (trust-on-first-use). The `CharacterTag` ("Name@World") is **opt-in** and used only for the Actor anchor and roster display.

**Data model:**

```csharp
record Group(
  Guid Id, string Code, string Name, string KosmiRoom,        // normalized room code, validated
  Guid HostId, HashSet<Guid> CoHosts, Visibility Visibility,  // Private | Unlisted | Public
  string? PasswordHash, bool Locked, int MaxMembers,          // default 48
  ScreenAnchorDto? Anchor, string? VenueLocationKey,
  bool Nsfw, DateTime CreatedUtc, DateTime LastActivityUtc);

record Member(
  Guid InstallId, Guid GroupId, string DisplayName, string? CharacterTag,
  Role Role, MemberPhase Phase, string? Detail,               // Loading|Watching|NeedsUser|Error|Away
  DateTime JoinedUtc, DateTime LastSeenUtc, string ConnectionId);
```

**Hub API:**

| Client → Server | Returns / Effect |
|---|---|
| `CreateGroup(CreateGroupRequest)` | `GroupSnapshot` (caller becomes host) |
| `JoinGroup(code, password?)` | `GroupSnapshot` or error (`NotFound`, `Locked`, `Full`, `BadPassword`) |
| `LeaveGroup(groupId)` | broadcast `MemberLeft` |
| `UpdateGroup(groupId, GroupPatch)` | role-checked; broadcast `GroupUpdated` |
| `UpdateAnchor(groupId, ScreenAnchorDto)` | role-checked; server throttles to 4 Hz; broadcast `AnchorUpdated` |
| `SetMemberState(groupId, phase, detail?)` | on change + 30 s heartbeat; broadcast `MemberStateChanged` |
| `StartReadyCheck(groupId, seconds)` | broadcast `ReadyCheck` |
| `RespondReady(groupId, ready)` | broadcast `ReadyResponse` |
| `Kick / Promote / Demote / TransferHost / CloseGroup` | role-checked; broadcasts |

| Server → Client | Client behavior |
|---|---|
| `GroupSnapshot`, `MemberJoined/Left/StateChanged` | Update roster |
| `GroupUpdated` (room changed) | Validate URL → toast "Host switched to *X*" → auto-follow (setting: Follow / Ask) |
| `AnchorUpdated` | Lerp the screen to the new transform over 150 ms |
| `ReadyCheck` | Toast with ✔/✘ plus an automatic status ("Loaded ✔" if `InRoom.Playing` or `NoMedia`) |
| `Kicked`, `GroupClosed(reason)` | Close session, toast |

**Lifetime rules:** a group expires 30 minutes after its last connected member leaves. If the host is disconnected for more than 2 minutes, host passes to the longest-standing co-host, then the longest-standing member (toggle: "keep host seat for 10 min"). Venue groups (§9.6) persist.

**Abuse limits:** 5 group creations per install per hour; 30 hub calls per 10 s per connection; room URL re-validated server-side against the same regex as the client; names 1–40 characters, stripped of control characters; maximum 48 members (tunable).

### 9.5 Key flows

**Create and invite (3b):**

```
Host UI "New watch-along" ──▶ paste Kosmi room link ──▶ KosmiRoomUrl.TryParse ✔
  ──▶ hub.CreateGroup ──▶ snapshot(code=K7M2QX9P) ──▶ KosmiSession.OpenRoom (host joins as guest too)
  ──▶ host places screen (optional) ──▶ hub.UpdateAnchor
  ──▶ "Copy invite" → clipboard "WA2:K7M2QX9P Movie night" → host pastes in /p
```

**Join:**

```
Member sees party chat ──▶ detector adds [Join watch-along: Movie night] ──▶ click ──▶ confirm toast
  ──▶ hub.JoinGroup ──▶ snapshot (room, anchor, roster) ──▶ KosmiSession.OpenRoom
  ──▶ agent: JoinGate → auto-name → InRoom ──▶ SetMemberState(Watching)
  ──▶ anchor resolves here? world screen : viewer window
```

**Host switches room:** `UpdateGroup{kosmiRoom}` → all clients get `GroupUpdated` → each validates and follows (`CloseRoom` + `OpenRoom`). The roster shows everyone's phase go Loading → Watching.

**Server unreachable:** Kosmi playback keeps working (D5). The group enters `Degraded`: roster greyed out, host tools disabled, reconnect with backoff. Nobody's picture stops.

### 9.6 Venue-bound groups and the directory (Phase 4)

A venue owner binds a house `locationKey` to a persistent group (placement rule: furnishing menu open, like XivMediaPlayer; ownership proven the way XivMediaPlayer's claim flow does it, or simply first-claim plus Discord verification later). When a plugin user enters that location:

1. The plugin calls `GET /api/venues/{locationKey}` and receives the group summary and anchor.
2. It draws the screen with an **idle poster** (title, "Watch-along available"), and **nothing is loaded from Kosmi yet**.
3. A toast asks "Join the watch-along here?" Options: Join / Not now / Always for this venue (stored in `TrustedVenues`).

The **directory** tab lists public groups (name, venue or "anywhere", member count, NSFW flag, started time), with the same card layout idea as XivMediaPlayer's `WatchPartyWindow`. NSFW-flagged groups are hidden unless the user enables them in settings.

### 9.7 Chat bridge (Phase 4, optional)

The agent watches Kosmi's chat list through profile selectors and emits `ChatMessage`. The plugin shows messages in the viewer overlay and, optionally, echoes them into the game chat log as a local-only custom message type ("[Kosmi] Ray: lol"). `/wa say <text>` types into Kosmi's chat input through the agent. This is fully dependent on selectors, so it ships behind a toggle and degrades silently.

---

## 10. Configuration

```csharp
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Identity
    public Guid InstallId { get; set; } = Guid.NewGuid();
    public string InstallSecret { get; set; } = RandomSecret();      // never logged
    public string KosmiDisplayName { get; set; } = "";               // required on first join
    public bool UseCharacterNameInKosmi { get; set; } = false;
    public bool ShareCharacterTagWithGroup { get; set; } = false;

    // Playback
    public float MasterVolume { get; set; } = 0.8f;
    public bool SpatialAudio { get; set; } = true;
    public float SpatialMaxDistance { get; set; } = 40f;
    public RoomVoiceMode VoiceMode { get; set; } = RoomVoiceMode.Off;
    public BgmMode Bgm { get; set; } = BgmMode.DuckTo30;
    public bool PresentingFromThisPc { get; set; } = false;
    public AudioBackend AudioBackend { get; set; } = AudioBackend.Auto;  // Wasapi | WaveOut

    // Video
    public QualityPreset Quality { get; set; } = QualityPreset.P720;     // P720 | P1080
    public int MaxFps { get; set; } = 30;
    public FrameTransport Transport { get; set; } = FrameTransport.Auto; // SharedMemory | SharedTexture
    public bool DepthOcclusion { get; set; } = true;
    public bool ShowInGpose { get; set; } = true;

    // Privacy / network
    public bool HideIpFromPeers { get; set; } = false;
    public string GroupServerUrl { get; set; } = "https://watchalong.example";
    public string SelectorProfileUrl { get; set; } = "https://raw.githubusercontent.com/<you>/WatchAlong/main/profiles/kosmi.json";

    // Groups
    public FollowMode FollowHostRoomSwitch { get; set; } = FollowMode.Follow;
    public HashSet<string> TrustedVenues { get; set; } = new();
    public bool ShowNsfwInDirectory { get; set; } = false;
    public List<RecentRoom> RecentRooms { get; set; } = new();          // last 10

    // Local screens (personal placements, not group anchors)
    public Dictionary<string, ScreenTransformDto> PersonalPlacements { get; set; } = new();

    // Debug
    public int CefRemoteDebugPort { get; set; } = 0;                      // 0 = off
    public bool VerboseRendererLog { get; set; } = false;
}
```

---

## 11. UI and commands

**Main window** (`/wa`), tabs:

- **Watch:** current room, page state, mini preview, volume, mute, open viewer, open control mode, "I'm presenting" toggle.
- **Group:** create or join, invite copy, roster with phase dots, host tools (room switch, place screen, ready check, co-hosts, kick, lock, visibility).
- **Browse** (Phase 4): directory cards.
- **Settings:** sections mirroring §10, plus **Diagnostics** (renderer PID, CEF version, codec probe results, fps in/out, frame laps, audio underruns, selector profile version, "Save debug snapshot", "Wipe CEF & re-download").

**Commands:**

| Command | Action |
|---|---|
| `/wa` | Toggle main window |
| `/wa join <WA1…\|WA2…\|kosmi url>` | Join (with confirmation) |
| `/wa leave` | Leave group and close room |
| `/wa view` / `/wa control` | Viewer window / control mode |
| `/wa invite` | Copy invite to clipboard |
| `/wa place` | Place/move screen (host) |
| `/wa vol <0-150>` / `/wa mute` | Volume / mute |
| `/wa ready` | Start ready check (host) or answer one |
| `/wa say <text>` | Send to Kosmi chat (Phase 4) |

---

## 12. Linux / Wine specifics (your setup: CachyOS + XIVLauncher.Core)

- **Runner:** test on XIVLauncher.Core's default Wine and on Proton-GE. Browsingway reports Proton-GE 10.27 working well. Record the known-good matrix in the README.
- **.NET:** the renderer is framework-dependent and uses Dalamud's runtime via `DOTNET_ROOT`. Don't install a separate .NET into the prefix (Browsingway warns this causes crashes).
- **Wine detection:** port `DetectWineRuntime()`. Under Wine, force software rendering and `no-sandbox`, force `FrameTransport.SharedMemory`, and default `AudioBackend` to WASAPI with automatic WaveOut fallback.
- **Fonts:** Kosmi's UI, usernames, and chat will show tofu for CJK and emoji unless the XL prefix has broad-coverage fonts. It's the same fix you already did for Firestorm's prefix (Noto Sans CJK + Noto Color Emoji or equivalent). Put it in the setup docs and have the diagnostics tab warn when `Noto*` is missing from the prefix's `Fonts` directory.
- **Audio path:** Wine's `winepulse` → PipeWire. If you hear crackle, raise the WASAPI latency to 100 ms or switch to WaveOut.
- **After Wine/Proton upgrades:** "Wipe CEF & re-download" (Browsingway's top troubleshooting step).
- **CEF remote debugging** (`CefRemoteDebugPort = 9222`): the Wine process listens on localhost, so native Chromium/Brave on CachyOS can open `http://localhost:9222` and inspect the Kosmi page inside the renderer, including `chrome://webrtc-internals` for codec checks. This is the main Phase 0 tool.
- **Escape hatch (only if CEF-under-Wine fails spike S1):** a native Linux renderer (Linux CEF build) writing frames to a file-backed mapping under `/dev/shm`, opened from the Wine side through the `Z:` drive. It's exotic and would add a second renderer to maintain, so don't plan for it unless forced.

---

## 13. Codec gap and the DirectMedia fallback (Phase 5)

| Kosmi content | Stock CEF | Plan |
|---|---|---|
| YouTube in Kosmi | ✔ (VP9/AV1 + Opus) | Nothing needed |
| Screen share / virtual browser (WebRTC) | ✔ if Kosmi's SFU accepts VP8/VP9 **[VERIFY S2]** | If H.264-only: custom CEF build (below) or rethink |
| Twitch embed | ✘ (H.264 HLS) | DirectMedia, live: no clock needed |
| MP4 link | ✘ | DirectMedia with clock scraping, or tell the host to use YouTube/screen share |
| WebM link | ✔ | Nothing needed |

**DirectMedia mode.** When the agent reports `html5` + `error 4` with a readable `src`, or an iframe whose `src` is Twitch, the plugin plays that URL through a slim port of XivMediaPlayer's LibVLC/yt-dlp stack into the same `FrameReader` interface (an `IFrameSource` abstraction lets the screens consume either shmem frames or VLC frames). The Kosmi page stays open, hidden and muted, as the sync source.

- **Live (Twitch):** play live, no sync.
- **VOD (MP4):** the clock is the hard part. An errored `<video>` doesn't advance `currentTime`, so the page can't simply be read. Options, in order of preference: (a) read Kosmi's own player time label through profile selectors, and drift-correct with XivMediaPlayer's 2.5 s threshold; (b) give up and show the message from §8.1.

**Custom CEF with proprietary codecs.** You can build CEF with `proprietary_codecs=true ffmpeg_branding=Chrome` (CefSharp's maintainer points people to exactly this), but *distributing* such builds brings H.264/AAC patent licensing into play. Acceptable for private testing; for public distribution, get real legal advice first. Treat this as the fallback only if spike S2 shows Kosmi's SFU forces H.264.

---

## 14. Privacy, safety, and platform terms

- **Consent before connection.** Nothing connects to Kosmi until the user clicks Join, whether from an invite, a venue prompt, or the directory. "Always for this venue" is opt-in and per-venue.
- **What others see:** your Kosmi display name (you choose; the character name is opt-in), and your in-game character only if you opt into sharing your CharacterTag with the group.
- **IP exposure:** Kosmi sees your IP like any website. In two-person rooms, streams may be peer-to-peer. "Hide my IP from peers" forces relay-only WebRTC handling.
- **Mic, camera, and screen capture are always denied.** The renderer can't publish anything into Kosmi.
- **Adult content:** groups and venues carry an NSFW flag, the directory hides NSFW by default, and a world screen only renders for people who joined the group. Bystanders with the plugin see only an idle poster at venues, nothing from the room.
- **Kosmi T&C:** read it before Phase 1 (§3.4). If it forbids automated clients, contact Kosmi before going further. Don't strip Kosmi branding beyond what its own fullscreen mode does.
- **Dalamud:** plan for a **custom plugin repo** (`repo.json`, like XivMediaPlayer and Browsingway). A plugin that spawns a Chromium process and joins a third-party service is unlikely to fit the official repository. The plugin never sends chat messages or game actions on its own.
- **AGPL:** public repo, license headers on ported files, source link in the About tab and on the server's landing page.

---

## 15. Performance budget

These are estimates to be replaced by Phase 0/1 measurements on your machine.

| Item | Estimate | Mitigation |
|---|---|---|
| Renderer CPU (720p30, software compositing + WebRTC decode) | ~0.5–1.5 cores | 30 fps cap, 1 fps when hidden, 720p default |
| Frame copies (CEF→shmem→D3D) | ~220 MB/s memcpy at 720p30 | Negligible on modern CPUs; shared-texture path later |
| Renderer RAM | ~300–700 MB | One browser, disk cache capped at 200 MB |
| Game frame-time impact | < 0.5 ms (texture upload + ImGui quad); depth-tested path adds more, to be measured | Upload only on a new `PublishedSeq` |
| Network per viewer | Whatever Kosmi streams (~1.5–4 Mbps at 720p) | Nothing to do; it's Kosmi's SFU |

---

## 16. Testing strategy

- **Unit tests:** `KosmiRoomUrl` (valid/invalid/normalization/lookalike domains such as `app.kosmi.io.evil.tld`), `InviteCodec` (round-trip, truncation, garbage, oversize), spatial math, seqlock reader/writer under a stress test (two threads, a million frames, zero torn frames accepted), `GroupService` permission matrix (server).
- **Renderer harness** (`WatchAlong.Renderer.exe --harness <url> --out <dir>`): runs without the game, logs page and media states, dumps a PNG every 2 s plus WAV audio chunks. **This is how you iterate on Kosmi selectors on Linux** without launching FFXIV.
- **Fake Kosmi fixtures** (`tests/fixtures/fake-kosmi/`): static pages with a name gate, a webcam grid of looping small videos, a primary WebM `<video>`, an MP4 `<video>` (to exercise the codec error path), a YouTube iframe, and a WebRTC loopback page (two `RTCPeerConnection`s in one page streaming a `canvas.captureStream()`). They're served from a local static server so CI can run the agent deterministically. Add a **navigation-policy test** that tries to leave `app.kosmi.io` and must be blocked (the allowlist is temporarily pointed at the fixture host).
- **Manual matrix per release:** {Windows native, CachyOS + XL.Core Wine, CachyOS + Proton-GE} × {YouTube, Chrome tab share, desktop share with audio, virtual browser, WebM link, MP4 link, Twitch} × {viewer window, world screen, depth-tested}. Plus a group test with at least 3 clients (it takes more than two people to trigger Kosmi's SFU relay path).

---

## 17. Roadmap

Sizes are relative (S ≈ a few evenings, M ≈ 1–2 weeks of evenings, L ≈ several weeks).

**Phase 0 — Spikes, go/no-go (M).** Throwaway code only.

| Spike | Question | Pass criteria |
|---|---|---|
| S1 | Does CefSharp.OffScreen in a separate exe run under your XL.Core/Wine setup? | Renders a page to PNG for 30 min without crashing |
| S2 | Can stock CEF decode Kosmi WebRTC? | Share a Chrome tab into a Kosmi room from your real browser; the CEF guest shows moving video. `webrtc-internals` shows the negotiated codec (write it down). Test again with ≥3 members to hit the SFU path. |
| S3 | YouTube inside Kosmi in CEF? | Plays with audio |
| S4 | `IAudioHandler` → NAudio under Wine | Clean audio, underruns < 1/min |
| S5 | Named pipe + named shmem between game-spawned exe and plugin under Wine | 60 Hz messages, 30 fps frames for 30 min |
| S6 | Kosmi DOM discovery | Real selectors for join gate, room states, primary media, webcam tiles, chat, members, fullscreen button. First real `kosmi.json`. |
| S7 | Kosmi T&C read, optional email to Kosmi | No blocking clause, or a plan |

**Go:** S1, S2, S4 pass. **No-go on S2** (H.264-only SFU) means pausing to decide between a custom CEF build and the §12 native-Linux escape hatch before anything else is built.

**Phase 1 — Solo viewer (M).** Renderer with policies, agent, and theater CSS; IPC; FrameReader + ViewerWindow; flat audio; control mode with input; dependency downloader; diagnostics tab. *Exit:* paste a Kosmi link, see and hear it in a window, click through Kosmi's UI in control mode; renderer crash recovers within 5 s without restarting the game.

**Phase 2 — World screens (L).** 2a: LocationService, anchors, ImGui quad renderer, placement gizmo, spatial audio, BGM ducking. 2b: depth-tested renderer port, GPose/cutscene behavior. *Exit:* a TV in your house with correct occlusion and panning; walking away fades audio.

**Phase 3 — Groups (M + M).** 3a: `WA1` invites, chat detector, join confirmation, roster from Kosmi's member list. 3b: server + hub, `WA2` codes, live anchors, roles, room follow, ready check, degraded mode. *Exit:* 3+ players on different worlds join from a party-chat link; the host moves the screen and switches rooms, and everyone follows.

**Phase 4 — Venues and social (M).** Venue binding + idle poster + prompt, trusted venues, directory with NSFW flag, optional chat bridge.

**Phase 5 — Hardening (M–L).** DirectMedia fallback (Twitch first), shared-texture fast path on Windows, profile hot-update pipeline, localization, public custom repo.

---

## 18. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Kosmi UI changes break selectors | High (it will happen) | High | Remote data-only profile, UV-crop fallback when CSS fails, debug snapshots, harness to re-discover quickly |
| Kosmi SFU requires H.264 | Unknown | High | Spike S2 first; custom CEF build (licensing caveat) |
| Kosmi T&C forbids automated/embedded clients, or Kosmi blocks the UA | Unknown | High | Read terms, contact Kosmi, honest UA |
| CEF instability under Wine | Medium | High | Out-of-process + watchdog; Proton-GE; software rendering; Browsingway precedent |
| Presenter audio feedback loop | Medium | Medium | "I'm presenting" toggle + presenter detection; recommend tab sharing |
| Voice chat not separable from media audio | Medium | Low | Offer only "voice on / all muted" |
| Kosmi room member caps or rate limits with many in-game guests | Unknown | Medium | Test with 10+ clients in Phase 3; `MaxMembers` on groups |
| Google OAuth blocked in embedded browser | High | Low | Guest mode default; document other login methods |
| Porting DepthTestedRenderer drags in hidden `Plugin.cs` coupling | Medium | Medium | Ship 2a (ImGui quad) first; port 2b as a unit behind an interface |
| AGPL obligations missed | Low | Medium | Public repo from day one, headers on ported files |
| Server cost/abuse | Low | Low | Phase 3a needs no server; rate limits; no media through it |

---

## 19. Open decisions (with recommended defaults)

1. **New plugin or contribute a "Kosmi source" upstream to XivMediaPlayer?** Recommended: a separate plugin (different architecture: out-of-process CEF, no VLC). It's still worth messaging Sebane1 — they may prefer to collaborate, and you'll share bug fixes in the ported renderer.
2. **Default group scope:** invite-code groups, cross-world. An optional "offer to my party" button is fine, but no automatic party-wide joining.
3. **Server hosting:** postpone with 3a; when needed, a single small VPS in Docker.
4. **Kosmi display name default:** user-chosen nickname, character name opt-in.
5. **Auto-follow host room switches:** on by default for group members (they already consented to the group); "Ask me" option available.
6. **Default spatial max distance:** 40 yalms (XivMediaPlayer's 100 is very large indoors).

---

## Appendix A — Room URL validation

```csharp
public static bool TryParse(string input, out string roomPath)
{
    roomPath = "";
    if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u)) return false;
    if (u.Scheme != Uri.UriSchemeHttps) return false;
    if (!u.Host.Equals("app.kosmi.io", StringComparison.OrdinalIgnoreCase)) return false; // exact host only
    var m = Regex.Match(u.AbsolutePath, @"^/room/(@?[A-Za-z0-9_\-]{2,64})/?$");        // [VERIFY] code charset
    if (!m.Success) return false;
    roomPath = m.Groups[1].Value;                   // store the code, rebuild the URL ourselves
    return true;
}
public static string ToUrl(string roomPath) => $"https://app.kosmi.io/room/{roomPath}";
```

Always store the **code**, never the raw URL, and rebuild the URL from it. That kills query-string and fragment tricks.

## Appendix B — IPC message examples

```json
{ "t": "OpenRoom", "roomUrl": "https://app.kosmi.io/room/sulync", "displayName": "Ray (in-game)",
  "viewport": { "w": 1280, "h": 720 }, "fps": 30, "voiceMode": "Off", "hideIp": false }

{ "t": "SetAudio", "volume": 0.62, "pan": -0.31, "muted": false }

{ "t": "Input", "mouseButton": { "x": 640, "y": 402, "btn": "Left", "up": false, "clicks": 1 } }

{ "t": "MediaState", "kind": "webrtc", "w": 1280, "h": 720, "paused": false,
  "currentTime": null, "src": null, "error": null, "rect": [0, 0, 1280, 720] }

{ "t": "Error", "code": "NavigationBlocked", "message": "Blocked top-level navigation to https://example.com" }
```

## Appendix C — Porting checklist from XivMediaPlayer (`1c3baac`)

| Source file | Destination | Changes while porting |
|---|---|---|
| `XivMediaPlayer/Windows/Direct3D11VideoTexture.cs` | `Screens/Ported/` | Copy from `IntPtr` + stride (no `byte[]`), content UV, handle `DXGI_ERROR_DEVICE_REMOVED` |
| `XivMediaPlayer/Compositing/WorldVideoRenderer.cs` | `Screens/Ported/` | Take `IFrameSource` instead of reading `MediaManager` |
| `XivMediaPlayer/Compositing/DepthTestedRenderer.cs` (+ Glow, UILayerCapture, VignetteExtractor, ShaderCompileHelper) | `Screens/Ported/` | Phase 2b, as one unit |
| `XivMediaPlayer/Compositing/PlacementManipulator.cs`, `MathUtils.cs`, `MediaPlayerCore/Compositing/WorldScreenTransform.cs` | `Screens/Ported/` | Remove direct `Plugin` references |
| `Plugin.cs` → `GetLocationKey`, `GetCurrentLocationKeys`, `GetDetailedLocationInfo` | `Screens/LocationService.cs` | Same key formats |
| `Plugin.cs` → `MuteBgm` / `RestoreBgm` | `Audio/BgmDucker.cs` | Add duck-to-percent mode |
| `MediaPlayerCore/MediaManager.cs` → `GetListeningPosition`, `AngleDir`, `CalculateObjectVolume` | `Audio/SpatialAudioController.cs` | Configurable max distance |
| `MediaPlayerCore/StreamProxy.cs` → `DetectWineRuntime` | `Core/WineDetect.cs` | — |
| `MediaPlayerCore/Resolvers/CefSharpResolver.cs` → loader workarounds, `DOTNET_ROOT` | `WatchAlong.Renderer/CefBootstrap.cs` + `RendererHost` | **Drop** `disable-web-security`, `allow-running-insecure-content`, `mute-audio` |
| `XivMediaPlayer/DependencyManager.cs` | `Core/DependencyManager.cs` | CEF only (no VLC/ffmpeg until Phase 5) |

## Appendix D — References

- XivMediaPlayer: https://github.com/Sebane1/XivMediaPlayer (AGPL-3.0), including `docs/media-timecode-sync.md`
- Browsingway: https://github.com/Styr1x/Browsingway
- CefSharp releases (codec notes): https://github.com/cefsharp/CefSharp/releases
- CefSharp discussion on building CEF with proprietary codecs: https://github.com/cefsharp/CefSharp/discussions/5090
- Kosmi screen share (SFU relay, 720p/1080p): https://kosmi.io/screen-share-with-friends/
- Kosmi virtual browser: https://kosmi.io/hyperbeam-alternative/
- Kosmi T&C (read manually): linked from the footer of https://kosmi.io/
