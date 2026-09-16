using System.Numerics;
using WatchAlong.Shared.Screens;
using Xunit;

namespace WatchAlong.Tests.Screens;

public class ScreenTransformTests
{
    [Fact]
    public void Corners_are_centered_on_position_with_no_rotation()
    {
        var t = new ScreenTransform { Position = new Vector3(10, 0, 0), Scale = new Vector2(4, 2) };

        var (tl, tr, br, bl) = t.Corners;

        Assert.Equal(new Vector3(8, 1, 0), tl, EqualityComparer());
        Assert.Equal(new Vector3(12, 1, 0), tr, EqualityComparer());
        Assert.Equal(new Vector3(12, -1, 0), br, EqualityComparer());
        Assert.Equal(new Vector3(8, -1, 0), bl, EqualityComparer());
    }

    [Fact]
    public void PlaceLookingAt_sets_position_and_yaw_toward_the_target()
    {
        var t = ScreenTransform.PlaceLookingAt(Vector3.Zero, new Vector3(5, 0, 0));

        Assert.Equal(Vector3.Zero, t.Position);
        // dir = (1,0,0) -> yaw = atan2(1, 0) = 90 degrees, per the ported PlaceLookingAt formula.
        Assert.Equal(90f, t.RotationDegrees.Y, 0.01f);
    }

    [Fact]
    public void ComputeFocusCameraPose_fits_an_untilted_screen_at_the_expected_distance()
    {
        // 90-degree vertical FoV, square viewport: half-FoV is 45 degrees on both axes, so
        // distance-for-height = halfH / tan(45) = halfH, distance-for-width = halfW / tan(45) =
        // halfW. Scale (4,2) makes width (halfW=2) the binding constraint over height (halfH=1).
        var t = new ScreenTransform { Position = Vector3.Zero, Scale = new Vector2(4, 2) };

        var (position, forward, up) = t.ComputeFocusCameraPose(MathF.PI / 2, aspectRatio: 1f, margin: 1f);

        // The camera sits on the screen's viewable side (-Forward) and looks back across the
        // gap (+Forward) — see ComputeFocusCameraPose's comment for why that's the correct sign.
        Assert.Equal(new Vector3(0, 0, 2), position, EqualityComparer());
        Assert.Equal(new Vector3(0, 0, -1), forward, EqualityComparer());
        Assert.Equal(new Vector3(0, 1, 0), up, EqualityComparer());
    }

    [Fact]
    public void ComputeFocusCameraPose_picks_whichever_of_width_or_height_is_binding()
    {
        var t = new ScreenTransform { Position = Vector3.Zero, Scale = new Vector2(4, 2) };

        // A wide viewport has horizontal FoV to spare, so height becomes binding: distance
        // equals distance-for-height (halfH / tan(halfFovY) = 1 / 1 = 1), not the (smaller)
        // distance-for-width.
        var (widePosition, _, _) = t.ComputeFocusCameraPose(MathF.PI / 2, aspectRatio: 4f, margin: 1f);
        Assert.Equal(new Vector3(0, 0, 1), widePosition, EqualityComparer());

        // A narrow viewport has little horizontal FoV, so width becomes binding instead — and
        // needs a larger fitting distance, placing the camera further out on the viewable side.
        var (narrowPosition, _, _) = t.ComputeFocusCameraPose(MathF.PI / 2, aspectRatio: 0.25f, margin: 1f);
        Assert.True(narrowPosition.Z > widePosition.Z, "a narrower viewport should need a larger fitting distance");
    }

    [Fact]
    public void ComputeFocusCameraPose_follows_the_screens_own_rotation_not_world_axes()
    {
        var t = new ScreenTransform
        {
            Position = new Vector3(1, 2, 3),
            RotationDegrees = new Vector3(0, 90, 30), // yawed and rolled away from the world axes
            Scale = new Vector2(4, 2),
        };

        var (position, forward, up) = t.ComputeFocusCameraPose(MathF.PI / 2, aspectRatio: 1f, margin: 1f);

        // Camera sits on the screen's viewable side, -Forward from its center (magnitude already
        // covered by the distance tests above), looks back in the +Forward direction, and its up
        // vector follows the screen's own tilt rather than staying world-up.
        Assert.Equal(-t.Forward, Vector3.Normalize(position - t.Position), EqualityComparer());
        Assert.Equal(t.Forward, forward, EqualityComparer());
        Assert.Equal(Vector3.TransformNormal(Vector3.UnitY, t.RotationMatrix), up, EqualityComparer());
        Assert.NotEqual(Vector3.UnitY, up);
    }

    private static IEqualityComparer<Vector3> EqualityComparer() => new ApproxVector3Comparer();

    private sealed class ApproxVector3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.001f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
