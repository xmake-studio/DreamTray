using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DreamTray.App.Interop;

/// <summary>
/// Builds the tray icon at runtime instead of shipping a bitmap.
///
/// Two reasons this is worth the interop: the icon is the *system* settings glyph
/// from Segoe Fluent Icons, so it sits next to the network and volume icons
/// looking like it belongs there; and it can be re-rendered at the exact tray size
/// for the current DPI, and re-coloured (black on a light taskbar, white on a dark
/// one) whenever Windows switches theme — which a static .ico cannot do.
/// </summary>
internal static class IconFactory
{
    /// <summary>Settings gear, Segoe Fluent Icons / Segoe MDL2 Assets code point.</summary>
    private const string GearGlyph = "";

    /// <summary>
    /// Render the gear to an HICON. Caller owns the handle and must destroy it.
    /// </summary>
    /// <param name="pixelSize">Icon edge in physical pixels (16 at 100%, 20 at 125%, …).</param>
    /// <param name="light">True to draw white (dark taskbar), false for black.</param>
    public static nint CreateGear(int pixelSize, bool light)
    {
        var brush = light ? Brushes.White : Brushes.Black;
        var typeface = ResolveIconTypeface();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fluent tray glyphs are drawn at ~80% of the icon box, optically centred.
            double em = pixelSize * 0.82;
            var text = new FormattedText(
                GearGlyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, em, brush, pixelsPerDip: 1.0);

            double x = (pixelSize - text.Width) / 2;
            double y = (pixelSize - text.Height) / 2;
            dc.DrawText(text, new Point(Math.Round(x), Math.Round(y)));
        }

        var rtb = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        return BitmapToIcon(rtb, pixelSize);
    }

    /// <summary>
    /// Segoe Fluent Icons ships with Windows 11; Segoe MDL2 Assets is the Windows 10
    /// name for the same gear code point. Fall back so the icon is never a box.
    /// </summary>
    private static Typeface ResolveIconTypeface()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var tf = new Typeface(name);
            if (tf.TryGetGlyphTypeface(out var gtf) &&
                gtf.CharacterToGlyphMap.ContainsKey(GearGlyph[0]))
                return tf;
        }
        return new Typeface("Segoe UI Symbol");
    }

    /// <summary>
    /// Wrap 32-bit premultiplied BGRA pixels in an HICON. The mask bitmap is all
    /// zeros: with a 32-bit colour bitmap Windows uses the alpha channel and ignores
    /// the mask, but <c>CreateIconIndirect</c> still requires one to be present.
    /// </summary>
    private static nint BitmapToIcon(BitmapSource source, int size)
    {
        int stride = size * 4;
        var pixels = new byte[stride * size];
        source.CopyPixels(pixels, stride, 0);

        nint colorBitmap = nint.Zero, maskBitmap = nint.Zero;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down: matches WPF's row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };

            colorBitmap = CreateDIBSection(nint.Zero, ref header, 0 /* DIB_RGB_COLORS */,
                                           out nint bits, nint.Zero, 0);
            if (colorBitmap == nint.Zero || bits == nint.Zero) return nint.Zero;
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            maskBitmap = CreateBitmap(size, size, 1, 1, null);
            if (maskBitmap == nint.Zero) return nint.Zero;

            var info = new ICONINFO
            {
                fIcon = true,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap,
            };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (colorBitmap != nint.Zero) DeleteObject(colorBitmap);
            if (maskBitmap != nint.Zero) DeleteObject(maskBitmap);
        }
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter,
                   biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER header, uint usage,
                                                 out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel,
                                             byte[]? bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(nint icon);
}
