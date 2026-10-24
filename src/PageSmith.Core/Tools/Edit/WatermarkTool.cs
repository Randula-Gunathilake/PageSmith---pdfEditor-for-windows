using System.Windows;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PdfSharp.Drawing;

namespace PageSmith.Core.Tools.Edit;

public enum WatermarkKind
{
    Text,
    Image,
}

public sealed record WatermarkOptions
{
    public required IReadOnlyList<InputFile> Files { get; init; }
    public required string OutputDirectory { get; init; }

    public WatermarkKind Kind { get; init; } = WatermarkKind.Text;
    public string Text { get; init; } = "CONFIDENTIAL";
    public TextStyle Style { get; init; } = new() { Size = 48, Bold = true, Color = "#D32F2F" };

    /// <summary>Image file for image watermarks.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Image width as a fraction of the page width.</summary>
    public double ImageScale { get; init; } = 0.4;

    public PagePosition Position { get; init; } = PagePosition.Center;

    /// <summary>Repeats the watermark across the page.</summary>
    public bool Mosaic { get; init; }

    /// <summary>0 = invisible, 1 = opaque. iLovePDF offers 100/75/50/25 % transparency steps.</summary>
    public double Opacity { get; init; } = 0.5;

    /// <summary>Counter-clockwise rotation in degrees, as shown on screen.</summary>
    public double Rotation { get; init; } = 45;

    /// <summary>Pages to stamp, e.g. "1-3"; null for all.</summary>
    public string? Pages { get; init; }

    /// <summary>Draws under the page content instead of over it.</summary>
    public bool BelowContent { get; init; }
}

/// <summary>Stamps text or an image over (or under) PDF pages.</summary>
public static class WatermarkTool
{
    public static Task<ToolResult> RunAsync(WatermarkOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Kind == WatermarkKind.Text && string.IsNullOrWhiteSpace(options.Text)) throw new UserFacingException("Enter the watermark text.");
            if (options.Kind == WatermarkKind.Image && (options.ImagePath == null || !File.Exists(options.ImagePath))) throw new UserFacingException("Choose an image for the watermark.");
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            var image = options.Kind == WatermarkKind.Image ? PrepareImage(options.ImagePath!, options.Opacity) : null;
            for (var f = 0; f < options.Files.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                var file = options.Files[f];
                scope.Report(f, options.Files.Count, $"Stamping {file.Name}");
                result.InputBytes += new FileInfo(file.Path).Length;
                using var doc = PdfDocs.Open(file);
                Apply(doc, options, image);
                var output = OutputPaths.ForInput(file.Path, "watermarked", ".pdf", options.OutputDirectory);
                PdfDocs.Save(doc, output);
                result.AddOutput(output);
            }
            result.Summary = $"Watermarked {options.Files.Count} file{(options.Files.Count == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);

    /// <summary>Bakes the opacity into the image's alpha channel (PDF images carry transparency as a soft mask).</summary>
    public static byte[] PrepareImage(string path, double opacity)
    {
        var px = ImageIO.ToPixels(ImageIO.Load(path));
        var alpha = Math.Clamp(opacity, 0, 1);
        for (var i = 3; i < px.Pixels.Length; i += 4) px.Pixels[i] = (byte)(px.Pixels[i] * alpha);
        return ImageIO.EncodePng(ImageIO.FromPixels(px));
    }

    public static void Apply(PdfSharp.Pdf.PdfDocument doc, WatermarkOptions o, byte[]? image)
    {
        var pages = PageRange.Parse(o.Pages, doc.PageCount);
        XImage? ximage = image != null ? XImage.FromStream(new MemoryStream(image)) : null;
        var font = o.Style.ToFont();
        var brush = new XSolidBrush(o.Style.ToColor(o.Opacity));
        try
        {
            foreach (var index in pages)
            {
                using var canvas = PageCanvas.Open(doc.Pages[index], o.BelowContent ? XGraphicsPdfPageOptions.Prepend : XGraphicsPdfPageOptions.Append);
                var gfx = canvas.Graphics;
                var page = new Size(canvas.Width, canvas.Height);
                Size box;
                if (ximage != null)
                {
                    var w = page.Width * Math.Clamp(o.ImageScale, 0.02, 1);
                    box = new Size(w, w * ximage.PixelHeight / Math.Max(1, ximage.PixelWidth));
                }
                else
                {
                    var measured = gfx.MeasureString(o.Text, font);
                    box = new Size(measured.Width, measured.Height);
                }

                var centers = new List<Point>();
                if (o.Mosaic)
                {
                    // A staggered grid that covers the page whatever the rotation.
                    var stepX = Math.Max(box.Width, box.Height) * 1.6 + 20;
                    var stepY = Math.Max(box.Height * 3, stepX * 0.6);
                    var row = 0;
                    for (var y = stepY / 2; y < page.Height + stepY / 2; y += stepY, row++)
                        for (var x = (row % 2 == 0 ? stepX / 2 : stepX); x < page.Width + stepX / 2; x += stepX)
                            centers.Add(new Point(x, y));
                }
                else
                {
                    var margin = Math.Min(page.Width, page.Height) * 0.05;
                    var rotatedBox = RotatedBounds(box, o.Rotation);
                    var topLeft = PagePositions.Place(o.Position, page, rotatedBox, margin);
                    centers.Add(new Point(topLeft.X + rotatedBox.Width / 2, topLeft.Y + rotatedBox.Height / 2));
                }

                foreach (var c in centers)
                {
                    var state = gfx.Save();
                    gfx.TranslateTransform(c.X, c.Y);
                    // Positive angles turn counter-clockwise on screen, like iLovePDF's options.
                    if (o.Rotation != 0) gfx.RotateTransform(-o.Rotation);
                    if (ximage != null)
                        gfx.DrawImage(ximage, -box.Width / 2, -box.Height / 2, box.Width, box.Height);
                    else
                        gfx.DrawString(o.Text, font, brush, new XRect(-box.Width / 2, -box.Height / 2, box.Width, box.Height), XStringFormats.Center);
                    gfx.Restore(state);
                }
            }
        }
        finally
        {
            ximage?.Dispose();
        }
    }

    private static Size RotatedBounds(Size box, double degrees)
    {
        var r = degrees * Math.PI / 180;
        var c = Math.Abs(Math.Cos(r));
        var s = Math.Abs(Math.Sin(r));
        return new Size(box.Width * c + box.Height * s, box.Width * s + box.Height * c);
    }
}
