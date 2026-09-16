using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using WatchAlong.Core;
using WatchAlong.Shared.Screens;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace WatchAlong.Camera;

/// <summary>
/// "Focus on screen" (screen-focus spec): snaps the game camera to a dead-center framing of the
/// active placed screen and holds it there until focus ends. Adapted from PoseKit's
/// <c>PoseKit.Camera.FreeCamService</c> (a sibling Dalamud plugin, not part of this repo) — same
/// two-hook takeover of the native orbit camera, same category of auto-disable safety checks, but
/// with no free-flight: there's a single fixed target pose (computed once, from the active
/// screen's own geometry) instead of WASD-accumulated motion, and consequently none of
/// PoseKit's <c>FreeCamInput</c> movement-blocking/emote-preservation machinery — player movement
/// is this feature's deliberate exit trigger instead of something to suppress. See design.md's
/// "Drop FreeCamInput entirely" decision.
/// </summary>
public sealed unsafe class ScreenFocusService : IDisposable
{
    private delegate nint CalculateView(SceneCamera* camera);

    private readonly ScreenController _screenController;
    private Hook<CalculateView>? viewHook;
    private Hook<GameCamera.Delegates.Update>? updateHook;
    private NativeCameraView.Loader? loadView;
    private GameCamera* camera;
    private nint actor;
    private Vector3 actorPosition;
    private Vector3 observedPosition;
    private nint observedActor;
    private float stationarySeconds;
    private uint territory;
    private bool disposed;
    private float dirH, dirV, distance, interpDistance, fov;
    private Vector3 focusPosition, focusForward, focusUp;
    private Vector3 scenePosition, sceneLookAt;

    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "Focus disabled.";

