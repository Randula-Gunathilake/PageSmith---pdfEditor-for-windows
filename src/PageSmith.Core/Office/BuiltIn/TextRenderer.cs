using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace PageSmith.Core.Office.BuiltIn;

/// <summary>Renders plain-text files to PDF (A4, monospace when the text looks like code or a table).</summary>
public static class TextRenderer
{
    public static void Render(string path, string pdfPath)
    {
        PdfDocs.EnsureInitialized();
        var text = File.ReadAllText(path);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var monospace = lines.Count(l => l.Contains("  ") || l.Contains('\t')) > lines.Length / 4;

        var document = new Document();
        var section = document.AddSection();
        section.PageSetup.PageWidth = Unit.FromPoint(595.28);
        section.PageSetup.PageHeight = Unit.FromPoint(841.89);
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromPoint(56);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromPoint(56);
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = monospace ? "Consolas" : "Calibri";
        normal.Font.Size = monospace ? 9.5 : 11;

        foreach (var line in lines)
        {
            var p = section.AddParagraph(line.Replace("\t", "    "));
            p.Format.SpaceAfter = monospace ? 0 : 2;
        }
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 8;
        footer.AddPageField();

        var pdf = new PdfDocumentRenderer { Document = document };
        pdf.RenderDocument();
        pdf.PdfDocument.Info.Creator = "PageSmith";
        pdf.PdfDocument.Save(pdfPath);
    }
}
