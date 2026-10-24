using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace PageSmith.Core.Pdf;

/// <summary>Information about an image XObject that PageSmith can decode.</summary>
public sealed record PdfImageInfo(PdfDictionary Dictionary, int Width, int Height, int Components, bool IsJpeg, bool HasSoftMask, bool HasColorKeyMask);

/// <summary>Decodes image XObjects (8-bit Gray/RGB, JPEG or Flate/LZW/uncompressed) into WPF bitmaps.</summary>
public static class PdfImageCodec
{
    public static bool IsImage(PdfDictionary dict) =>
        dict.Elements.GetName("/Subtype") == "/Image" && dict.Stream != null;

    /// <summary>Returns null when the image uses a format PageSmith leaves untouched (masks, CMYK, JPEG 2000, CCITT, JBIG2, indexed…).</summary>
    public static PdfImageInfo? Inspect(PdfDictionary dict)
    {
        if (!IsImage(dict)) return null;
        if (dict.Elements.GetBoolean("/ImageMask")) return null;
        if (dict.Elements.ContainsKey("/Decode")) return null;
        var bpc = dict.Elements.GetInteger("/BitsPerComponent");
        var width = dict.Elements.GetInteger("/Width");
        var height = dict.Elements.GetInteger("/Height");
        if (width <= 0 || height <= 0) return null;

        var filters = Filters(dict);
        var isJpeg = filters.Count == 1 && filters[0] == "/DCTDecode";
        if (!isJpeg && filters.Any(f => f is not ("/FlateDecode" or "/LZWDecode"))) return null;
        if (!isJpeg && bpc != 8) return null;

        var comps = Components(dict);
        if (comps is not (1 or 3)) return null;

        var hasSMask = dict.Elements.ContainsKey("/SMask");
        var colorKey = dict.Elements["/Mask"] is PdfArray;
        return new PdfImageInfo(dict, width, height, comps, isJpeg, hasSMask, colorKey);
    }

    public static IReadOnlyList<string> Filters(PdfDictionary dict)
    {
        var item = dict.Elements["/Filter"];
        if (item is PdfReference r) item = r.Value;
        return item switch
        {
            PdfName n => [n.Value],
            PdfArray a => a.Elements.Select(e => (e is PdfReference rr ? rr.Value : e) is PdfName nn ? nn.Value : "?").ToList(),
            _ => [],
        };
    }

    /// <summary>Number of colour components of the image's colour space, or 0 if unsupported.</summary>
    public static int Components(PdfDictionary dict)
    {
        var cs = dict.Elements["/ColorSpace"];
        if (cs is PdfReference r) cs = r.Value;
        switch (cs)
        {
            case PdfName n:
                return n.Value switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
            case PdfArray a when a.Elements.Count >= 2:
            {
                var kind = (a.Elements[0] is PdfReference r0 ? r0.Value : a.Elements[0]) as PdfName;
                switch (kind?.Value)
                {
                    case "/ICCBased":
                        var profile = a.Elements[1] is PdfReference pr ? pr.Value as PdfDictionary : a.Elements[1] as PdfDictionary;
                        return profile?.Elements.GetInteger("/N") ?? 0;
                    case "/CalRGB":
                        return 3;
                    case "/CalGray":
                        return 1;
                }
                return 0;
            }
            default:
                // JPEGs without a colour space take it from the JPEG data.
                return 0;
        }
    }

    /// <summary>Decodes the image; returns null when the data does not match its dictionary.</summary>
    public static BitmapSource? Decode(PdfImageInfo info)
    {
        var stream = info.Dictionary.Stream!;
        try
        {
            if (info.IsJpeg)
            {
                using var ms = new MemoryStream(stream.Value);
                var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                if (frame.Format == PixelFormats.Cmyk32) return null;
                frame.Freeze();
                return frame;
            }

            var filter = info.Dictionary.Elements["/Filter"];
            var raw = filter == null ? stream.Value : Filtering.Decode(stream.Value, filter, info.Dictionary.Elements["/DecodeParms"]) ?? stream.Value;
            var stride = info.Width * info.Components;
            if (raw.Length < stride * info.Height) return null;
            var format = info.Components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
            var bmp = BitmapSource.Create(info.Width, info.Height, 96, 96, format, null, raw, stride);
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Converts a decoded bitmap to the raw 8-bit samples the PDF image dictionary expects.</summary>
    public static byte[] ToRawSamples(BitmapSource source, int components)
    {
        var format = components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        var converted = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
        var stride = converted.PixelWidth * components;
        var data = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(data, stride, 0);
        return data;
    }

    /// <summary>Number of distinct colours in a sample of the image, capped at <paramref name="cap"/>.</summary>
    public static int EstimateColorCount(BitmapSource source, int cap = 512)
    {
        var px = Imaging.ImageIO.ToPixels(source);
        var colors = new HashSet<int>();
        var step = Math.Max(1, (px.Width * px.Height) / 40000);
        for (var i = 0; i < px.Width * px.Height; i += step)
        {
            var o = i * 4;
            colors.Add(px.Pixels[o] | px.Pixels[o + 1] << 8 | px.Pixels[o + 2] << 16);
            if (colors.Count >= cap) break;
        }
        return colors.Count;
    }
}
