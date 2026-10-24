using System.Collections.Concurrent;
using System.Windows;
using PdfSharp.Fonts;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTypeface = System.Windows.Media.Typeface;

namespace PageSmith.Core.Common;

/// <summary>
/// Resolves any font installed in Windows (system-wide or per user) for PDFsharp by asking WPF
/// for the font file. Members of TrueType collections (.ttc) are extracted into standalone fonts.
/// Unknown families fall back to Segoe UI, then Arial.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string[] Fallbacks = ["Segoe UI", "Arial"];
    private readonly ConcurrentDictionary<string, byte[]?> _fontData = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<HashSet<string>> _installed = new(LoadInstalledFamilies);

    public static IReadOnlyList<string> InstalledFamilies() =>
        System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(n => !n.StartsWith("Global ", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (var name in new[] { familyName }.Concat(Fallbacks))
        {
            if (!_installed.Value.Contains(name)) continue;
            var info = TryResolve(name, bold, italic);
            if (info != null) return info;
        }
        return null;
    }

    public byte[]? GetFont(string faceName) => _fontData.GetOrAdd(faceName, LoadFace);

    private static FontResolverInfo? TryResolve(string familyName, bool bold, bool italic)
    {
        var typeface = new WpfTypeface(new WpfFontFamily(familyName),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyph)) return null;

        var uri = glyph.FontUri;
        if (!uri.IsFile) return null;
        var index = 0;
        if (!string.IsNullOrEmpty(uri.Fragment) && int.TryParse(uri.Fragment.TrimStart('#'), out var i)) index = i;

        var simulateBold = bold && glyph.Weight.ToOpenTypeWeight() < 600;
        var simulateItalic = italic && glyph.Style == FontStyles.Normal;
        return new FontResolverInfo($"{uri.LocalPath}|{index}", simulateBold, simulateItalic);
    }

    private static byte[]? LoadFace(string faceName)
    {
        var bar = faceName.LastIndexOf('|');
        if (bar < 0) return null;
        var path = faceName[..bar];
        var index = int.Parse(faceName[(bar + 1)..]);
        var data = File.ReadAllBytes(path);
        return IsCollection(data) ? ExtractFromCollection(data, index) : data;
    }

    private static HashSet<string> LoadInstalledFamilies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            set.Add(family.Source);
            foreach (var name in family.FamilyNames.Values) set.Add(name);
        }
        return set;
    }

    private static bool IsCollection(byte[] d) => d.Length > 12 && d[0] == 't' && d[1] == 't' && d[2] == 'c' && d[3] == 'f';

    /// <summary>Rebuilds font number <paramref name="index"/> of a TrueType collection as a standalone font file.</summary>
    internal static byte[] ExtractFromCollection(byte[] ttc, int index)
    {
        var numFonts = ReadU32(ttc, 8);
        if (index < 0 || index >= numFonts) index = 0;
        var offsetTable = (int)ReadU32(ttc, 12 + 4 * index);
        var numTables = ReadU16(ttc, offsetTable + 4);

        var headerSize = 12 + 16 * numTables;
        var tables = new List<(uint Tag, uint Checksum, int Offset, int Length)>();
        for (var t = 0; t < numTables; t++)
        {
            var rec = offsetTable + 12 + 16 * t;
            tables.Add((ReadU32(ttc, rec), ReadU32(ttc, rec + 4), (int)ReadU32(ttc, rec + 8), (int)ReadU32(ttc, rec + 12)));
        }

        using var ms = new MemoryStream();
        ms.Write(ttc, offsetTable, 12);
        var dataOffset = headerSize;
        var records = new byte[16 * numTables];
        for (var t = 0; t < numTables; t++)
        {
            var (tag, checksum, _, length) = tables[t];
            WriteU32(records, 16 * t, tag);
            WriteU32(records, 16 * t + 4, checksum);
            WriteU32(records, 16 * t + 8, (uint)dataOffset);
            WriteU32(records, 16 * t + 12, (uint)length);
            dataOffset += (length + 3) & ~3;
        }
        ms.Write(records);
        foreach (var (_, _, offset, length) in tables)
        {
            ms.Write(ttc, offset, length);
            var pad = ((length + 3) & ~3) - length;
            for (var p = 0; p < pad; p++) ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    private static uint ReadU32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    private static int ReadU16(byte[] d, int o) => d[o] << 8 | d[o + 1];

    private static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24);
        d[o + 1] = (byte)(v >> 16);
        d[o + 2] = (byte)(v >> 8);
        d[o + 3] = (byte)v;
    }
}
