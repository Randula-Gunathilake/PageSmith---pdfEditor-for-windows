using PageSmith.App.Tools;
using Wpf.Ui.Controls;

namespace PageSmith.App.Infrastructure;

public enum ToolCategory
{
    Organize,
    Optimize,
    ConvertToPdf,
    ConvertFromPdf,
    Edit,
    Security,
    Intelligence,
}

public enum InputKind
{
    Pdf,
    Images,
    Office,
    Word,
    PowerPoint,
    Excel,
    Html,
    None,
}

/// <summary>Everything the shell needs to know about a tool.</summary>
public sealed record ToolDescriptor(
    string Id,
    string Title,
    string Description,
    ToolCategory Category,
    SymbolRegular Icon,
    string Accent,
    string ActionText,
    InputKind Input,
    bool MultipleFiles,
    Func<ToolController> CreateController)
{
    /// <summary>How the tool relates to the internet: null for fully offline.</summary>
    public string? OnlineNote { get; init; }

    /// <summary>Which iLovePDF plan offers this tool (shown in the feature catalog).</summary>
    public string ILovePdfTier { get; init; } = "Free";

    /// <summary>Extra iLovePDF Premium options this tool includes.</summary>
    public string? PremiumNote { get; init; }

    /// <summary>True while the tool's engine is done but its interactive screen is still being built.</summary>
    public bool InProgress => ToolRegistry.InProgressIds.Contains(Id);

    public string[] Extensions => Input switch
    {
        InputKind.Images => Core.Imaging.ImageIO.SupportedExtensions,
        InputKind.Word => Core.Tools.Convert.OfficeToPdfTool.WordExtensions,
        InputKind.PowerPoint => Core.Tools.Convert.OfficeToPdfTool.PowerPointExtensions,
        InputKind.Excel => Core.Tools.Convert.OfficeToPdfTool.ExcelExtensions,
        InputKind.Office => [.. Core.Tools.Convert.OfficeToPdfTool.WordExtensions, .. Core.Tools.Convert.OfficeToPdfTool.ExcelExtensions, .. Core.Tools.Convert.OfficeToPdfTool.PowerPointExtensions],
        InputKind.Html => [".html", ".htm", ".mhtml", ".mht", ".svg", ".xhtml"],
        InputKind.None => [],
        _ => [".pdf"],
    };

    public string FileFilter => Input switch
    {
        InputKind.Pdf => "PDF files (*.pdf)|*.pdf",
        InputKind.None => "",
        _ => $"Supported files|{string.Join(";", Extensions.Select(e => "*" + e))}|All files|*.*",
    };

    public bool Accepts(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
}

public static class ToolRegistry
{
    public static string CategoryTitle(ToolCategory c) => c switch
    {
        ToolCategory.Organize => "Organize PDF",
        ToolCategory.Optimize => "Optimize PDF",
        ToolCategory.ConvertToPdf => "Convert to PDF",
        ToolCategory.ConvertFromPdf => "Convert from PDF",
        ToolCategory.Edit => "Edit PDF",
        ToolCategory.Security => "PDF security",
        _ => "PDF intelligence",
    };

    public static SymbolRegular CategoryIcon(ToolCategory c) => c switch
    {
        ToolCategory.Organize => SymbolRegular.SlideGrid24,
        ToolCategory.Optimize => SymbolRegular.ArrowMinimize24,
        ToolCategory.ConvertToPdf => SymbolRegular.ArrowImport24,
        ToolCategory.ConvertFromPdf => SymbolRegular.ArrowExportLtr24,
        ToolCategory.Edit => SymbolRegular.Edit24,
        ToolCategory.Security => SymbolRegular.ShieldLock24,
        _ => SymbolRegular.Sparkle24,
    };

    /// <summary>Tools still on placeholder controllers (see PendingControllers.cs and TODO.md).</summary>
    public static readonly HashSet<string> InProgressIds = ["scan", "html-to-pdf", "redact", "compare"];

    private const string Red = "#E5484D", Green = "#46A758", Blue = "#3E63DD", Purple = "#8E4EC6", Orange = "#F76B15", Teal = "#12A594", Gold = "#D9A400", Indigo = "#6E56CF";

    public static IReadOnlyList<ToolDescriptor> All { get; } =
    [
        // Organize
        new("merge", "Merge PDF", "Combine PDFs in the order you want.", ToolCategory.Organize, SymbolRegular.Merge24, Red, "Merge PDF", InputKind.Pdf, true, () => new MergeController()),
        new("split", "Split PDF", "Separate one page or a whole set into independent PDF files.", ToolCategory.Organize, SymbolRegular.ArrowSplit24, Red, "Split PDF", InputKind.Pdf, false, () => new SplitController())
            { PremiumNote = "Split by file size and smart split are Premium on iLovePDF." },
        new("remove-pages", "Remove pages", "Delete the pages you don't need.", ToolCategory.Organize, SymbolRegular.DocumentDismiss24, Red, "Remove pages", InputKind.Pdf, false, () => new RemovePagesController()),
        new("extract-pages", "Extract pages", "Pull selected pages into a new PDF.", ToolCategory.Organize, SymbolRegular.DocumentArrowRight24, Red, "Extract pages", InputKind.Pdf, false, () => new ExtractPagesController()),
        new("organize", "Organize PDF", "Sort, rotate, delete, duplicate and add pages.", ToolCategory.Organize, SymbolRegular.SlideGrid24, Red, "Save PDF", InputKind.Pdf, true, () => new OrganizeController()),
        new("scan", "Scan to PDF", "Scan with your scanner or camera, or import photos of documents.", ToolCategory.Organize, SymbolRegular.ScanCamera24, Red, "Create PDF", InputKind.Images, true, () => new ScanController()),

        // Optimize
        new("compress", "Compress PDF", "Reduce file size while keeping the best quality.", ToolCategory.Optimize, SymbolRegular.ArrowMinimize24, Green, "Compress PDF", InputKind.Pdf, true, () => new CompressController())
            { ILovePdfTier = "Free (2 files per task)" },
        new("repair", "Repair PDF", "Recover data from damaged or corrupt PDFs.", ToolCategory.Optimize, SymbolRegular.WrenchScrewdriver24, Green, "Repair PDF", InputKind.Pdf, true, () => new RepairController()),
        new("ocr", "OCR PDF", "Make scanned PDFs searchable and selectable.", ToolCategory.Optimize, SymbolRegular.ScanText24, Green, "Run OCR", InputKind.Pdf, true, () => new OcrController())
            { ILovePdfTier = "Free (50 pages) · Premium advanced OCR" },

        // Convert to PDF
        new("jpg-to-pdf", "JPG to PDF", "Turn images into PDF with your choice of size and margins.", ToolCategory.ConvertToPdf, SymbolRegular.ImageMultiple24, Gold, "Convert to PDF", InputKind.Images, true, () => new ImagesToPdfController()),
        new("word-to-pdf", "Word to PDF", "Convert DOC and DOCX documents to PDF.", ToolCategory.ConvertToPdf, SymbolRegular.DocumentText24, Blue, "Convert to PDF", InputKind.Word, true, () => new OfficeToPdfController("word-to-pdf")),
        new("powerpoint-to-pdf", "PowerPoint to PDF", "Convert PPT and PPTX slideshows to PDF.", ToolCategory.ConvertToPdf, SymbolRegular.SlideText24, Orange, "Convert to PDF", InputKind.PowerPoint, true, () => new OfficeToPdfController("powerpoint-to-pdf")),
        new("excel-to-pdf", "Excel to PDF", "Convert spreadsheets to PDF.", ToolCategory.ConvertToPdf, SymbolRegular.Table24, Green, "Convert to PDF", InputKind.Excel, true, () => new OfficeToPdfController("excel-to-pdf")),
        new("html-to-pdf", "HTML to PDF", "Convert web pages and HTML files to PDF.", ToolCategory.ConvertToPdf, SymbolRegular.Globe24, Gold, "Convert to PDF", InputKind.Html, false, () => new HtmlToPdfController())
            { OnlineNote = "Web addresses need the internet; local HTML files work offline." },

        // Convert from PDF
        new("pdf-to-jpg", "PDF to JPG", "Convert each page to an image or extract the images inside.", ToolCategory.ConvertFromPdf, SymbolRegular.Image24, Gold, "Convert to images", InputKind.Pdf, true, () => new PdfToImagesController()),
        new("pdf-to-word", "PDF to Word", "Convert PDFs into editable Word documents.", ToolCategory.ConvertFromPdf, SymbolRegular.DocumentEdit24, Blue, "Convert to Word", InputKind.Pdf, true, () => new PdfToOfficeController("pdf-to-word"))
            { PremiumNote = "OCR for scanned PDFs is Premium on iLovePDF." },
        new("pdf-to-powerpoint", "PDF to PowerPoint", "Turn PDFs into PPTX slideshows.", ToolCategory.ConvertFromPdf, SymbolRegular.SlideMultiple24, Orange, "Convert to PowerPoint", InputKind.Pdf, true, () => new PdfToOfficeController("pdf-to-powerpoint"))
            { PremiumNote = "OCR for scanned PDFs is Premium on iLovePDF." },
        new("pdf-to-excel", "PDF to Excel", "Pull table data from PDFs into spreadsheets.", ToolCategory.ConvertFromPdf, SymbolRegular.DocumentTable24, Green, "Convert to Excel", InputKind.Pdf, true, () => new PdfToOfficeController("pdf-to-excel"))
            { PremiumNote = "OCR for scanned PDFs is Premium on iLovePDF." },
        new("pdf-to-pdfa", "PDF to PDF/A", "Convert to the ISO standard for long-term archiving.", ToolCategory.ConvertFromPdf, SymbolRegular.Archive24, Teal, "Convert to PDF/A", InputKind.Pdf, true, () => new PdfToPdfAController())
            { ILovePdfTier = "Premium" },
        new("pdf-to-markdown", "PDF to Markdown", "Turn PDFs into clean Markdown for notes, docs and AI tools.", ToolCategory.ConvertFromPdf, SymbolRegular.Code24, Indigo, "Convert to Markdown", InputKind.Pdf, true, () => new PdfToMarkdownController())
            { ILovePdfTier = "Free (30 pages) · AI credits" },

        // Edit
        new("rotate", "Rotate PDF", "Rotate whole documents or single pages.", ToolCategory.Edit, SymbolRegular.ArrowRotateClockwise24, Purple, "Rotate PDF", InputKind.Pdf, true, () => new RotateController()),
        new("page-numbers", "Add page numbers", "Number pages with your position, format and typography.", ToolCategory.Edit, SymbolRegular.DocumentPageNumber24, Purple, "Add page numbers", InputKind.Pdf, true, () => new PageNumbersController()),
        new("watermark", "Add watermark", "Stamp text or an image over your PDF.", ToolCategory.Edit, SymbolRegular.Drop24, Purple, "Add watermark", InputKind.Pdf, true, () => new WatermarkController()),
        new("crop", "Crop PDF", "Crop margins or keep just the area you need.", ToolCategory.Edit, SymbolRegular.Crop24, Purple, "Crop PDF", InputKind.Pdf, false, () => new CropController()),
        new("edit", "Edit PDF", "Add text, images, shapes and drawings; change existing text.", ToolCategory.Edit, SymbolRegular.Pen24, Purple, "Save changes", InputKind.Pdf, false, () => new EditController())
            { PremiumNote = "Editing existing text is Premium on iLovePDF." },
        new("forms", "PDF Forms", "Fill forms, create fillable fields, or detect them automatically.", ToolCategory.Edit, SymbolRegular.Form24, Purple, "Save form", InputKind.Pdf, false, () => new FormsController()),

        // Security
        new("unlock", "Unlock PDF", "Remove passwords and restrictions.", ToolCategory.Security, SymbolRegular.LockOpen24, Blue, "Unlock PDF", InputKind.Pdf, true, () => new UnlockController()),
        new("protect", "Protect PDF", "Encrypt PDFs with a password and set permissions.", ToolCategory.Security, SymbolRegular.LockClosed24, Blue, "Protect PDF", InputKind.Pdf, true, () => new ProtectController()),
        new("sign", "Sign PDF", "Sign with a drawn, typed or image signature, or a digital certificate.", ToolCategory.Security, SymbolRegular.Signature24, Blue, "Sign PDF", InputKind.Pdf, false, () => new SignController())
            { PremiumNote = "Certificate-based digital signatures are Premium on iLovePDF." },
        new("redact", "Redact PDF", "Permanently remove sensitive text and areas.", ToolCategory.Security, SymbolRegular.EyeOff24, Blue, "Redact PDF", InputKind.Pdf, false, () => new RedactController()),
        new("compare", "Compare PDF", "Spot the changes between two versions side by side.", ToolCategory.Security, SymbolRegular.BranchCompare24, Blue, "Compare", InputKind.Pdf, true, () => new CompareController()),

        // Intelligence
        new("summarize", "AI Summarizer", "Get a concise summary and key points of any PDF.", ToolCategory.Intelligence, SymbolRegular.Sparkle24, Indigo, "Summarize", InputKind.Pdf, false, () => new SummarizeController())
            { ILovePdfTier = "Free (25,000 words) · AI credits" },
        new("translate", "Translate PDF", "Translate PDFs while keeping the layout.", ToolCategory.Intelligence, SymbolRegular.Translate24, Indigo, "Translate", InputKind.Pdf, false, () => new TranslateController())
            { OnlineNote = "Translation services need the internet.", ILovePdfTier = "Free (30 pages) · AI credits" },
    ];

    public static ToolDescriptor Get(string id) => All.First(t => t.Id == id);

    public static IEnumerable<ToolDescriptor> InCategory(ToolCategory c) => All.Where(t => t.Category == c);

    /// <summary>Tools that can take a PDF as input, for "Continue with…".</summary>
    public static IEnumerable<ToolDescriptor> PdfTools => All.Where(t => t.Input == InputKind.Pdf);
}
