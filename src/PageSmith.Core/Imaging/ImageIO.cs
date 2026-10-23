using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PageSmith.Core.Imaging;

/// <summary>A BGRA32 pixel buffer (premultiplication is not used).</summary>
public sealed class PixelBuffer(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;
    public byte[] Pixels { get; } = pixels;

    public PixelBuffer(int width, int height) : this(width, height, new byte[width * height * 4]) { }
}

/// <summary>Image decoding, encoding and pixel helpers built on Windows Imaging Component (through WPF).</summary>
public static class ImageIO
{
    public static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".jxr", ".wdp"];

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    public static BitmapSource Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>Loads every frame of an image file (multi-page TIFF and GIF have several), upright per EXIF.</summary>
    public static IReadOnlyList<BitmapSource> LoadFrames(string path)
    {
        using var fs = File.OpenRead(path);
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex)
        {
            throw new UserFacingException($"\"{Path.GetFileName(path)}\" is not a supported image ({ex.Message}).", ex);
        }
        var frames = new List<BitmapSource>();
        foreach (var frame in decoder.Frames)
        {
            BitmapSource upright = ApplyOrientation(frame, GetExifOrientation(frame));
            upright.Freeze();
            frames.Add(upright);
        }
        return frames;
    }

    public static BitmapSource Load(string path) => LoadFrames(path)[0];

    /// <summary>EXIF orientation tag (1 = upright) of a decoded frame, or 1 when absent.</summary>
    public static int GetExifOrientation(BitmapSource frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                foreach (var query in new[] { "/app1/ifd/{ushort=274}", "System.Photo.Orientation" })
                {
                    if (meta.ContainsQuery(query) && meta.GetQuery(query) is { } value)
                        return Convert.ToInt32(value);
                }
            }
        }
        catch
        {
            // Metadata is optional; unreadable metadata means "upright".
        }
        return 1;
    }

    /// <summary>EXIF orientation of the first frame of an image file.</summary>
    public static int GetExifOrientation(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return GetExifOrientation(decoder.Frames[0]);
    }

    /// <summary>Clockwise rotation in degrees and mirroring implied by an EXIF orientation value.</summary>
    public static (int Rotation, bool Mirror) DescribeOrientation(int orientation) => orientation switch
    {
        2 => (0, true),
        3 => (180, false),
        4 => (180, true),
        5 => (90, true),
        6 => (90, false),
        7 => (270, true),
        8 => (270, false),
        _ => (0, false),
    };

    public static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        var (rotation, mirror) = DescribeOrientation(orientation);
        if (rotation == 0 && !mirror) return source;
        var group = new TransformGroup();
        if (mirror) group.Children.Add(new ScaleTransform(-1, 1));
        if (rotation != 0) group.Children.Add(new RotateTransform(rotation));
        var result = new TransformedBitmap(source, group);
        result.Freeze();
        return result;
    }

    public static BitmapSource Rotate(BitmapSource source, int degreesClockwise)
    {
        degreesClockwise = ((degreesClockwise % 360) + 360) % 360;
        if (degreesClockwise == 0) return source;
        var result = new TransformedBitmap(source, new RotateTransform(degreesClockwise));
        result.Freeze();
        return result;
    }

    public static byte[] EncodeJpeg(BitmapSource source, int quality = 85)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(EnsureOpaqueCompatible(source)));
        return Save(encoder);
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        return Save(encoder);
    }

    public static byte[] EncodeTiff(IEnumerable<BitmapSource> frames)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        foreach (var f in frames) encoder.Frames.Add(BitmapFrame.Create(f));
        return Save(encoder);
    }

    public static byte[] EncodeBmp(BitmapSource source)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(EnsureOpaqueCompatible(source)));
        return Save(encoder);
    }

    public static byte[] Encode(BitmapSource source, ImageFormatKind format, int jpegQuality = 85) => format switch
    {
        ImageFormatKind.Jpeg => EncodeJpeg(source, jpegQuality),
        ImageFormatKind.Png => EncodePng(source),
        ImageFormatKind.Tiff => EncodeTiff([source]),
        ImageFormatKind.Bmp => EncodeBmp(source),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static PixelBuffer ToPixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var buffer = new PixelBuffer(bgra.PixelWidth, bgra.PixelHeight);
        bgra.CopyPixels(buffer.Pixels, buffer.Stride, 0);
        return buffer;
    }

    public static BitmapSource FromPixels(PixelBuffer buffer, double dpi = 96)
    {
        var bmp = BitmapSource.Create(buffer.Width, buffer.Height, dpi, dpi, PixelFormats.Bgra32, null, buffer.Pixels, buffer.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Scales an image with area averaging (downscale) or bilinear filtering (upscale).</summary>
    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        if (width == source.PixelWidth && height == source.PixelHeight) return source;
        var resized = Resize(ToPixels(source), width, height);
        return FromPixels(resized, source.DpiX > 0 ? source.DpiX : 96);
    }

    public static PixelBuffer Resize(PixelBuffer src, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var dst = new PixelBuffer(width, height);
        if (width <= src.Width && height <= src.Height)
            AreaAverage(src, dst);
        else
            Bilinear(src, dst);
        return dst;
    }

    private static void AreaAverage(PixelBuffer src, PixelBuffer dst)
    {
        var sx = (double)src.Width / dst.Width;
        var sy = (double)src.Height / dst.Height;
        Parallel.For(0, dst.Height, y =>
        {
            var y0 = y * sy;
            var y1 = y0 + sy;
            for (var x = 0; x < dst.Width; x++)
            {
                var x0 = x * sx;
                var x1 = x0 + sx;
                double b = 0, g = 0, r = 0, a = 0, total = 0;
                for (var yy = (int)y0; yy < Math.Min(src.Height, (int)Math.Ceiling(y1)); yy++)
                {
                    var wy = Math.Min(y1, yy + 1) - Math.Max(y0, yy);
                    var row = yy * src.Stride;
                    for (var xx = (int)x0; xx < Math.Min(src.Width, (int)Math.Ceiling(x1)); xx++)
                    {
                        var w = wy * (Math.Min(x1, xx + 1) - Math.Max(x0, xx));
                        var i = row + xx * 4;
                        b += src.Pixels[i] * w;
                        g += src.Pixels[i + 1] * w;
                        r += src.Pixels[i + 2] * w;
                        a += src.Pixels[i + 3] * w;
                        total += w;
                    }
                }
                var o = y * dst.Stride + x * 4;
                if (total <= 0) total = 1;
                dst.Pixels[o] = (byte)Math.Round(b / total);
                dst.Pixels[o + 1] = (byte)Math.Round(g / total);
                dst.Pixels[o + 2] = (byte)Math.Round(r / total);
                dst.Pixels[o + 3] = (byte)Math.Round(a / total);
            }
        });
    }

    private static void Bilinear(PixelBuffer src, PixelBuffer dst)
    {
        var sx = (double)(src.Width - 1) / Math.Max(1, dst.Width - 1);
        var sy = (double)(src.Height - 1) / Math.Max(1, dst.Height - 1);
        Parallel.For(0, dst.Height, y =>
        {
            var fy = y * sy;
            var y0 = (int)fy;
            var y1 = Math.Min(src.Height - 1, y0 + 1);
            var ty = fy - y0;
            for (var x = 0; x < dst.Width; x++)
            {
                var fx = x * sx;
                var x0 = (int)fx;
                var x1 = Math.Min(src.Width - 1, x0 + 1);
                var tx = fx - x0;
                var o = y * dst.Stride + x * 4;
                for (var c = 0; c < 4; c++)
                {
                    var p00 = src.Pixels[y0 * src.Stride + x0 * 4 + c];
                    var p01 = src.Pixels[y0 * src.Stride + x1 * 4 + c];
                    var p10 = src.Pixels[y1 * src.Stride + x0 * 4 + c];
                    var p11 = src.Pixels[y1 * src.Stride + x1 * 4 + c];
                    var top = p00 + (p01 - p00) * tx;
                    var bottom = p10 + (p11 - p10) * tx;
                    dst.Pixels[o + c] = (byte)Math.Round(top + (bottom - top) * ty);
                }
            }
        });
    }

    /// <summary>True when any pixel is not fully opaque.</summary>
    public static bool HasTransparency(BitmapSource source)
    {
        if (!source.Format.Equals(PixelFormats.Bgra32) && !source.Format.Equals(PixelFormats.Pbgra32) &&
            !source.Format.Equals(PixelFormats.Rgba64) && !source.Format.Equals(PixelFormats.Prgba64) &&
            !source.Format.Equals(PixelFormats.Rgba128Float) && !source.Format.Equals(PixelFormats.Prgba128Float) &&
            source.Palette?.Colors.Any(c => c.A < 255) != true)
            return false;
        var px = ToPixels(source);
        for (var i = 3; i < px.Pixels.Length; i += 4)
            if (px.Pixels[i] < 255) return true;
        return false;
    }

    /// <summary>Composites transparent pixels onto white so formats without alpha (JPEG, BMP) look right.</summary>
    public static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var px = ToPixels(source);
        var p = px.Pixels;
        for (var i = 0; i < p.Length; i += 4)
        {
            var a = p[i + 3];
            if (a == 255) continue;
            p[i] = (byte)((p[i] * a + 255 * (255 - a)) / 255);
            p[i + 1] = (byte)((p[i + 1] * a + 255 * (255 - a)) / 255);
            p[i + 2] = (byte)((p[i + 2] * a + 255 * (255 - a)) / 255);
            p[i + 3] = 255;
        }
        return FromPixels(px, source.DpiX > 0 ? source.DpiX : 96);
    }

    private static BitmapSource EnsureOpaqueCompatible(BitmapSource source) =>
        HasTransparency(source) ? FlattenOnWhite(source) : source;

    private static byte[] Save(BitmapEncoder encoder)
    {
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}

public enum ImageFormatKind
{
    Jpeg,
    Png,
    Tiff,
    Bmp,
}
