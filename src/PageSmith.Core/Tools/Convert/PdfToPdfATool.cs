using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Tools.Convert;

public enum PdfAConformance
{
    PdfA1b, PdfA1a,
    PdfA2b, PdfA2u, PdfA2a,
    PdfA3b, PdfA3u, PdfA3a,
}

public sealed record PdfToPdfAOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public PdfAConformance Conformance { get; init; } = PdfAConformance.PdfA2b;
}

/// <summary>
/// Converts a PDF to PDF/A for long-term archiving: adds an sRGB output intent and matching XMP
/// metadata, removes encryption, JavaScript, forbidden actions and (for PDF/A-1/2) embedded files,
/// and re-renders pages PDF/A cannot keep as they are (fonts that are not embedded, CMYK without a
/// CMYK output intent, transparency in PDF/A-1) with an invisible text layer so they stay searchable.
/// </summary>
public static class PdfToPdfATool
{
    public static (int Part, char Level) Describe(PdfAConformance c) => c switch
    {
        PdfAConformance.PdfA1a => (1, 'A'),
        PdfAConformance.PdfA1b => (1, 'B'),
        PdfAConformance.PdfA2a => (2, 'A'),
        PdfAConformance.PdfA2u => (2, 'U'),
        PdfAConformance.PdfA2b => (2, 'B'),
        PdfAConformance.PdfA3a => (3, 'A'),
        PdfAConformance.PdfA3u => (3, 'U'),
        _ => (3, 'B'),
    };

    public static async Task<ToolResult> RunAsync(PdfToPdfAOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        var (part, level) = Describe(o.Conformance);

        scope.Report(0.05, "Checking fonts, colours and transparency");
        var problems = await Task.Run(() => FindPagesToRasterize(o.Input, part), ct).ConfigureAwait(false);
        var pageText = problems.Count > 0 ? TextExtractor.ReadPages(o.Input, problems.Keys, ct).ToDictionary(p => p.Index) : [];
        var images = new Dictionary<int, byte[]>();
        if (problems.Count > 0)
        {
            using var renderer = await PdfRenderer.OpenAsync(o.Input.Path, o.Input.Password, ct).ConfigureAwait(false);
            var i = 0;
            foreach (var index in problems.Keys)
            {
                ct.ThrowIfCancellationRequested();
                scope.Slice(0.1, 0.6).Report(i++, problems.Count, $"Re-rendering page {index + 1}");
                var bmp = await renderer.RenderAsync(index, 300, ct).ConfigureAwait(false);
                images[index] = ImageIO.EncodeJpeg(bmp, 90);
            }
        }

        scope.Report(0.65, "Writing archive metadata");
        await Task.Run(() =>
        {
            using var doc = PdfDocs.Open(o.Input);
            doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();

            foreach (var (index, jpeg) in images)
                RasterizePage(doc.Pages[index], jpeg, pageText.GetValueOrDefault(index));

            RemoveForbiddenContent(doc, part, result);
            if (part == 1) DecodeLzw(doc);

            if (level == 'A' && !IsTagged(doc))
            {
                level = 'B';
                result.Warnings.Add("Conformance level A needs a tagged (accessible) PDF and this one has no tags, so PDF/A level B was produced.");
            }
            if (level == 'U' && !HasUnicodeText(doc))
            {
                level = 'B';
                result.Warnings.Add("Some fonts have no Unicode mapping, so PDF/A level B was produced instead of U.");
            }

            doc.Version = part == 1 ? 14 : 17;
            AddOutputIntent(doc);
            PrepareInfo(doc);
            PdfDocs.Save(doc, o.OutputPath);
            if (!XmpPatcher.AddPdfAIdentification(o.OutputPath, part, level))
                throw new UserFacingException("The PDF/A metadata could not be written.");
        }, ct).ConfigureAwait(false);

        result.AddOutput(o.OutputPath);
        result.Summary = $"Converted to PDF/A-{part}{char.ToLowerInvariant(level)}.";
        if (problems.Count > 0)
        {
            var reasons = problems.Values.SelectMany(r => r).Distinct();
            result.Warnings.Add($"{problems.Count} page{(problems.Count == 1 ? " was" : "s were")} re-rendered at 300 DPI ({string.Join(", ", reasons)}); their text stays searchable.");
        }
        scope.Report(1);
        return result;
    }

