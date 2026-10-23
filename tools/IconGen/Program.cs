// Renders the PageSmith application icon (multi-resolution .ico plus PNG previews) with WPF.
// Usage: dotnet run --project tools/IconGen -- <output directory>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = args.Length > 0 ? args[0] : Path.Combine("src", "PageSmith.App", "Assets");
Directory.CreateDirectory(outDir);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.ToDictionary(s => s, RenderFrame);
File.WriteAllBytes(Path.Combine(outDir, "PageSmith-256.png"), frames[256]);
File.WriteAllBytes(Path.Combine(outDir, "PageSmith-64.png"), frames[64]);

// ICO container with PNG-compressed entries (supported since Windows Vista).
using (var fs = File.Create(Path.Combine(outDir, "PageSmith.ico")))
using (var bw = new BinaryWriter(fs))
{
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    foreach (var s in sizes)
    {
        var dim = (byte)(s >= 256 ? 0 : s);
        bw.Write(dim);
        bw.Write(dim);
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)frames[s].Length);
        bw.Write((uint)offset);
        offset += frames[s].Length;
    }
    foreach (var s in sizes) bw.Write(frames[s]);
}
Console.WriteLine($"Icon written to {Path.GetFullPath(outDir)}");

static byte[] RenderFrame(int size)
{
    double s = size;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        var tileBrush = new LinearGradientBrush(Color.FromRgb(0xF2, 0x5C, 0x54), Color.FromRgb(0xB8, 0x1D, 0x3A), 45);
        var pad = s * 0.06;
        dc.DrawRoundedRectangle(tileBrush, null, new Rect(pad, pad, s - 2 * pad, s - 2 * pad), s * 0.2, s * 0.2);

        // Page with a folded corner.
        double w = s * 0.46, h = s * 0.58, x = (s - w) / 2, y = (s - h) / 2, fold = w * 0.32;
        var page = new StreamGeometry();
        using (var ctx = page.Open())
        {
            ctx.BeginFigure(new Point(x, y), true, true);
            ctx.LineTo(new Point(x + w - fold, y), true, true);
            ctx.LineTo(new Point(x + w, y + fold), true, true);
            ctx.LineTo(new Point(x + w, y + h), true, true);
            ctx.LineTo(new Point(x, y + h), true, true);
        }
        dc.DrawGeometry(Brushes.White, null, page);

        var corner = new StreamGeometry();
        using (var ctx = corner.Open())
        {
            ctx.BeginFigure(new Point(x + w - fold, y), true, true);
            ctx.LineTo(new Point(x + w - fold, y + fold), true, true);
            ctx.LineTo(new Point(x + w, y + fold), true, true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF6, 0xC1, 0xC4)), null, corner);

        // Text lines, skipped at tiny sizes where they turn into noise.
        if (size >= 32)
        {
            var lineBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x4A, 0x55));
            var lh = Math.Max(1.0, s * 0.035);
            for (var i = 0; i < 3; i++)
            {
                var ly = y + h * (0.48 + 0.14 * i);
                var lw = i == 2 ? w * 0.38 : w * 0.62;
                dc.DrawRoundedRectangle(lineBrush, null, new Rect(x + w * 0.18, ly, lw, lh), lh / 2, lh / 2);
            }
        }
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}
