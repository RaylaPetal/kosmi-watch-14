using System.Text.Json.Serialization;

namespace WatchAlong.Shared.Ipc;

/// <summary>
/// Base type for every message on the control channel (see
/// specs/ipc-protocol/spec.md "Control channel delivers commands and state events").
/// Wire shape is a JSON object with a "t" discriminator, matching design.md Appendix B,
/// e.g. { "t": "OpenRoom", "roomUrl": "...", ... }.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "Hello")]
[JsonDerivedType(typeof(HelloAckMessage), "HelloAck")]
[JsonDerivedType(typeof(OpenRoomMessage), "OpenRoom")]
[JsonDerivedType(typeof(CloseRoomMessage), "CloseRoom")]
[JsonDerivedType(typeof(SetViewModeMessage), "SetViewMode")]
[JsonDerivedType(typeof(SetViewportMessage), "SetViewport")]
[JsonDerivedType(typeof(SetFrameRateMessage), "SetFrameRate")]
[JsonDerivedType(typeof(SetAudioMessage), "SetAudio")]
[JsonDerivedType(typeof(SetVoiceModeMessage), "SetVoiceMode")]
[JsonDerivedType(typeof(InputMessage), "Input")]
[JsonDerivedType(typeof(SetProfileMessage), "SetProfile")]
[JsonDerivedType(typeof(SetAnchorMessage), "SetAnchor")]
[JsonDerivedType(typeof(ClearAnchorMessage), "ClearAnchor")]
[JsonDerivedType(typeof(SetRenderModeMessage), "SetRenderMode")]
[JsonDerivedType(typeof(ReloadMessage), "Reload")]
[JsonDerivedType(typeof(DebugSnapshotMessage), "DebugSnapshot")]
[JsonDerivedType(typeof(ShutdownMessage), "Shutdown")]
[JsonDerivedType(typeof(FrameRingInfoMessage), "FrameRingInfo")]
[JsonDerivedType(typeof(PageStateMessage), "PageState")]
[JsonDerivedType(typeof(MediaStateMessage), "MediaState")]
[JsonDerivedType(typeof(RoomInfoMessage), "RoomInfo")]
[JsonDerivedType(typeof(AudioStatsMessage), "AudioStats")]
[JsonDerivedType(typeof(ErrorMessage), "Error")]
[JsonDerivedType(typeof(LogMessage), "Log")]
public abstract record IpcMessage;

public enum ViewMode { Theater, Full }

public enum VoiceMode { Off, Flat }

public enum InputKind { MouseMove, MouseButton, Wheel, Key, Text }

/// <summary>Non-occluded world-space quad (Phase 2a) vs. depth-tested, occluded rendering (Phase 2b).</summary>
public enum ScreenRenderMode { Quad, DepthTested }

public sealed record CodecSupport(bool H264, bool Aac, bool Vp9, bool Av1);

public sealed record ViewportSize(int W, int H);

// ---- Plugin -> Renderer ----

public sealed record HelloAckMessage(int Protocol, string LogLevel, bool IsWine) : IpcMessage;

public sealed record OpenRoomMessage(
    string RoomUrl,
    string DisplayName,
    ViewportSize Viewport,
    int Fps,
    VoiceMode VoiceMode,
    bool HideIp) : IpcMessage;

public sealed record CloseRoomMessage : IpcMessage;

public sealed record SetViewModeMessage(ViewMode Mode) : IpcMessage;

public sealed record SetViewportMessage(int W, int H) : IpcMessage;

public sealed record SetFrameRateMessage(int Fps) : IpcMessage;

/// <summary>
/// Volume/mute plus a stereo pan (-1..1), reserved-but-unused in Phase 1 (design.md D2) and
/// populated by <c>SpatialAudioController</c> once a screen has a world anchor (Phase 2 design.md D4).
/// </summary>
public sealed record SetAudioMessage(double Volume, bool Muted, double Pan = 0.0) : IpcMessage;

/// <summary>Sets or replaces a placed screen's world transform (Phase 2 design.md D4).</summary>
public sealed record SetAnchorMessage(
    double PosX,
    double PosY,
    double PosZ,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees,
    double Width,
    double Height) : IpcMessage;

/// <summary>Removes a placed screen's world transform; the session's audio and rendering fall back to flat/viewer-window behavior.</summary>
public sealed record ClearAnchorMessage : IpcMessage;

/// <summary>Switches a placed screen between the non-occluded quad and the depth-tested renderer.</summary>
public sealed record SetRenderModeMessage(ScreenRenderMode Mode) : IpcMessage;

public sealed record SetVoiceModeMessage(VoiceMode Mode) : IpcMessage;

/// <summary>
/// Flattened union of the five input variants (mouseMove/mouseButton/wheel/key/text) from
/// design.md §7 — one message shape keyed by <see cref="Kind"/> rather than a nested
/// polymorphic type, since the payloads are small and the rate is low (&lt;=50/s).
/// </summary>
public sealed record InputMessage(
    InputKind Kind,
    double X = 0,
    double Y = 0,
    string? Button = null,
    bool Up = false,
    int Clicks = 0,
    double Dx = 0,
    double Dy = 0,
    int Vk = 0,
    int Mods = 0,
    string? Text = null) : IpcMessage;

/// <summary>Carries the full selector-profile JSON, already schema-validated by the plugin (see SelectorProfile).</summary>
public sealed record SetProfileMessage(string ProfileJson) : IpcMessage;

public sealed record ReloadMessage : IpcMessage;

public sealed record DebugSnapshotMessage : IpcMessage;

public sealed record ShutdownMessage : IpcMessage;

// ---- Renderer -> Plugin ----

public sealed record HelloMessage(int Protocol, string CefVersion, string ChromiumVersion, CodecSupport Codecs) : IpcMessage;

public sealed record FrameRingInfoMessage(string MapName, int Slots, int MaxWidth, int MaxHeight) : IpcMessage;

public sealed record PageStateMessage(string State, string? Detail) : IpcMessage;

public sealed record MediaStateMessage(
    string Kind,
    int W,
    int H,
    bool Paused,
    double? CurrentTime,
    string? Src,
    int? Error,
    int[] Rect) : IpcMessage;

public sealed record RoomInfoMessage(string Title, string[] Members, string? Presenter) : IpcMessage;

public sealed record AudioStatsMessage(int Underruns, double LatencyMs) : IpcMessage;

public sealed record ErrorMessage(string Code, string Message) : IpcMessage;

public sealed record LogMessage(string Level, string Msg) : IpcMessage;
