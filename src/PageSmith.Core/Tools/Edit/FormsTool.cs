using System.Globalization;
using System.Windows;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Tools.Edit;

public enum FormFieldKind
{
    Text,
    CheckBox,
    RadioGroup,
    ComboBox,
    ListBox,
    Signature,
    Button,
}

/// <summary>A form field found in a PDF. <see cref="Bounds"/> is the first widget, in display space.</summary>
public sealed record FormFieldInfo(string Name, FormFieldKind Kind, string Value, IReadOnlyList<string> Options, int PageIndex, Rect Bounds, bool ReadOnly, bool Multiline);

/// <summary>A field to create. For radio groups, each option gets a button stacked inside <see cref="Bounds"/>.</summary>
public sealed record NewFormField(FormFieldKind Kind, string Name, int PageIndex, Rect Bounds, IReadOnlyList<string>? Options = null, string? Value = null, bool Multiline = false);

public sealed record FillFormOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>Bakes the filled values into the page so the form can no longer be edited.</summary>
    public bool Flatten { get; init; }
}

public sealed record CreateFieldsOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public required IReadOnlyList<NewFormField> Fields { get; init; }
}

/// <summary>PDF Forms: read, fill, flatten and create interactive form fields, and detect where fields belong.</summary>
public static class FormsTool
{
    public static IReadOnlyList<FormFieldInfo> ReadFields(InputFile input)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Modify);
        var list = new List<FormFieldInfo>();
        var form = AcroForm(doc);
        if (form == null) return list;
        foreach (var field in Terminal(form.Fields)) list.Add(Describe(doc, field));
        return list;
    }

    /// <summary>The document's AcroForm, or null (PDFsharp's getter throws when there is none).</summary>
    private static PdfAcroForm? AcroForm(PdfDocument doc) =>
        doc.Internals.Catalog.Elements.ContainsKey("/AcroForm") ? doc.AcroForm : null;

    private static IEnumerable<PdfAcroField> Terminal(PdfAcroField.PdfAcroFieldCollection fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            // Radio groups keep their buttons as kids but are one field.
            if (f.HasKids && f is not PdfRadioButtonField && f.Fields.Count > 0 && f.Fields[0].Elements.ContainsKey("/T"))
            {
                foreach (var kid in Terminal(f.Fields)) yield return kid;
            }
            else
            {
                yield return f;
            }
        }
    }

    private static FormFieldInfo Describe(PdfDocument doc, PdfAcroField field)
    {
        var kind = field switch
        {
            PdfTextField => FormFieldKind.Text,
            PdfCheckBoxField => FormFieldKind.CheckBox,
            PdfRadioButtonField => FormFieldKind.RadioGroup,
            PdfComboBoxField => FormFieldKind.ComboBox,
            PdfListBoxField => FormFieldKind.ListBox,
            PdfSignatureField => FormFieldKind.Signature,
            _ => FormFieldKind.Button,
        };
        var options = new List<string>();
        if (field.Elements.GetArray("/Opt") is { } opt)
        {
            foreach (var item in opt.Elements)
            {
                var v = item is PdfReference r ? r.Value : item;
                options.Add(v is PdfArray pair && pair.Elements.Count > 1 ? Str(pair.Elements[1]) : Str(v));
            }
        }
        if (kind == FormFieldKind.RadioGroup)
        {
            foreach (var widget in Widgets(field))
                if (widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N") is { } n)
                    options.AddRange(n.Elements.Keys.Where(k => k != "/Off").Select(k => k.TrimStart('/')));
        }

        var value = field switch
        {
            PdfTextField t => t.Text ?? "",
            PdfCheckBoxField c => c.Checked ? "true" : "false",
            _ => Str(field.Elements["/V"]).TrimStart('/'),
        };

        var first = Widgets(field).FirstOrDefault();
        var pageIndex = -1;
        var bounds = Rect.Empty;
        if (first != null)
        {
            pageIndex = PageOf(doc, first);
            if (pageIndex >= 0 && first.Elements.GetArray("/Rect") is { Elements.Count: 4 } rect)
            {
                var g = PageGeometry.From(doc.Pages[pageIndex]);
                bounds = g.ToDisplay(new Rect(new Point(rect.Elements.GetReal(0), rect.Elements.GetReal(1)), new Point(rect.Elements.GetReal(2), rect.Elements.GetReal(3))));
            }
        }
        var flags = field.Elements.GetInteger("/Ff");
        return new FormFieldInfo(field.Name, kind, value, options.Distinct().ToList(), pageIndex, bounds, field.ReadOnly, (flags & 4096) != 0);
    }

    private static IEnumerable<PdfDictionary> Widgets(PdfAcroField field)
    {
        if (field.Elements.ContainsKey("/Rect")) yield return field;
        if (field.Elements.GetArray("/Kids") is { } kids)
            for (var i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid && kid.Elements.ContainsKey("/Rect")) yield return kid;
    }

    private static int PageOf(PdfDocument doc, PdfDictionary widget)
    {
        if (widget.Elements.GetReference("/P") is { } pRef)
            for (var i = 0; i < doc.PageCount; i++)
                if (doc.Pages[i].Reference == pRef) return i;
        for (var i = 0; i < doc.PageCount; i++)
        {
            var annots = doc.Pages[i].Elements.GetArray("/Annots");
            if (annots == null) continue;
            for (var a = 0; a < annots.Elements.Count; a++)
                if (ReferenceEquals(annots.Elements.GetDictionary(a), widget)) return i;
        }
        return -1;
    }

    private static string Str(PdfItem? item)
    {
        switch (item)
        {
            case null:
                return "";
            case PdfReference r:
                return Str(r.Value);
            case PdfString s:
                return s.Value;
            case PdfStringObject so:
                return so.Value;
            case PdfName n:
                return n.Value;
        }
        // Literals print with PDF syntax, e.g. "(Japan)".
        var text = item.ToString() ?? "";
        return text.Length >= 2 && text[0] == '(' && text[^1] == ')' ? text[1..^1] : text;
    }

    public static Task<ToolResult> FillAsync(FillFormOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            var form = AcroForm(doc) ?? throw new UserFacingException("This PDF has no form fields. Use \"Create fields\" to add some.");
            var filled = 0;
            foreach (var field in Terminal(form.Fields))
            {
                ct.ThrowIfCancellationRequested();
                if (!options.Values.TryGetValue(field.Name, out var value) || field.ReadOnly) continue;
                if (SetValue(field, value)) filled++;
            }
            form.Elements["/NeedAppearances"] = new PdfBoolean(true);
            if (options.Flatten)
            {
                // PDFsharp writes field appearances when saving, so flatten a saved copy.
                var temp = Path.Combine(Path.GetTempPath(), $"pagesmith_form_{Guid.NewGuid():N}.pdf");
                try
                {
                    PdfDocs.Save(doc, temp);
                    using var saved = PdfDocs.Open(new InputFile(temp));
                    FormFlattener.Flatten(saved);
                    PdfDocs.Save(saved, options.OutputPath);
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* temp file */ }
                }
            }
            else
            {
                PdfDocs.Save(doc, options.OutputPath);
            }
            result.AddOutput(options.OutputPath);
            result.Summary = $"Filled {filled} field{(filled == 1 ? "" : "s")}{(options.Flatten ? " and flattened the form" : "")}.";
            return result;
        }, ct);

    private static bool SetValue(PdfAcroField field, string value)
    {
        switch (field)
        {
            case PdfTextField t:
                t.Text = value;
                return true;
            case PdfCheckBoxField c:
                c.Checked = value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" or "x" or "checked";
                return true;
            case PdfComboBoxField or PdfListBoxField:
            {
                // Set /V and /I directly: PDFsharp's SelectedIndex stores the option with PDF string syntax.
                var index = IndexOfOption(field, value);
                if (index < 0 && field is PdfListBoxField) return false;
                field.Elements.SetString("/V", index >= 0 ? OptionText(field, index) : value);
                if (index >= 0) field.Elements["/I"] = new PdfArray(field.Owner, new PdfInteger(index));
                else field.Elements.Remove("/I");
                return true;
            }
            case PdfRadioButtonField:
            {
                // /V on the group names the chosen appearance state; each button shows it or /Off.
                string? chosen = null;
                var widgets = Widgets(field).ToList();
                foreach (var widget in widgets)
                {
                    var names = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Elements.Keys ?? [];
                    chosen ??= names.FirstOrDefault(n => n != "/Off" && n.TrimStart('/').Equals(value, StringComparison.OrdinalIgnoreCase));
                }
                if (chosen == null) return false;
                field.Elements.SetName("/V", chosen);
                foreach (var widget in widgets)
                {
                    var names = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Elements.Keys ?? [];
                    widget.Elements.SetName("/AS", names.Contains(chosen) ? chosen : "/Off");
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>The export value of option <paramref name="index"/> (first element of [export display] pairs).</summary>
    private static string OptionText(PdfAcroField field, int index)
    {
        var item = field.Elements.GetArray("/Opt")!.Elements[index];
        if (item is PdfReference r) item = r.Value;
        return item is PdfArray pair && pair.Elements.Count > 0 ? Str(pair.Elements[0]) : Str(item);
    }

    private static int IndexOfOption(PdfAcroField field, string value)
    {
        var opt = field.Elements.GetArray("/Opt");
        if (opt == null) return -1;
        for (var i = 0; i < opt.Elements.Count; i++)
        {
            var item = opt.Elements[i] is PdfReference r ? r.Value : opt.Elements[i];
            var texts = item is PdfArray pair ? pair.Elements.Select(Str) : [Str(item)];
            if (texts.Any(t => t.Equals(value, StringComparison.OrdinalIgnoreCase))) return i;
        }
        return -1;
    }

    public static Task<ToolResult> CreateFieldsAsync(CreateFieldsOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Fields.Count == 0) throw new UserFacingException("Draw at least one field on the page.");
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            var acro = EnsureAcroForm(doc);
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (AcroForm(doc) is { } existing)
                foreach (var f in Terminal(existing.Fields)) names.Add(f.Name);
            foreach (var field in options.Fields)
            {
                ct.ThrowIfCancellationRequested();
                var name = Unique(field.Name, field.Kind, names);
                AddField(doc, acro, field with { Name = name });
            }
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
            PdfDocs.Save(doc, options.OutputPath);
            result.AddOutput(options.OutputPath);
            result.Summary = $"Added {options.Fields.Count} fillable field{(options.Fields.Count == 1 ? "" : "s")}.";
            return result;
        }, ct);

    private static string Unique(string name, FormFieldKind kind, HashSet<string> taken)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? kind.ToString() : name.Trim().Replace('.', '_');
        var candidate = baseName;
        for (var i = 2; !taken.Add(candidate); i++) candidate = $"{baseName}_{i}";
        return candidate;
    }

    private static PdfDictionary EnsureAcroForm(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        var acro = catalog.Elements.GetDictionary("/AcroForm");
        if (acro == null)
        {
            acro = new PdfDictionary(doc);
            doc.Internals.AddObject(acro);
            catalog.Elements.SetReference("/AcroForm", acro);
        }
        if (acro.Elements.GetArray("/Fields") == null) acro.Elements["/Fields"] = new PdfArray(doc);
        var dr = acro.Elements.GetDictionary("/DR");
        if (dr == null)
        {
            dr = new PdfDictionary(doc);
            acro.Elements["/DR"] = dr;
        }
        var fonts = dr.Elements.GetDictionary("/Font");
        if (fonts == null)
        {
            fonts = new PdfDictionary(doc);
            dr.Elements["/Font"] = fonts;
        }
        if (!fonts.Elements.ContainsKey("/Helv")) fonts.Elements.SetReference("/Helv", StandardFont(doc, "Helvetica", true));
        if (!fonts.Elements.ContainsKey("/ZaDb")) fonts.Elements.SetReference("/ZaDb", StandardFont(doc, "ZapfDingbats", false));
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        return acro;
    }

    private static PdfDictionary StandardFont(PdfDocument doc, string name, bool winAnsi)
    {
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/" + name);
        if (winAnsi) font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(font);
        return font;
    }

    private static void AddField(PdfDocument doc, PdfDictionary acro, NewFormField f)
    {
        var page = doc.Pages[f.PageIndex];
        var g = PageGeometry.From(page);
        var fields = acro.Elements.GetArray("/Fields")!;

        if (f.Kind == FormFieldKind.RadioGroup)
        {
            var options = f.Options is { Count: > 0 } ? f.Options : ["Option 1", "Option 2"];
            var parent = new PdfDictionary(doc);
            parent.Elements.SetName("/FT", "/Btn");
            parent.Elements.SetString("/T", f.Name);
            parent.Elements.SetInteger("/Ff", 49152); // radio + no toggle to off
            var selected = f.Value != null && options.Contains(f.Value) ? ExportName(f.Value) : "Off";
            parent.Elements.SetName("/V", "/" + selected);
            doc.Internals.AddObject(parent);
            var kids = new PdfArray(doc);
            var rowHeight = f.Bounds.Height / options.Count;
            var size = Math.Min(rowHeight, 14);
            for (var i = 0; i < options.Count; i++)
            {
                var box = new Rect(f.Bounds.X, f.Bounds.Y + i * rowHeight + (rowHeight - size) / 2, size, size);
                var widget = Widget(doc, page, g, box);
                widget.Elements.SetReference("/Parent", parent);
                var on = ExportName(options[i]);
                widget.Elements["/AP"] = OnOffAppearance(doc, box.Width, box.Height, on, "l"); // "l" = filled circle in ZapfDingbats
                widget.Elements.SetName("/AS", selected == on ? "/" + on : "/Off");
                var mk = new PdfDictionary(doc);
                mk.Elements.SetString("/CA", "l");
                mk.Elements["/BC"] = Color(doc, 0.4, 0.4, 0.4);
                widget.Elements["/MK"] = mk;
                kids.Elements.Add(widget.Reference!);
                AddAnnotation(page, widget);
                // Option labels next to the buttons so the user can read them.
                using var canvas = PageCanvas.Open(page);
                canvas.Graphics.DrawString(options[i], new PdfSharp.Drawing.XFont("Arial", Math.Max(6, size * 0.75)), PdfSharp.Drawing.XBrushes.Black,
                    new PdfSharp.Drawing.XRect(box.Right + 4, box.Top, Math.Max(10, f.Bounds.Width - size - 4), box.Height), PdfSharp.Drawing.XStringFormats.CenterLeft);
            }
            parent.Elements["/Kids"] = kids;
            fields.Elements.Add(parent.Reference!);
            return;
        }

        var w = Widget(doc, page, g, f.Bounds);
        w.Elements.SetString("/T", f.Name);
        var fontSize = f.Multiline ? 10 : Math.Clamp(Math.Round(f.Bounds.Height * 0.6), 6, 14);
        switch (f.Kind)
        {
            case FormFieldKind.Text:
                w.Elements.SetName("/FT", "/Tx");
                w.Elements.SetString("/DA", $"/Helv {fontSize.ToString(CultureInfo.InvariantCulture)} Tf 0 g");
                if (f.Multiline) w.Elements.SetInteger("/Ff", 4096);
                if (!string.IsNullOrEmpty(f.Value)) w.Elements.SetString("/V", f.Value);
                w.Elements["/AP"] = BoxAppearance(doc, f.Bounds.Width, f.Bounds.Height);
                break;
            case FormFieldKind.CheckBox:
                w.Elements.SetName("/FT", "/Btn");
                var check = f.Value is "true" or "Yes";
                w.Elements.SetName("/V", check ? "/Yes" : "/Off");
                w.Elements.SetName("/AS", check ? "/Yes" : "/Off");
                w.Elements["/AP"] = OnOffAppearance(doc, f.Bounds.Width, f.Bounds.Height, "Yes", "4");
                var mk = new PdfDictionary(doc);
                mk.Elements.SetString("/CA", "4");
                mk.Elements["/BC"] = Color(doc, 0.4, 0.4, 0.4);
                w.Elements["/MK"] = mk;
                break;
            case FormFieldKind.ComboBox or FormFieldKind.ListBox:
                w.Elements.SetName("/FT", "/Ch");
                if (f.Kind == FormFieldKind.ComboBox) w.Elements.SetInteger("/Ff", 131072);
                var opt = new PdfArray(doc);
                foreach (var o in f.Options ?? []) opt.Elements.Add(new PdfString(o));
                w.Elements["/Opt"] = opt;
                w.Elements.SetString("/DA", $"/Helv {fontSize.ToString(CultureInfo.InvariantCulture)} Tf 0 g");
                if (!string.IsNullOrEmpty(f.Value)) w.Elements.SetString("/V", f.Value);
                w.Elements["/AP"] = BoxAppearance(doc, f.Bounds.Width, f.Bounds.Height);
                break;
            case FormFieldKind.Signature:
                w.Elements.SetName("/FT", "/Sig");
                w.Elements["/AP"] = BoxAppearance(doc, f.Bounds.Width, f.Bounds.Height);
                break;
        }
        fields.Elements.Add(w.Reference!);
        AddAnnotation(page, w);
    }

    private static string ExportName(string option)
    {
        var sb = new StringBuilder();
        foreach (var c in option) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "Option" : sb.ToString();
    }

    private static PdfDictionary Widget(PdfDocument doc, PdfPage page, PageGeometry g, Rect display)
    {
        var user = g.ToUser(display);
        var w = new PdfDictionary(doc);
        w.Elements.SetName("/Type", "/Annot");
        w.Elements.SetName("/Subtype", "/Widget");
        var rect = new PdfArray(doc);
        foreach (var v in new[] { user.Left, user.Top, user.Right, user.Bottom }) rect.Elements.Add(new PdfReal(v));
        w.Elements["/Rect"] = rect;
        w.Elements.SetInteger("/F", 4);
        w.Elements.SetReference("/P", page);
        doc.Internals.AddObject(w);
        return w;
    }

    private static void AddAnnotation(PdfPage page, PdfDictionary widget)
    {
        var annots = page.Elements.GetArray("/Annots");
        if (annots == null)
        {
            annots = new PdfArray(page.Owner);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(widget.Reference!);
    }

    private static PdfArray Color(PdfDocument doc, double r, double g, double b) =>
        new(doc, new PdfReal(r), new PdfReal(g), new PdfReal(b));

    private static PdfDictionary Appearance(PdfDocument doc, double w, double h, string content, bool zapf)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        var bbox = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(w), new PdfReal(h));
        form.Elements["/BBox"] = bbox;
        if (zapf)
        {
            var resources = new PdfDictionary(doc);
            var fonts = new PdfDictionary(doc);
            fonts.Elements.SetReference("/ZaDb", StandardFont(doc, "ZapfDingbats", false));
            resources.Elements["/Font"] = fonts;
            form.Elements["/Resources"] = resources;
        }
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(form);
        return form;
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static PdfDictionary BoxAppearance(PdfDocument doc, double w, double h)
    {
        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", Appearance(doc, w, h, $"q 1 1 1 rg 0 0 {N(w)} {N(h)} re f 0.55 G 0.75 w 0.5 0.5 {N(w - 1)} {N(h - 1)} re S Q", false));
        return ap;
    }

    private static PdfDictionary OnOffAppearance(PdfDocument doc, double w, double h, string onName, string glyph)
    {
        var size = Math.Min(w, h) * 0.8;
        var border = $"q 1 1 1 rg 0 0 {N(w)} {N(h)} re f 0.4 G 0.75 w 0.5 0.5 {N(w - 1)} {N(h - 1)} re S Q";
        var mark = $"{border} q 0 g BT /ZaDb {N(size)} Tf {N((w - size * 0.8) / 2)} {N((h - size * 0.7) / 2)} Td ({glyph}) Tj ET Q";
        var normal = new PdfDictionary(doc);
        normal.Elements.SetReference("/" + onName, Appearance(doc, w, h, mark, true));
        normal.Elements.SetReference("/Off", Appearance(doc, w, h, border, false));
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = normal;
        return ap;
    }

    /// <summary>
    /// Suggests fields from the page layout: underscore runs and empty boxes become text fields,
    /// small squares (or ☐ characters) become checkboxes. Labels to the left name the fields.
    /// </summary>
    public static IReadOnlyList<NewFormField> DetectFields(InputFile input, CancellationToken ct = default)
    {
        var fields = new List<NewFormField>();
        using var pig = TextExtractor.Open(input);
        for (var p = 0; p < pig.NumberOfPages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pig.GetPage(p + 1);
            var text = TextExtractor.ReadPage(page, p);
            var lines = TextExtractor.GetLines(text.Words);
            foreach (var line in lines)
            {
                for (var i = 0; i < line.Words.Count; i++)
                {
                    var w = line.Words[i];
                    var label = string.Join(" ", line.Words.Take(i).Select(x => x.Text)).Trim().TrimEnd(':');
                    if (w.Text.Count(c => c == '_') >= 4)
                    {
                        // Field sits on the underline: from the first underscore to the end of the run.
                        var h = Math.Max(12, w.FontSize * 1.3);
                        fields.Add(new NewFormField(FormFieldKind.Text, NameFrom(label, fields.Count), p, new Rect(w.Bounds.Left, w.Bounds.Bottom - h, w.Bounds.Width, h)));
                    }
                    else if (w.Text is "☐" or "□" or "❏" or "▢")
                    {
                        var s = Math.Max(8, w.Bounds.Height);
                        var after = string.Join(" ", line.Words.Skip(i + 1).TakeWhile(x => x.Text is not ("☐" or "□")).Select(x => x.Text));
                        fields.Add(new NewFormField(FormFieldKind.CheckBox, NameFrom(after, fields.Count), p, new Rect(w.Bounds.Left, w.Bounds.Top, s, s)));
                    }
                }
            }

            // Rectangles drawn on the page.
            var g = PageGeometry.From(page);
            foreach (var path in SafePaths(page))
            {
                var r = path.GetBoundingRectangle();
                if (r == null) continue;
                var box = g.ToDisplay(new Rect(new Point(r.Value.Left, r.Value.Bottom), new Point(r.Value.Right, r.Value.Top)));
                if (fields.Any(f => f.PageIndex == p && f.Bounds.IntersectsWith(box))) continue;
                var inside = text.Words.Any(x => box.Contains(new Point(x.Bounds.Left + x.Bounds.Width / 2, x.Bounds.Top + x.Bounds.Height / 2)));
                if (inside) continue;
                var label = LabelLeftOf(lines, box);
                if (box.Width is >= 6 and <= 20 && Math.Abs(box.Width - box.Height) < 3)
                    fields.Add(new NewFormField(FormFieldKind.CheckBox, NameFrom(label, fields.Count), p, box));
                else if (box.Width >= 50 && box.Height is >= 12 and <= 40 && box.Width < g.Width * 0.95)
                    fields.Add(new NewFormField(FormFieldKind.Text, NameFrom(label, fields.Count), p, box));
                else if (box.Width >= 80 && box.Height > 40 && box.Height < g.Height * 0.4 && box.Width < g.Width * 0.95)
                    fields.Add(new NewFormField(FormFieldKind.Text, NameFrom(label, fields.Count), p, box, Multiline: true));
            }
        }
        return fields;
    }

    private static IEnumerable<UglyToad.PdfPig.Graphics.PdfPath> SafePaths(UglyToad.PdfPig.Content.Page page)
    {
        try
        {
            return page.Paths.Where(path => path.IsStroked || path.IsFilled).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string LabelLeftOf(IReadOnlyList<TextLineInfo> lines, Rect box)
    {
        var line = lines.FirstOrDefault(l => l.Bounds.Bottom > box.Top && l.Bounds.Top < box.Bottom && l.Bounds.Left < box.Left);
        if (line == null) return "";
        return string.Join(" ", line.Words.Where(w => w.Bounds.Right <= box.Left + 1).Select(w => w.Text)).Trim().TrimEnd(':');
    }

    private static string NameFrom(string label, int index)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(4);
        var name = string.Join(" ", words).Trim(' ', ':', '_', '.');
        return name.Length == 0 ? $"Field {index + 1}" : name;
    }
}
