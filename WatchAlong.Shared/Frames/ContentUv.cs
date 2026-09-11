namespace WatchAlong.Shared.Frames;

/// <summary>Normalized UV min/max for cropping a texture to just its content rect (design.md §5.2/§8.2).</summary>
public readonly record struct ContentUv(float MinU, float MinV, float MaxU, float MaxV)
{
    public static ContentUv FullFrame => new(0f, 0f, 1f, 1f);

    public static ContentUv FromContentRect(int frameWidth, int frameHeight, int contentX, int contentY, int contentW, int contentH)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return FullFrame;

        return new ContentUv(
            (float)contentX / frameWidth,
            (float)contentY / frameHeight,
            (float)(contentX + contentW) / frameWidth,
            (float)(contentY + contentH) / frameHeight);
    }

    public static ContentUv FromSlotHeader(FrameSlotHeader header) =>
        FromContentRect(header.Width, header.Height, header.ContentX, header.ContentY, header.ContentW, header.ContentH);
}