    public ScreenFocusService(ScreenController screenController)
    {
        _screenController = screenController;
        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Toggle()
    {
        if (disposed) return;
        if (Enabled) { Disable(); return; }
        try
        {
            var reason = EntryUnsupportedReason();
            if (reason != null) { Status = reason; return; }

            var manager = CameraManager.Instance();
            var candidate = manager->GetActiveCamera();
            NativeCameraView.Validate((nint)candidate->SceneCamera.RenderCamera, (Matrix4x4*)&candidate->SceneCamera.ViewMatrix);

            if (!CameraInfo.TryGetCameraBasis(out _, out _, out _, out _, out var fovY, out var aspectRatio, out _, out _))
                throw new InvalidOperationException("The camera's field of view is unavailable.");

            // EntryUnsupportedReason() already confirmed ActiveAnchor is non-null.
            var anchor = _screenController.ActiveAnchor!;
            var (targetPosition, targetForward, targetUp) = anchor.Transform.ComputeFocusCameraPose(fovY, aspectRatio);

            if (viewHook != null) { viewHook.Dispose(); viewHook = null; }
            if (updateHook != null) { updateHook.Dispose(); updateHook = null; }

            // Same native functions PoseKit's freecam hooks — see design.md's "Port a trimmed
            // copy of PoseKit's two-hook technique" decision. ScanText auto-resolves the leading
            // E8 call to its target, same as PoseKit's own loadView resolution.
            var loadAddress = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 17 48 8D 4D E0");
            loadView = Marshal.GetDelegateForFunctionPointer<NativeCameraView.Loader>(loadAddress);
            viewHook = Plugin.GameInteropProvider.HookFromSignature<CalculateView>(
                "48 89 5C 24 ?? 57 48 81 EC ?? ?? ?? ?? F6 81 ?? ?? ?? ?? ?? 48 8B D9 48 89 B4 24 ?? ?? ?? ??", CalculateViewDetour);
            updateHook = Plugin.GameInteropProvider.HookFromAddress<GameCamera.Delegates.Update>(
                (nint)candidate->VirtualTable->Update, UpdateCameraDetour);

            focusPosition = targetPosition;
            focusForward = targetForward;
            focusUp = targetUp;
            dirH = candidate->DirH;
            dirV = candidate->DirV;
            distance = candidate->Distance;
            interpDistance = candidate->InterpDistance;
            fov = candidate->FoV;
            scenePosition = candidate->SceneCamera.Position;
            sceneLookAt = candidate->SceneCamera.LookAtVector;
            actor = Plugin.ObjectTable.LocalPlayer!.Address;
            actorPosition = Plugin.ObjectTable.LocalPlayer.Position;
            territory = Plugin.ClientState.TerritoryType;
            camera = candidate;

            updateHook.Enable();
            viewHook.Enable();
            Enabled = true;
            Status = "Focused on screen. Move, or trigger focus again, to exit.";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[WatchAlong] Focus activation failed.");
            Disable();
            Status = $"Focus unavailable: {ex.Message}";
        }
    }

    /// <summary>
    /// Checks shared by entry, tick, and <see cref="Disable"/>'s restore-safety gate: is it still
    /// sane to touch the camera at all (logged in, not mid-transition, on the normal world
    /// camera)? Deliberately excludes the active-screen check — <see cref="Disable"/> must still
    /// restore the camera even when focus is ending because the screen was removed.
    /// </summary>
    private static string? BaseUnsupportedReason()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null) return "Log in before focusing on a screen.";
        if (Plugin.ClientState.IsGPosing) return "Leave GPose before focusing on a screen.";
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return "Focus is unavailable during this transition.";
        var manager = CameraManager.Instance();
        if (manager == null || manager->ActiveCameraIndex != 0 || manager->Camera == null ||
            manager->GetActiveCamera() != manager->Camera || manager->Camera->SceneCamera.RenderCamera == null)
            return "Focus requires the normal world camera.";
        return null;
    }

    /// <summary>
    /// Entering while moving or autorunning would immediately trip <see cref="InvalidSessionReason"/>
    /// on the very next tick — same requirement freecam has, for the same reason (a moving
    /// character would otherwise fight a just-engaged camera takeover).
    /// </summary>
    private string? EntryUnsupportedReason()
    {
        var reason = BaseUnsupportedReason();
        if (reason != null) return reason;
        if (InputManager.Addresses.IsAutoRunning.Value == 0) return "Autorun detection is unavailable.";
        if (InputManager.IsAutoRunning() || stationarySeconds < 0.25f) return "Stop moving and turn off autorun before focusing on a screen.";
        if (_screenController.ActiveAnchor is null) return "No screen is placed at this location.";
        return null;
    }

    public void Tick(float seconds)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            observedActor = 0;
            stationarySeconds = 0;
        }
        else
        {
            stationarySeconds = observedActor == player.Address && Vector3.DistanceSquared(observedPosition, player.Position) < 0.00000001f
                ? stationarySeconds + Math.Clamp(seconds, 0, 0.05f) : 0;
            observedActor = player.Address;
            observedPosition = player.Position;
        }
        if (!Enabled) return;
        try
        {
            var reason = InvalidSessionReason();
            if (reason != null)
            {
                Plugin.Log.Warning($"[WatchAlong] Focus auto-disabled: {reason}");
                Disable();
                Status = "Focus ended because the world or character state changed.";
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[WatchAlong] Focus update failed.");
            Disable();
        }
    }

    // Screen-focus spec "Player movement automatically ends focus" / "Environment changes
    // automatically end focus": the same actor-drift check freecam uses to detect unwanted
    // external movement is, here, the deliberate way a player ends focus by simply moving.
    private string? InvalidSessionReason()
    {
        var reason = BaseUnsupportedReason();
        if (reason != null) return $"unsupported ({reason})";
        if (_screenController.ActiveAnchor is null) return "screen removed";
        if (camera == null) return "camera null";
        if (CameraManager.Instance()->GetActiveCamera() != camera) return "active camera changed";
        if (Plugin.ClientState.TerritoryType != territory) return "territory changed";
        if (Plugin.ObjectTable.LocalPlayer is not { } player || player.Address != actor) return "actor changed";
        var distSq = Vector3.DistanceSquared(actorPosition, player.Position);
        if (distSq >= ActorDriftEpsilonSquared) return $"actor moved (distSq={distSq})";
        return null;
    }

    private const float ActorDriftEpsilonSquared = 0.0001f;

    private bool SessionValid() => InvalidSessionReason() == null;

    private void UpdateCameraDetour(GameCamera* current)
    {
        updateHook!.Original(current);
        if (Enabled && current == camera)
        {
            try
            {
                if (!SessionValid()) { Disable(); return; }
                current->Distance = distance;
                current->InterpDistance = interpDistance;
                current->FoV = fov;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[WatchAlong] Focus camera update failed.");
                Disable();
            }
        }
    }

    private nint CalculateViewDetour(SceneCamera* current)
    {
        var result = viewHook!.Original(current);
        if (!Enabled || camera == null || current != &camera->SceneCamera) return result;
        try
        {
            if (!SessionValid()) { Disable(); return result; }
            // Screen-focus spec "Focus holds the framing until it ends": unlike freecam, DirH/DirV
            // are never read here — the target pose is fixed, so camera-look input has no effect.
            var matrix = (Matrix4x4*)&current->ViewMatrix;
            NativeCameraView.Validate((nint)current->RenderCamera, matrix);
            current->Position = focusPosition;
            current->LookAtVector = focusPosition + focusForward;
            *matrix = Matrix4x4.CreateLookAt(focusPosition, focusPosition + focusForward, focusUp);
            NativeCameraView.Load(loadView!, (nint)current->RenderCamera, matrix);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[WatchAlong] Focus view failed.");
            Disable();
        }
        return result;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (Enabled && value && flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 or
            ConditionFlag.WatchingCutscene or ConditionFlag.WatchingCutscene78 or ConditionFlag.OccupiedInCutSceneEvent)
            Disable();
    }

    private void OnTerritoryChanged(uint _) => Disable();
    private void OnLogout(int _, int __) => Disable();

    public void Disable()
    {
        Enabled = false;
        try
        {
            if (camera != null && BaseUnsupportedReason() == null && Plugin.ClientState.TerritoryType == territory &&
                CameraManager.Instance()->GetActiveCamera() == camera)
            {
                camera->DirH = dirH;
                camera->DirV = dirV;
                camera->Distance = distance;
                camera->InterpDistance = interpDistance;
                camera->FoV = fov;
                camera->SceneCamera.Position = scenePosition;
                camera->SceneCamera.LookAtVector = sceneLookAt;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[WatchAlong] Focus restoration failed.");
        }
        finally
        {
            camera = null;
            if (viewHook != null) TryCleanup(viewHook.Disable);
            if (updateHook != null) TryCleanup(updateHook.Disable);
            Status = "Focus disabled.";
        }
    }

    private static void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "[WatchAlong] Focus cleanup failed."); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.ClientState.Logout -= OnLogout;
        Disable();
        if (viewHook != null) TryCleanup(viewHook.Dispose);
        if (updateHook != null) TryCleanup(updateHook.Dispose);
    }
}
