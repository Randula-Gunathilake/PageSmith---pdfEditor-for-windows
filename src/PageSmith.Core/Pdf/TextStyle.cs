using System.Windows;
using PdfSharp.Drawing;

namespace PageSmith.Core.Pdf;

/// <summary>Nine anchor positions on a page.</summary>
public enum PagePosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>Font settings shared by page numbers, watermarks and text elements.</summary>
public sealed record TextStyle
{
    public string Font { get; init; } = "Arial";
    public double Size { get; init; } = 12;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }

    /// <summary>Colour as #RRGGBB.</summary>
    public string Color { get; init; } = "#000000";

    public XFont ToFont()
    {
        var style = (Bold, Italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        if (Underline) style |= XFontStyleEx.Underline;
        return new XFont(Font, Math.Max(1, Size), style, new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode));
    }

    public XColor ToColor(double opacity = 1) => ParseColor(Color, opacity);

    public static XColor ParseColor(string hex, double opacity = 1)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..]; // ARGB: ignore alpha, opacity is separate
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) v = 0;
        return XColor.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), (int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF));
    }
}

public static class PagePositions
{
    /// <summary>Top-left corner for a box of the given size at an anchor position with a margin.</summary>
    public static Point Place(PagePosition position, Size page, Size box, double margin)
    {
        var col = (int)position % 3;
        var row = (int)position / 3;
        var x = col switch { 0 => margin, 1 => (page.Width - box.Width) / 2, _ => page.Width - margin - box.Width };
        var y = row switch { 0 => margin, 1 => (page.Height - box.Height) / 2, _ => page.Height - margin - box.Height };
        return new Point(x, y);
    }

    /// <summary>Swaps left and right (for the left-hand page of a facing pair).</summary>
    public static PagePosition Mirror(PagePosition p) => ((int)p % 3) switch
    {
        0 => p + 2,
        2 => p - 2,
        _ => p,
    };
}
