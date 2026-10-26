using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Pdf;

namespace PageSmith.App.Controls;

/// <summary>
/// Draws shapes, ink, redaction marks, the crop area and new form fields in the page editor. It draws in
/// the item's own coordinates (0,0 = top-left of its bounds), in PDF points like the rest of the page.
/// </summary>
public sealed class ElementVisual : FrameworkElement
{
    private EditorItem? _item;

    public ElementVisual()
    {
        DataContextChanged += (_, e) =>
        {
            if (_item != null) _item.PropertyChanged -= OnItemChanged;
            _item = e.NewValue as EditorItem;
            if (_item != null) _item.PropertyChanged += OnItemChanged;
            InvalidateVisual();
        };
        IsHitTestVisible = false;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private static Brush Hex(string hex, double opacity = 1)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B));
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        switch (_item)
        {
            case ShapeItem s:
                DrawShape(dc, s, size);
                break;
            case InkItem ink:
                DrawInk(dc, ink);
                break;
            case RedactItem:
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromRgb(229, 57, 53)), 1), size);
                break;
            case CropItem crop:
                DrawCrop(dc, crop, size);
                break;
            case SignatureBoxItem:
                DrawLabelledBox(dc, size, Color.FromRgb(0x3E, 0x63, 0xDD), "Digital signature");
                break;
            case FieldItem field:
                DrawLabelledBox(dc, size, Color.FromRgb(0x00, 0x78, 0xD4), string.IsNullOrWhiteSpace(field.Name) ? field.Kind : $"{field.Kind}: {field.Name}");
                break;
        }
    }

    private static void DrawShape(DrawingContext dc, ShapeItem s, Rect r)
    {
        var pen = s.Stroke != null && s.StrokeWidth > 0
            ? new Pen(Hex(s.Stroke, s.Opacity), s.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }
            : null;
        var brush = s.Fill != null ? Hex(s.Fill, s.Opacity) : null;
        switch (s.Shape)
        {
            case ShapeKind.Ellipse:
                dc.DrawEllipse(brush, pen, new Point(r.Width / 2, r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            case ShapeKind.Line or ShapeKind.Arrow:
            {
                pen ??= new Pen(Brushes.Black, 1.5);
                var (a, b) = s.RisingDiagonal ? (r.BottomLeft, r.TopRight) : (r.TopLeft, r.BottomRight);
                dc.DrawLine(pen, a, b);
                if (s.Shape == ShapeKind.Arrow)
                {
                    var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    var head = Math.Max(6, s.StrokeWidth * 4);
                    var p1 = new Point(b.X - head * Math.Cos(angle - 0.45), b.Y - head * Math.Sin(angle - 0.45));
                    var p2 = new Point(b.X - head * Math.Cos(angle + 0.45), b.Y - head * Math.Sin(angle + 0.45));
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        g.BeginFigure(b, true, true);
                        g.LineTo(p1, true, false);
                        g.LineTo(p2, true, false);
                    }
                    dc.DrawGeometry(pen.Brush, null, geometry);
                }
                break;
            }
            default:
                // Keep a white-out box visible on white paper while it is being edited.
                if (s.IsWhiteout) pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 120, 120, 120)), 0.5) { DashStyle = DashStyles.Dash };
                dc.DrawRectangle(brush, pen, r);
                break;
        }
    }

    private void DrawInk(DrawingContext dc, InkItem ink)
    {
        var pen = new Pen(Hex(ink.Color, ink.Opacity), ink.Width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var origin = ink.Bounds.TopLeft;
        foreach (var stroke in ink.Strokes)
        {
            if (stroke.Count == 0) continue;
            if (stroke.Count == 1)
            {
                dc.DrawEllipse(pen.Brush, null, new Point(stroke[0].X - origin.X, stroke[0].Y - origin.Y), ink.Width / 2, ink.Width / 2);
                continue;
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(stroke[0].X - origin.X, stroke[0].Y - origin.Y), false, false);
                g.PolyLineTo(stroke.Skip(1).Select(p => new Point(p.X - origin.X, p.Y - origin.Y)).ToList(), true, true);
            }
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawCrop(DrawingContext dc, CropItem crop, Rect r)
    {
        // Dim everything outside the kept area (drawn beyond this element's bounds, which WPF allows).
        var page = new Rect(-crop.Bounds.X, -crop.Bounds.Y, crop.PageSize.Width, crop.PageSize.Height);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(page), new RectangleGeometry(r));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 20, 20, 20)), null, outside);
        dc.DrawRectangle(null, new Pen(Brushes.White, 2), r);
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x8E, 0x4E, 0xC6)), 1) { DashStyle = DashStyles.Dash }, r);
        // Rule-of-thirds guides help to centre the kept area.
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 0.5);
        for (var i = 1; i < 3; i++)
        {
            dc.DrawLine(guide, new Point(r.Width * i / 3, 0), new Point(r.Width * i / 3, r.Height));
            dc.DrawLine(guide, new Point(0, r.Height * i / 3), new Point(r.Width, r.Height * i / 3));
        }
    }

    private void DrawLabelledBox(DrawingContext dc, Rect r, Color color, string label)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)),
            new Pen(new SolidColorBrush(color), 1) { DashStyle = DashStyles.Dash }, r);
        var size = Math.Clamp(r.Height * 0.45, 5, 9);
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
            new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, r.Width - 4),
            MaxTextHeight = Math.Max(1, r.Height),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        dc.DrawText(text, new Point(2, Math.Max(0, (r.Height - text.Height) / 2)));
    }
}
