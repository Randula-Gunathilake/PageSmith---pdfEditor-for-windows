using System.Globalization;
using System.Security;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Pdf;

/// <summary>
/// PDFsharp regenerates the XMP metadata packet on every save and has no PDF/A identification in it.
/// This rewrites the packet inside a saved file, in place and at the same byte length (XMP packets may
/// be padded with whitespace), so the cross-reference table stays valid. The new packet is built from
/// the saved Info dictionary, which keeps both in agreement as PDF/A requires.
/// </summary>
public static class XmpPatcher
{
    private static readonly byte[] Begin = "<?xpacket begin="u8.ToArray();
    private static readonly byte[] End = "<?xpacket end=\"w\"?>"u8.ToArray();

    /// <returns>False when the file has no XMP packet or the new one does not fit.</returns>
    public static bool AddPdfAIdentification(string path, int part, char level)
    {
        var info = ReadInfo(path);
        var data = File.ReadAllBytes(path);
        var start = IndexOf(data, Begin, 0);
        if (start < 0) return false;
        var end = IndexOf(data, End, start);
        if (end < 0) return false;
        end += End.Length;

        var packet = Encoding.UTF8.GetBytes(BuildPacket(info, part, level, padding: 0));
        var room = end - start;
        if (packet.Length > room) return false;
        var padded = Encoding.UTF8.GetBytes(BuildPacket(info, part, level, room - packet.Length));
        if (padded.Length != room) return false;
        Buffer.BlockCopy(padded, 0, data, start, room);
        File.WriteAllBytes(path, data);
        return true;
    }

    private sealed record Info(string? Title, string? Author, string? Subject, string? Keywords, string? Creator, string? Producer, string? Created, string? Modified);

    private static Info ReadInfo(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var i = doc.Info;
        string? Raw(string key) => i.Elements[key]?.ToString()?.Trim('(', ')');
        string? Text(string key) => i.Elements.ContainsKey(key) ? i.Elements.GetString(key) : null;
        return new Info(Text("/Title"), Text("/Author"), Text("/Subject"), Text("/Keywords"), Text("/Creator"), Text("/Producer"),
            PdfDateToXmp(Raw("/CreationDate")), PdfDateToXmp(Raw("/ModDate")));
    }

    /// <summary>Converts "D:20261024095545+08'00'" to "2026-10-24T09:55:45+08:00".</summary>
    public static string? PdfDateToXmp(string? pdfDate)
    {
        if (string.IsNullOrEmpty(pdfDate)) return null;
        var s = pdfDate.StartsWith("D:") ? pdfDate[2..] : pdfDate;
        if (s.Length < 4) return null;
        string Part(int index, int length, string fallback) => s.Length >= index + length ? s.Substring(index, length) : fallback;
        var date = $"{Part(0, 4, "0000")}-{Part(4, 2, "01")}-{Part(6, 2, "01")}T{Part(8, 2, "00")}:{Part(10, 2, "00")}:{Part(12, 2, "00")}";
        if (s.Length > 14)
        {
            var tz = s[14..];
            if (tz.StartsWith('Z')) return date + "Z";
            var sign = tz[0];
            var digits = new string(tz.Where(char.IsDigit).ToArray());
            if ((sign == '+' || sign == '-') && digits.Length >= 2)
                return $"{date}{sign}{digits[..2]}:{(digits.Length >= 4 ? digits.Substring(2, 2) : "00")}";
        }
        return date;
    }

    private static string BuildPacket(Info info, int part, char level, int padding)
    {
        static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">");
        sb.Append($"<pdfaid:part>{part}</pdfaid:part><pdfaid:conformance>{level}</pdfaid:conformance></rdf:Description>");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:format>application/pdf</dc:format>");
        if (!string.IsNullOrEmpty(info.Title)) sb.Append($"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(info.Title)}</rdf:li></rdf:Alt></dc:title>");
        if (!string.IsNullOrEmpty(info.Author)) sb.Append($"<dc:creator><rdf:Seq><rdf:li>{X(info.Author)}</rdf:li></rdf:Seq></dc:creator>");
        if (!string.IsNullOrEmpty(info.Subject)) sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{X(info.Subject)}</rdf:li></rdf:Alt></dc:description>");
        sb.Append("</rdf:Description>");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">");
        if (info.Created != null) sb.Append($"<xmp:CreateDate>{info.Created}</xmp:CreateDate>");
        if (info.Modified != null) sb.Append($"<xmp:ModifyDate>{info.Modified}</xmp:ModifyDate><xmp:MetadataDate>{info.Modified}</xmp:MetadataDate>");
        if (!string.IsNullOrEmpty(info.Creator)) sb.Append($"<xmp:CreatorTool>{X(info.Creator)}</xmp:CreatorTool>");
        sb.Append("</rdf:Description>");
        sb.Append("<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">");
        if (!string.IsNullOrEmpty(info.Producer)) sb.Append($"<pdf:Producer>{X(info.Producer)}</pdf:Producer>");
        if (!string.IsNullOrEmpty(info.Keywords)) sb.Append($"<pdf:Keywords>{X(info.Keywords)}</pdf:Keywords>");
        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");
        sb.Append(new string(' ', Math.Max(0, padding)));
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    private static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        for (var i = from; i <= data.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }
}
