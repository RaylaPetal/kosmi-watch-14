// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — XivMediaPlayer/Compositing/WorldVideoRenderer.
// The depth-tested path (RenderWithOcclusion) wires the ported Screens/Ported/DepthTested/ unit
// (design.md Appendix C: "port as one unit"). An earlier attempt at this path used a
// hand-reconstructed camera basis and rendered fully transparent; CameraInfo.TryGetCameraBasis()
// now derives it the same way XivMediaPlayer's own working code does (invert the render camera's
// view matrix), which this depends on. The original non-occluded ImGui-quad path (Phase 2a) has
// been removed now that depth-tested rendering is verified against a live game and always used.
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using WatchAlong.Core;
using WatchAlong.Screens.Ported.DepthTested;
using WatchAlong.Shared.Frames;
using WatchAlong.Shared.Screens;

namespace WatchAlong.Screens.Ported;

/// <summary>
/// Draws a placed screen's current video texture as a 3D object in world space: a per-pixel
/// depth-occluded composite that also draws game UI back over the screen
/// (depth-tested-rendering spec).
/// </summary>
public sealed class WorldVideoRenderer(IGameGui gameGui) : IDisposable
{
    private DepthBufferCapture? _depthCapture;
    private UILayerCapture? _uiCapture;
    private DepthTestedRenderer? _depthRenderer;
    private GlowRenderer? _glowRenderer;
    private string? _lastInitError;

    private Direct3D11VideoTexture? _placeholderTexture;
    private string? _placeholderMessage;

    /// <summary>Non-null when the depth-tested path failed to initialize (e.g. shader compile failure) or a frame's render call didn't produce output — surfaced for diagnostics; the screen simply doesn't draw that frame.</summary>
    public string? DepthRendererError => _lastInitError;

    /// <summary>Call once per frame, before any ImGui rendering (matches the ported <c>DepthBufferCapture.BeginFrame</c>/<c>UILayerCapture.CaptureFrame</c> contract).</summary>
    public void BeginFrame()
    {
        EnsureDepthTestedResources();
        if (_depthCapture is { } depth)
        {
            depth.ReadDepthEnabled = true;
            depth.BeginFrame();
        }
        _uiCapture?.CaptureFrame();
    }

    /// <summary>
    /// Renders <paramref name="transform"/>'s screen, depth-occluded, textured with
    /// <paramref name="textureSrv"/> (a <see cref="Direct3D11VideoTexture"/>'s SRV) cropped to
    /// <paramref name="contentUv"/>. Does nothing this frame if the depth-tested renderer, camera
    /// basis, or captured depth/UI data aren't ready yet (see <see cref="DepthRendererError"/>).
    /// </summary>
    public void Render(ScreenTransform transform, nint textureSrv, ContentUv contentUv)
    {
        if (textureSrv == nint.Zero)
            return;

        RenderWithOcclusion(transform, textureSrv, contentUv);
    }

    /// <summary>
    /// No-active-media placeholder (world-screens spec "the waiting placeholder is depth-occluded
    /// like active video"): a dim panel with a waiting message, composited through the same
    /// per-pixel depth-occluded path as active video (<see cref="RenderWithOcclusion"/>) instead of
    /// an unoccluded 2D overlay — so it's correctly hidden behind nearer world geometry and
    /// characters, exactly like a video frame at the same placement would be. Does nothing this
    /// frame if the depth-tested renderer, camera basis, or captured depth/UI data aren't ready yet,
    /// same as <see cref="Render"/> (see <see cref="DepthRendererError"/>).
    /// </summary>
    public void RenderPlaceholder(ScreenTransform transform, string message)
    {
        if (!EnsurePlaceholderTexture(message))
            return;

        RenderWithOcclusion(transform, _placeholderTexture!.ShaderResourceView, ContentUv.FullFrame);
    }

    /// <returns>False if the placeholder texture isn't available yet (device not ready this frame, or the rasterize/upload failed).</returns>
    private bool EnsurePlaceholderTexture(string message)
    {
        if (_placeholderTexture is null)
        {
            var (device, context) = GameDevice.GetD3D11DeviceAndContext();
            _placeholderTexture = new Direct3D11VideoTexture(device, context);
        }

        // Only re-rasterize and re-upload when the message actually changed, or the texture was
        // dropped (e.g. after a device-removed/reset) — not on every frame the placeholder is shown.
        if (_placeholderTexture.HasTexture && _placeholderMessage == message)
            return true;

        var pixels = PlaceholderPanelRasterizer.Rasterize(message);
        const int stride = PlaceholderPanelRasterizer.Width * 4;
        if (!_placeholderTexture.Upload(pixels, PlaceholderPanelRasterizer.Width, PlaceholderPanelRasterizer.Height, stride, ContentUv.FullFrame))
            return false;

        _placeholderMessage = message;
        return true;
    }

    private void EnsureDepthTestedResources()
    {
        _depthCapture ??= new DepthBufferCapture();
        if (!_depthCapture.IsInitialized)
            _depthCapture.Initialize();

        _uiCapture ??= new UILayerCapture();
        if (!_uiCapture.IsInitialized)
            _uiCapture.Initialize();

        _glowRenderer ??= new GlowRenderer();
        if (!_glowRenderer.IsInitialized)
            _glowRenderer.Initialize();

        if (_depthRenderer is null)
        {
            _depthRenderer = new DepthTestedRenderer();
            if (!_depthRenderer.Initialize())
                _lastInitError = _depthRenderer.InitError ?? "DepthTestedRenderer initialization failed.";
            else
                _lastInitError = null;
        }
    }