    /// <summary>Pages PDF/A cannot keep as they are, with the reasons.</summary>
    internal static Dictionary<int, List<string>> FindPagesToRasterize(InputFile input, int part)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Import);
        var map = new Dictionary<int, List<string>>();
        for (var i = 0; i < doc.PageCount; i++)
        {
            var reasons = new List<string>();
            var page = doc.Pages[i];
            var scan = new PageScan();
            try
            {
                Scan(ContentReader.ReadContent(page), page.Elements.GetDictionary("/Resources"), scan, 0);
            }
            catch
            {
                reasons.Add("unreadable content");
            }
            if (scan.UnembeddedFont) reasons.Add("fonts not embedded");
            if (scan.Cmyk) reasons.Add("CMYK colours");
            if (part == 1 && (scan.Transparency || page.Elements.GetDictionary("/Group")?.Elements.GetName("/S") == "/Transparency"))
                reasons.Add("transparency");
            if (reasons.Count > 0) map[i] = reasons;
        }
        return map;
    }

    private sealed class PageScan
    {
        public bool UnembeddedFont;
        public bool Cmyk;
        public bool Transparency;
        public readonly HashSet<PdfDictionary> Visited = new(ReferenceEqualityComparer.Instance);
    }

    private static void Scan(CSequence ops, PdfDictionary? resources, PageScan scan, int depth)
    {
        if (resources != null)
        {
            if (resources.Elements.GetDictionary("/Font") is { } fonts)
                foreach (var key in fonts.Elements.Keys)
                    if (fonts.Elements.GetDictionary(key) is { } font && !IsEmbedded(font)) scan.UnembeddedFont = true;
            if (resources.Elements.GetDictionary("/ColorSpace") is { } spaces)
                foreach (var key in spaces.Elements.Keys)
                    if (spaces.Elements[key]?.ToString()?.Contains("DeviceCMYK") == true) scan.Cmyk = true;
            if (resources.Elements.GetDictionary("/ExtGState") is { } states)
            {
                foreach (var key in states.Elements.Keys)
                {
                    if (states.Elements.GetDictionary(key) is not { } gs) continue;
                    var smask = gs.Elements["/SMask"];
                    if (smask != null && smask.ToString() != "/None") scan.Transparency = true;
                    if (gs.Elements.ContainsKey("/CA") && gs.Elements.GetReal("/CA") < 1) scan.Transparency = true;
                    if (gs.Elements.ContainsKey("/ca") && gs.Elements.GetReal("/ca") < 1) scan.Transparency = true;
                    var bm = gs.Elements.GetName("/BM");
                    if (!string.IsNullOrEmpty(bm) && bm is not ("/Normal" or "/Compatible")) scan.Transparency = true;
                }
            }
        }

        foreach (var item in ops)
        {
            if (item is not COperator op) continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.k or OpCodeName.K:
                    scan.Cmyk = true;
                    break;
                case OpCodeName.Do when op.Operands.Count == 1 && op.Operands[0] is CName name:
                    var x = resources?.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name.Name);
                    if (x == null || !scan.Visited.Add(x)) break;
                    if (x.Elements.GetName("/Subtype") == "/Image")
                    {
                        if (x.Elements["/ColorSpace"]?.ToString()?.Contains("DeviceCMYK") == true) scan.Cmyk = true;
                        if (x.Elements.ContainsKey("/SMask")) scan.Transparency = true;
                    }
                    else if (x.Elements.GetName("/Subtype") == "/Form" && depth < 10 && x.Stream != null)
                    {
                        if (x.Elements.GetDictionary("/Group")?.Elements.GetName("/S") == "/Transparency") scan.Transparency = true;
                        try
                        {
                            Scan(ContentReader.ReadContent(x.Stream.UnfilteredValue), x.Elements.GetDictionary("/Resources") ?? resources, scan, depth + 1);
                        }
                        catch
                        {
                            scan.UnembeddedFont = true; // unknown content: be safe and re-render
                        }
                    }
                    break;
            }
        }
    }

    private static bool IsEmbedded(PdfDictionary font)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type3") return true;
        var descriptor = font.Elements.GetDictionary("/FontDescriptor");
        if (subtype == "/Type0")
        {
            var descendant = font.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0);
            descriptor = descendant?.Elements.GetDictionary("/FontDescriptor");
        }
        return descriptor != null && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
    }

    /// <summary>Replaces a page's content with a picture of it plus an invisible text layer.</summary>
    private static void RasterizePage(PdfPage page, byte[] jpeg, PageText? text)
    {
        // A fresh, unshared resource dictionary: the old one may be shared with other pages.
        // It must be set before anything reads page.Resources, which PDFsharp caches.
        page.Elements["/Resources"] = new PdfResources(page.Owner);
        page.Elements.Remove("/Group");
        page.Contents.Elements.Clear();
        if (page.Annotations.Count > 0)
        {
            // Keep links (they have no appearance); other annotations may reference the removed resources.
            for (var i = page.Annotations.Count - 1; i >= 0; i--)
                if (page.Annotations[i].Elements.GetName("/Subtype") != "/Link") page.Annotations.Remove(page.Annotations[i]);
        }
        using (var canvas = PageCanvas.Open(page))
        using (var ms = new MemoryStream(jpeg))
        using (var image = XImage.FromStream(ms))
        {
            canvas.Graphics.DrawImage(image, 0, 0, canvas.Width, canvas.Height);
        }
        if (text != null && text.Words.Count > 0) InvisibleText.AddWords(page, text.Words);
    }

    private static void RemoveForbiddenContent(PdfDocument doc, int part, ToolResult result)
    {
        var catalog = doc.Internals.Catalog;
        catalog.Elements.Remove("/AA");
        if (catalog.Elements.GetDictionary("/OpenAction") is { } open && open.Elements.GetName("/S") is "/JavaScript" or "/Launch")
            catalog.Elements.Remove("/OpenAction");
        if (catalog.Elements.GetDictionary("/Names") is { } names)
        {
            names.Elements.Remove("/JavaScript");
            if (part < 3 && names.Elements.ContainsKey("/EmbeddedFiles"))
            {
                names.Elements.Remove("/EmbeddedFiles");
                result.Warnings.Add($"Attached files were removed (PDF/A-{part} does not allow them; PDF/A-3 does).");
            }
        }
        if (part == 1) catalog.Elements.Remove("/OCProperties");
        if (catalog.Elements.GetDictionary("/AcroForm") is { } form)
        {
            form.Elements.Remove("/XFA");
            form.Elements.Remove("/NeedAppearances");
        }

        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/AA");
            if (!page.HasAnnotations) continue;
            for (var i = page.Annotations.Count - 1; i >= 0; i--)
            {
                var annot = page.Annotations[i];
                var subtype = annot.Elements.GetName("/Subtype");
                if (subtype is "/Movie" or "/Sound" or "/Screen" or "/3D" or "/RichMedia" || part < 3 && subtype == "/FileAttachment")
                {
                    page.Annotations.Remove(annot);
                    continue;
                }
                annot.Elements.Remove("/AA");
                if (annot.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") is "/JavaScript" or "/Launch" or "/ImportData" or "/ResetForm" or "/SubmitForm" or "/Sound" or "/Movie")
                    annot.Elements.Remove("/A");
                // Annotations must print and must not be hidden.
                annot.Elements.SetInteger("/F", 4);
                if (part == 1) annot.Elements.Remove("/CA");
            }
        }

        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary d) continue;
            if (d.Elements.GetName("/Subtype") == "/Image")
            {
                d.Elements.Remove("/Alternates");
                d.Elements.Remove("/OPI");
                if (d.Elements.GetBoolean("/Interpolate")) d.Elements.SetBoolean("/Interpolate", false);
            }
            if (d.Elements.GetName("/Type") == "/Filespec" && part == 3 && !d.Elements.ContainsKey("/AFRelationship"))
                d.Elements.SetName("/AFRelationship", "/Unspecified");
        }
    }

    private static void DecodeLzw(PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary { Stream: not null } d) continue;
            if (!PdfImageCodec.Filters(d).Contains("/LZWDecode")) continue;
            if (d.Stream.TryUnfilter()) d.Stream.Zip();
        }
    }

    private static bool IsTagged(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        return catalog.Elements.ContainsKey("/StructTreeRoot") &&
               catalog.Elements.GetDictionary("/MarkInfo")?.Elements.GetBoolean("/Marked") == true;
    }

    private static bool HasUnicodeText(PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary d || d.Elements.GetName("/Type") != "/Font") continue;
            var subtype = d.Elements.GetName("/Subtype");
            if (d.Elements.ContainsKey("/ToUnicode")) continue;
            var encoding = d.Elements["/Encoding"]?.ToString() ?? "";
            if (subtype != "/Type0" && (encoding.Contains("WinAnsi") || encoding.Contains("MacRoman") || encoding.Contains("Standard"))) continue;
            return false;
        }
        return true;
    }

    private static void AddOutputIntent(PdfDocument doc)
    {
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        if (!File.Exists(profilePath)) throw new UserFacingException("The Windows sRGB colour profile is missing, so no PDF/A output intent can be added.");
        var icc = new PdfDictionary(doc);
        icc.CreateStream(File.ReadAllBytes(profilePath));
        icc.Elements.SetInteger("/N", 3);
        icc.Stream!.Zip();
        doc.Internals.AddObject(icc);

        var intent = new PdfDictionary(doc);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFA1");
        intent.Elements.SetString("/OutputConditionIdentifier", "sRGB IEC61966-2.1");
        intent.Elements.SetString("/RegistryName", "http://www.color.org");
        intent.Elements.SetString("/Info", "sRGB IEC61966-2.1");
        intent.Elements.SetReference("/DestOutputProfile", icc);
        doc.Internals.AddObject(intent);

        var intents = new PdfArray(doc);
        intents.Elements.Add(intent.Reference!);
        doc.Internals.Catalog.Elements["/OutputIntents"] = intents;
    }

    /// <summary>Sets the Info dictionary dates and creator; the XMP packet is derived from it after saving.</summary>
    private static void PrepareInfo(PdfDocument doc)
    {
        var info = doc.Info;
        var now = DateTime.Now;
        var created = info.Elements.ContainsKey("/CreationDate") ? info.CreationDate : now;
        if (created == DateTime.MinValue) created = now;
        info.CreationDate = created;
        info.ModificationDate = now;
        if (string.IsNullOrEmpty(info.Creator)) info.Creator = "PageSmith";
        info.Elements.Remove("/Trapped");
        doc.Internals.Catalog.Elements.Remove("/Metadata");
    }
}
