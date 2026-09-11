namespace WatchAlong.Shared.Screens;

/// <summary>
/// Whether a placed screen should currently be drawn, per depth-tested-rendering spec "Screens
/// are hidden during cutscenes by default" / "Screen visibility in GPose is configurable".
/// Pure decision logic — testable without the game running.
/// </summary>
public static class ScreenVisibilityGate
{
    public static bool ShouldShow(bool isInCutscene, bool isGPosing, bool showScreensInGPose)
    {
        if (isInCutscene)
            return false;

        if (isGPosing)
            return showScreensInGPose;

        return true;
    }
}