    /// <summary>Shared by <see cref="Render"/> and <see cref="RenderPlaceholder"/> — both active video and the waiting placeholder are composited through this same per-pixel depth-occluded path.</summary>
    /// <returns>False (nothing is drawn this frame) if the camera basis, depth data, the texture SRV, or the depth-tested renderer aren't ready this frame.</returns>
    private bool RenderWithOcclusion(ScreenTransform transform, nint textureSrv, ContentUv contentUv)
    {
        if (_depthRenderer is not { IsInitialized: true } depthRenderer || _depthCapture is not { } depthCapture || depthCapture.CapturedSRV is null)
            return false;

        if (!CameraInfo.TryGetCameraBasis(out var cameraPos, out var cameraForward, out var cameraRight, out var cameraUp, out var fovY, out var aspectRatio, out var nearPlane, out var farPlane))
        {
            _lastInitError = "camera basis unavailable this frame (render camera not ready)";
            return false;
        }

        var (tl, tr, br, bl) = transform.Corners;

        // Cull entirely-behind-camera placements — cameraForward is in the shader's "backwards"
        // convention (see CameraInfo.TryGetCameraBasis), hence the negation here to get the true
        // view direction for this front/behind test.
        float ZOf(Vector3 corner) => Vector3.Dot(corner - cameraPos, -cameraForward);
        if (ZOf(tl) <= 0.1f && ZOf(tr) <= 0.1f && ZOf(br) <= 0.1f && ZOf(bl) <= 0.1f)
            return false;

        if (!gameGui.WorldToScreen(tl, out var sTl) ||
            !gameGui.WorldToScreen(tr, out var sTr) ||
            !gameGui.WorldToScreen(br, out var sBr) ||
            !gameGui.WorldToScreen(bl, out var sBl))
            return false;

        float DepthOf(Vector3 corner)
        {
            var viewZ = Vector3.Dot(corner - cameraPos, -cameraForward);
            if (viewZ <= 0f) return 0f;
            var d = nearPlane * (farPlane - viewZ) / (viewZ * (farPlane - nearPlane));
            return Math.Clamp(d, 0f, 1f);
        }

        var cornerDepths = new Vector4(DepthOf(tl), DepthOf(tr), DepthOf(br), DepthOf(bl));

        var viewport = ImGui.GetMainViewport();
        var screenW = (int)viewport.Size.X;
        var screenH = (int)viewport.Size.Y;
        var localTl = sTl - viewport.Pos;
        var localTr = sTr - viewport.Pos;
        var localBr = sBr - viewport.Pos;
        var localBl = sBl - viewport.Pos;

        depthCapture.GetMinMaxDepth(out var minDepth, out var maxDepth);

        // The shader's pillarbox/letterbox correction (DepthTestedRenderer.cs PS(), "if (VideoAspectRatio > 0)")
        // compares this against the TV mesh's own aspect ratio and rescales sampleUV to compensate —
        // it needs the video CONTENT's true pixel aspect ratio, not the UV-fraction span ratio
        // ((MaxU-MinU)/(MaxV-MinV)), which is only numerically equal to the true aspect ratio when
        // the underlying texture happens to be square. Using the UV-fraction ratio fed a wrong value
        // in here, which the shader read as "the video is a different shape than the TV", triggering
        // an unwanted zoom/crop. Kosmi's renderer always outputs 1920x1080 (see RendererApp) — hardcode
        // that rather than trying to plumb the real pixel dimensions through for one ratio.
        const float contentAspect = 1920f / 1080f;

        var success = depthRenderer.Render(
            (localTl, localTr, localBr, localBl),
            (tl, tr, br, bl),
            cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio,
            textureSrv,
            depthCapture.CapturedSRV,
            cornerDepths,
            nearPlane, farPlane,
            screenW, screenH,
            _uiCapture?.BackBufferSRV,
            hoverUV: null, progress: 0f, bufferProgress: 1f, playbackState: 0f, lockState: -1f,
            minDepth, maxDepth, volume: 0f,
            depthCapture.RenderWidth, depthCapture.RenderHeight,
            _uiCapture?.LastAddonRects,
            videoAspectRatio: contentAspect,
            uvBottom: contentUv.MaxV, uvRight: contentUv.MaxU, uvTop: contentUv.MinV, uvLeft: contentUv.MinU);

        if (!success || depthRenderer.OutputSRV is not { } outputSrv)
        {
            _lastInitError = "depth-tested draw call did not produce an output texture";
            return false;
        }

        var drawList = ImGui.GetBackgroundDrawList(viewport);
        var outputId = new ImTextureID(outputSrv.NativePointer);
        drawList.AddImage(outputId, viewport.Pos, viewport.Pos + viewport.Size);
        return true;
    }

    public void Dispose()
    {
        _depthRenderer?.Dispose();
        _glowRenderer?.Dispose();
        _uiCapture?.Dispose();
        _depthCapture?.Dispose();
        _placeholderTexture?.Dispose();
    }
}
