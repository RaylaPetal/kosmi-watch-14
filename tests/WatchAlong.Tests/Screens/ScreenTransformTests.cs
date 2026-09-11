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

    private static IEqualityComparer<Vector3> EqualityComparer() => new ApproxVector3Comparer();

    private sealed class ApproxVector3Comparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.001f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
