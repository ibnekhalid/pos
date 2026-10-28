using System.ComponentModel;
using System.Runtime.InteropServices;
using Restaurant.Client.Core.Printing;

namespace Restaurant.Desktop.Platform;

/// <summary>
/// Draws the receipt lines a thermal printer's text mode can't print (Urdu item names, accented customer
/// names) with Windows GDI, whose Uniscribe layer joins Arabic-script letters and orders right-to-left text.
/// Segoe UI ships with Windows 10/11 and covers Urdu (Naskh style). Each line is drawn grey-scale on white
/// and then thresholded to the printer's 1-bit dots.
/// </summary>
public sealed class GdiTextRasterizer : ITextRasterizer
{
    public static readonly GdiTextRasterizer Instance = new();

    private const string Face = "Segoe UI";

    /// <summary>Em size in dots for normal text: about the printer's own 12 x 24 dot font.</summary>
    private const int EmDots = 24;

    public MonoBitmap Rasterize(RasterText text)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new Win32Exception();
        var font = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            font = CreateFont(text);
            SelectObject(dc, font);

            var rtl = text.RightToLeft ? DT_RTLREADING : 0;
            var endWidth = 0;
            var endHeight = 0;
            if (!string.IsNullOrEmpty(text.End))
            {
                var r = new Rect { Right = text.Width };
                DrawText(dc, text.End, text.End.Length, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                endWidth = r.Right;
                endHeight = r.Bottom;
            }

            // The text wraps in the room left of the amount, with a gap of about one character.
            var room = endWidth == 0 ? text.Width : Math.Max(text.Width / 4, text.Width - endWidth - EmDots / 2 * text.WidthScale);
            var measure = new Rect { Right = room };
            DrawText(dc, text.Text, text.Text.Length, ref measure, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX | rtl);
            var height = Math.Max(measure.Bottom, endHeight);
            if (height <= 0) return new MonoBitmap(text.Width, 0, []);

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = text.Width, Height = -height, // negative: top-down rows
                Planes = 1, BitCount = 32
            };
            bitmap = CreateDIBSection(dc, ref header, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception();
            SelectObject(dc, bitmap);
            PatBlt(dc, 0, 0, text.Width, height, Whiteness);
            SetBkMode(dc, Transparent);
            SetTextColor(dc, 0);

            var align = text.Align switch { RasterAlign.Center => DT_CENTER, RasterAlign.Right => DT_RIGHT, _ => DT_LEFT };
            var area = new Rect { Right = room, Bottom = height };
            DrawText(dc, text.Text, text.Text.Length, ref area, align | DT_WORDBREAK | DT_NOPREFIX | rtl);
            if (endWidth > 0)
            {
                var end = new Rect { Left = text.Width - endWidth, Top = height - endHeight, Right = text.Width, Bottom = height };
                DrawText(dc, text.End!, text.End!.Length, ref end, DT_RIGHT | DT_SINGLELINE | DT_NOPREFIX);
            }
            GdiFlush();

            var argb = new int[text.Width * height];
            Marshal.Copy(pixels, argb, 0, argb.Length);
            return new MonoBitmap(text.Width, height, Threshold(argb, text.Width, height));
        }
        finally
        {
            DeleteDC(dc);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (font != IntPtr.Zero) DeleteObject(font);
        }
    }

    /// <summary>
    /// Double height (with or without double width) draws the font at twice the size: squeezing Urdu to the
    /// printer's narrow double-height shape makes it hard to read.
    /// </summary>
    private static IntPtr CreateFont(RasterText text)
    {
        var size = EmDots * Math.Max(text.WidthScale, text.HeightScale);
        var font = CreateFontW(-size, 0, 0, 0, text.Bold ? 700 : 400, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, Face);
        return font == IntPtr.Zero ? throw new Win32Exception() : font;
    }

    /// <summary>Darker than mid-grey prints as a dot (blue channel; the text is grey on white).</summary>
    private static byte[] Threshold(int[] argb, int width, int height)
    {
        var row = width / 8;
        var bits = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if ((argb[y * width + x] & 0xFF) < 128) bits[y * row + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return bits;
    }

    private const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20;
    private const uint DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_RTLREADING = 0x20000;
    private const uint Whiteness = 0x00FF0062;
    private const uint DefaultCharset = 1, AntialiasedQuality = 4;
    private const int Transparent = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)]
    private static extern int DrawText(IntPtr dc, string text, int length, ref Rect rect, uint format);
}
