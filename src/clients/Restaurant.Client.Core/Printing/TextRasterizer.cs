namespace Restaurant.Client.Core.Printing;

/// <summary>
/// Draws a receipt line the printer's code page can't print (Urdu, accented names) as a picture, which
/// <see cref="EscPos"/> sends in raster mode. The desktop app implements it with Windows text shaping; the
/// browser never prints ESC/POS, so Client.Core itself has no implementation.
/// </summary>
public interface ITextRasterizer
{
    /// <returns>An image exactly <see cref="RasterText.Width"/> dots wide, as tall as the text needs.</returns>
    MonoBitmap Rasterize(RasterText text);
}

public enum RasterAlign { Left, Center, Right }

/// <param name="Text">The line; it word-wraps onto more rows when it's wider than the paper.</param>
/// <param name="End">Optional text kept at the right edge of the last row (an amount); Text then wraps in the room left of it.</param>
/// <param name="Width">Paper width in dots (576 on 80 mm, 384 on 58 mm), a multiple of 8.</param>
/// <param name="RightToLeft">Lay Text out in right-to-left reading order (it starts with Urdu or Arabic).</param>
/// <param name="WidthScale">1 or 2, as the printer's double-width text.</param>
/// <param name="HeightScale">1 or 2, as the printer's double-height text.</param>
public sealed record RasterText(string Text, string? End, int Width, RasterAlign Align, bool RightToLeft, bool Bold, int WidthScale, int HeightScale);

/// <summary>A 1-bit image: rows top to bottom, each Width / 8 bytes, most significant bit leftmost, 1 = black.</summary>
public sealed record MonoBitmap(int Width, int Height, byte[] Bits);
