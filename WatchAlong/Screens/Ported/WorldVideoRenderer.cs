// Ported from Sebane1/XivMediaPlayer (AGPL-3.0) — XivMediaPlayer/Compositing/WorldVideoRenderer.
// The ImGui screen-space path (RenderScreenSpace/WorldToScreenClamped) is Phase 2a: no depth
// testing, no glow/vignette/screensaver/loading-overlay state — those are VLC-player-specific or
// belong to the Phase 2b depth-tested capability. The depth-tested path (RenderWithOcclusion)
// wires the ported Screens/Ported/DepthTested/ unit (design.md Appendix C: "port as one unit").
// An earlier attempt at this path used a hand-reconstructed camera basis and rendered fully
// transparent; CameraInfo.TryGetCameraBasis() now derives it the same way XivMediaPlayer's own
// working code does (invert the render camera's view matrix), which this depends on.
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using WatchAlong.Core;
using WatchAlong.Screens.Ported.DepthTested;
using WatchAlong.Shared.Frames;
using WatchAlong.Shared.Ipc;
using WatchAlong.Shared.Screens;

namespace WatchAlong.Screens.Ported;

/// <summary>
/// Draws a placed screen's current video texture as a 3D object in world space: either a
/// non-occluded ImGui quad (Phase 2a, world-screens spec) or, once switched via
/// <see cref="ScreenRenderMode.DepthTested"/>, a per-pixel depth-occluded composite that also
/// draws game UI back over the screen (Phase 2b, depth-tested-rendering spec).
/// </summary>
public sealed class WorldVideoRenderer(IGameGui gameGui) : IDisposable
{
    private const int QuadGridSubdivisions = 8;

    private DepthBufferCapture? _depthCapture;
    private UILayerCapture? _uiCapture;
    private DepthTestedRenderer? _depthRenderer;
    private GlowRenderer? _glowRenderer;
    private string? _lastInitError;

    /// <summary>Non-null when the depth-tested path failed to initialize (e.g. shader compile failure) — the caller should fall back to the quad and can surface this for diagnostics.</summary>
    public string? DepthRendererError => _lastInitError;

    /// <summary>
    /// Call once per frame, before any ImGui rendering (matches the ported
    /// <c>DepthBufferCapture.BeginFrame</c>/<c>UILayerCapture.CaptureFrame</c> contract). A no-op
    /// in <see cref="ScreenRenderMode.Quad"/> mode — the CPU/GPU cost of depth+UI capture is only
    /// paid when a screen is actually depth-tested.
    /// </summary>
    public void BeginFrame(ScreenRenderMode mode)
    {
        if (mode != ScreenRenderMode.DepthTested)
            return;

        EnsureDepthTestedResources();
        if (_depthCapture is { } depth)
        {
            depth.ReadDepthEnabled = true;
            depth.BeginFrame();
        }
        _uiCapture?.CaptureFrame();
    }

    /// <summary>
    /// Renders <paramref name="transform"/>'s quad textured with <paramref name="textureSrv"/>
    /// (a <see cref="Direct3D11VideoTexture"/>'s SRV) cropped to <paramref name="contentUv"/>.
    /// </summary>
    public void Render(ScreenTransform transform, nint textureSrv, ContentUv contentUv, ScreenRenderMode mode)
    {
        if (textureSrv == nint.Zero)
            return;

        if (mode == ScreenRenderMode.DepthTested && RenderWithOcclusion(transform, textureSrv, contentUv))
            return;

        RenderScreenSpace(transform, textureSrv, contentUv);
    }

