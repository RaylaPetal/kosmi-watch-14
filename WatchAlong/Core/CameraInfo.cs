using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace WatchAlong.Core;

/// <summary>Tiny unsafe reads of the game's active camera, kept separate from the pure spatial-audio math (design.md D3), matching <see cref="GameDevice"/>'s existing pattern.</summary>
public static unsafe class CameraInfo
{
    public static (Vector3 Position, Vector3 Forward) GetCamera()
    {
        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera is null)
            return (Vector3.Zero, new Vector3(0, 0, 1));

        var scene = camera->SceneCamera;
        // LookAtVector is the world-space point the camera orbits/targets (not a pre-normalized
        // direction). Validated in-game: a CPU-side depth-occlusion test using this Forward
        // (dot product against world geometry) correctly told "in front of the camera" from
        // "behind it", so this reading is correct — unlike GetCameraBasis() below, which needs a
        // separate, more precise source for the depth-tested renderer's per-pixel ray casting.
        return (scene.Position, Vector3.Normalize(scene.LookAtVector - scene.Position));
    }

    public static Vector3 GetPlayerPosition(IObjectTable objectTable) => objectTable.LocalPlayer?.Position ?? Vector3.Zero;

    /// <summary>
    /// Full basis for the depth-tested renderer (Screens/Ported/DepthTested), which needs an
    /// orthogonal Right/Up plus exact FoV/aspect/near/far — not just a forward direction. Ported
    /// directly from XivMediaPlayer's own Plugin.cs (the code that actually drives its working
    /// DepthTestedRenderer, not a reconstruction): invert the render camera's own view matrix and
    /// read Right/Up/Forward off its rows, and read FoV/AspectRatio/NearPlane/FarPlane straight
    /// from the render camera instead of guessing constants. `CameraForward` comes out already in
    /// the "backwards" convention the ported shader's `-fovDist * CameraForward` term expects
    /// (see that file's header comment) — do not negate it again here.
    /// </summary>
    public static bool TryGetCameraBasis(out Vector3 position, out Vector3 cameraForward, out Vector3 right, out Vector3 up, out float fovY, out float aspectRatio, out float nearPlane, out float farPlane)
    {
        position = Vector3.Zero;
        cameraForward = new Vector3(0, 0, -1);
        right = Vector3.UnitX;
        up = Vector3.UnitY;
        fovY = 0.785f;
        aspectRatio = 1.0f;
        nearPlane = 0.1f;
        farPlane = 10000f;

        var camera = CameraManager.Instance()->GetActiveCamera();
        if (camera is null)
            return false;

        var sceneCamera = camera->CameraBase.SceneCamera;
        if (sceneCamera.RenderCamera is null)
            return false;

        var renderCamera = sceneCamera.RenderCamera;
        var rawView = renderCamera->ViewMatrix;
        var view = System.Runtime.CompilerServices.Unsafe.As<FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4, Matrix4x4>(ref rawView);

        // FFXIV matrices often leave the 4th row/column uninitialized or zeroed — Invert()
        // requires a proper affine matrix (M44 = 1) or it silently returns garbage.
        view.M14 = 0f;
        view.M24 = 0f;
        view.M34 = 0f;
        view.M44 = 1f;

        if (!Matrix4x4.Invert(view, out var invView))
            return false;

        position = invView.Translation;
        right = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        up = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        cameraForward = Vector3.Normalize(new Vector3(invView.M31, invView.M32, invView.M33));

        fovY = renderCamera->FoV;
        aspectRatio = renderCamera->AspectRatio;
        nearPlane = renderCamera->NearPlane;
        farPlane = renderCamera->FarPlane;
        return true;
    }
}
