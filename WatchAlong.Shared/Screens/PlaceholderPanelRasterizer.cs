using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WatchAlong.Shared.Screens;

/// <summary>
/// Rasterizes the "no active content" placed-screen panel (dim background + centered message)
/// into a top-down BGRA32 buffer, so it can be fed into a placed screen's depth-tested compositing
/// path the same way a real video frame would be (world-screens spec: the waiting placeholder is
/// depth-occluded like active video, not drawn as a flat, unoccluded overlay).
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlaceholderPanelRasterizer
{
    // Matches the fixed content resolution WorldVideoRenderer already assumes for real video
    // (Kosmi's renderer always outputs 1920x1080) so the placeholder shares the same aspect-ratio
    // convention as active video at the same screen placement.
    public const int Width = 1920;
    public const int Height = 1080;

    private static readonly Color PanelColor = Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A);

    /// <summary>
    /// Returns a BGRA32, row-major, unpadded (stride == width * 4) pixel buffer of
    /// <paramref name="width"/>x<paramref name="height"/> — matches the layout
    /// <c>Direct3D11VideoTexture.Upload</c> expects.
    /// </summary>
    public static byte[] Rasterize(string message, int width = Width, int height = Height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // Flat background fill: SourceCopy (no blending against the bitmap's initial
            // transparent pixels) and no antialiasing (this rectangle exactly covers the bitmap,
            // so there are no edges to smooth — antialiasing here only risks partial-coverage
            // rounding at the boundary pixels) gives every pixel PanelColor's bytes exactly.
            g.CompositingMode = CompositingMode.SourceCopy;
            g.SmoothingMode = SmoothingMode.None;
            using var panelBrush = new SolidBrush(PanelColor);
            g.FillRectangle(panelBrush, 0, 0, width, height);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var font = new Font(FontFamily.GenericSansSerif, height * 0.045f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(message, font, textBrush, new RectangleF(0, 0, width, height), format);
        }

        // Format32bppArgb is stored in memory as B, G, R, A per pixel (little-endian ARGB) — the
        // same channel order Direct3D11VideoTexture.Upload expects, so no channel swizzling here.
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = width * 4;
            var buffer = new byte[height * rowBytes];
            if (bits.Stride == rowBytes)
            {
                Marshal.Copy(bits.Scan0, buffer, 0, buffer.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(bits.Scan0 + y * bits.Stride, buffer, y * rowBytes, rowBytes);
            }
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
    }
}
