namespace WatchAlong.Shared.Input;

/// <summary>
/// Maps a point in the displayed (possibly scaled/cropped) viewer image to CEF view
/// coordinates, per design.md §8.3 ("accounting for UV crop and window scale"). Pure and
/// CefSharp-independent so it can be unit tested; <c>InputRouter</c> (renderer project) is the
/// thin glue that feeds its output into CEF's <c>SendMouse*Event</c> calls.
/// </summary>
public static class InputCoordinateMapper
{
    public static (int X, int Y) MapToViewCoordinates(
        double displayX, double displayY,
        double displayWidth, double displayHeight,
        int contentX, int contentY, int contentW, int contentH)
    {
        if (displayWidth <= 0 || displayHeight <= 0)
            return (contentX, contentY);

        var scaleX = contentW / displayWidth;
        var scaleY = contentH / displayHeight;

        var x = contentX + displayX * scaleX;
        var y = contentY + displayY * scaleY;

        return ((int)Math.Round(x), (int)Math.Round(y));
    }
}