    /// <summary>No-active-media placeholder (world-screens spec "No active content on a placed screen"): a dim panel with a waiting message instead of the last frame or a blank quad.</summary>
    public void RenderPlaceholder(ScreenTransform transform, string message)
    {
        var (tl, tr, br, bl) = transform.Corners;

        if (!gameGui.WorldToScreen(tl, out var sTl) ||
            !gameGui.WorldToScreen(tr, out var sTr) ||
            !gameGui.WorldToScreen(br, out var sBr) ||
            !gameGui.WorldToScreen(bl, out var sBl))
            return;

        var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        drawList.AddQuadFilled(sTl, sTr, sBr, sBl, 0xCC1A1A1A);

        var center = (sTl + sTr + sBr + sBl) / 4f;
        var textSize = ImGui.CalcTextSize(message);
        drawList.AddText(center - textSize / 2f, 0xFFFFFFFF, message);
    }

    /// <summary>
    /// Non-occluded quad, subdivided into a grid rather than one <c>AddImageQuad</c> call: ImGui
    /// splits a quad into two triangles and interpolates UVs affinely (not perspective-correct)
    /// per triangle, which shows up as visible warping/twisting along the diagonal seam at a
    /// steep viewing angle. Subdividing shrinks the perspective range (and so the error) each
    /// individual affine interpolation covers, hiding the artifact almost entirely. Does nothing
    /// if any corner can't be projected to screen space (behind the camera).
    /// </summary>
    private void RenderScreenSpace(ScreenTransform transform, nint textureSrv, ContentUv contentUv)
    {
        var (tl, tr, br, bl) = transform.Corners;

        if (!gameGui.WorldToScreen(tl, out _) ||
            !gameGui.WorldToScreen(tr, out _) ||
            !gameGui.WorldToScreen(br, out _) ||
            !gameGui.WorldToScreen(bl, out _))
            return;

        var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
        var textureId = new ImTextureID(textureSrv);

        const int grid = QuadGridSubdivisions;
        for (var row = 0; row < grid; row++)
        {
            var v0 = (float)row / grid;
            var v1 = (float)(row + 1) / grid;
            for (var col = 0; col < grid; col++)
            {
                var u0 = (float)col / grid;
                var u1 = (float)(col + 1) / grid;

                var cellTl = Bilerp(tl, tr, bl, br, u0, v0);
                var cellTr = Bilerp(tl, tr, bl, br, u1, v0);
                var cellBr = Bilerp(tl, tr, bl, br, u1, v1);
                var cellBl = Bilerp(tl, tr, bl, br, u0, v1);

                if (!gameGui.WorldToScreen(cellTl, out var sTl) ||
                    !gameGui.WorldToScreen(cellTr, out var sTr) ||
                    !gameGui.WorldToScreen(cellBr, out var sBr) ||
                    !gameGui.WorldToScreen(cellBl, out var sBl))
                    continue;

                var uvU0 = contentUv.MinU + (contentUv.MaxU - contentUv.MinU) * u0;
                var uvU1 = contentUv.MinU + (contentUv.MaxU - contentUv.MinU) * u1;
                var uvV0 = contentUv.MinV + (contentUv.MaxV - contentUv.MinV) * v0;
                var uvV1 = contentUv.MinV + (contentUv.MaxV - contentUv.MinV) * v1;

                drawList.AddImageQuad(
                    textureId,
                    sTl, sTr, sBr, sBl,
                    new Vector2(uvU0, uvV0),
                    new Vector2(uvU1, uvV0),
                    new Vector2(uvU1, uvV1),
                    new Vector2(uvU0, uvV1),
                    0xFFFFFFFF);
            }
        }
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

    /// <returns>False (caller should fall back to the non-occluded quad) if the camera basis, depth data, the video SRV, or the depth-tested renderer aren't ready this frame.</returns>
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

    private static Vector3 Bilerp(Vector3 topLeft, Vector3 topRight, Vector3 bottomLeft, Vector3 bottomRight, float u, float v)
    {
        var top = Vector3.Lerp(topLeft, topRight, u);
        var bottom = Vector3.Lerp(bottomLeft, bottomRight, u);
        return Vector3.Lerp(top, bottom, v);
    }

    public void Dispose()
    {
        _depthRenderer?.Dispose();
        _glowRenderer?.Dispose();
        _uiCapture?.Dispose();
        _depthCapture?.Dispose();
    }
}
