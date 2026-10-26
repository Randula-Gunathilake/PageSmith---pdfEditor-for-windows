using System.Windows.Controls;
using PageSmith.App.Controls;
using PageSmith.App.Tools;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Abstractions.Controls;

namespace PageSmith.App.Infrastructure;

/// <summary>
/// Page hosting one tool. Each tool has its own small subclass so the navigation view can tell them
/// apart; the shared <see cref="ToolShell"/> builds the actual UI from the tool's controller.
/// </summary>
public abstract class ToolPage : Page, INavigationAware
{
    protected ToolPage(string toolId)
    {
        Controller = ToolRegistry.Get(toolId).CreateController();
        Content = new ToolShell { DataContext = Controller };
        Title = Controller.Descriptor.Title;
        // Tool pages fit the window and scroll inside their own panels; without this the navigation
        // view wraps them in a scroll viewer and editors grow taller than the window.
        ScrollViewer.SetCanContentScroll(this, false);
        // That scroll viewer also supplied the theme's text colour; a bare Page defaults to black text.
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    public ToolController Controller { get; }

    public async Task OnNavigatedToAsync()
    {
        var pending = AppServices.TakePendingFiles(Controller.Descriptor.Id);
        if (pending != null)
        {
            if (Controller.HasResult) Controller.StartOverCommand.Execute(null);
            await Controller.AddFilesAsync(pending);
        }
        await Controller.OnShownAsync();
    }

    public Task OnNavigatedFromAsync() => Task.CompletedTask;
}

public sealed class MergePage() : ToolPage("merge");
public sealed class SplitPage() : ToolPage("split");
public sealed class RemovePagesPage() : ToolPage("remove-pages");
public sealed class ExtractPagesPage() : ToolPage("extract-pages");
public sealed class OrganizePage() : ToolPage("organize");
public sealed class ScanPage() : ToolPage("scan");
public sealed class CompressPage() : ToolPage("compress");
public sealed class RepairPage() : ToolPage("repair");
public sealed class OcrPage() : ToolPage("ocr");
public sealed class JpgToPdfPage() : ToolPage("jpg-to-pdf");
public sealed class WordToPdfPage() : ToolPage("word-to-pdf");
public sealed class PowerPointToPdfPage() : ToolPage("powerpoint-to-pdf");
public sealed class ExcelToPdfPage() : ToolPage("excel-to-pdf");
public sealed class HtmlToPdfPage() : ToolPage("html-to-pdf");
public sealed class PdfToJpgPage() : ToolPage("pdf-to-jpg");
public sealed class PdfToWordPage() : ToolPage("pdf-to-word");
public sealed class PdfToPowerPointPage() : ToolPage("pdf-to-powerpoint");
public sealed class PdfToExcelPage() : ToolPage("pdf-to-excel");
public sealed class PdfToPdfAPage() : ToolPage("pdf-to-pdfa");
public sealed class PdfToMarkdownPage() : ToolPage("pdf-to-markdown");
public sealed class RotatePage() : ToolPage("rotate");
public sealed class PageNumbersPage() : ToolPage("page-numbers");
public sealed class WatermarkPage() : ToolPage("watermark");
public sealed class CropPage() : ToolPage("crop");
public sealed class EditPage() : ToolPage("edit");
public sealed class FormsPage() : ToolPage("forms");
public sealed class UnlockPage() : ToolPage("unlock");
public sealed class ProtectPage() : ToolPage("protect");
public sealed class SignPage() : ToolPage("sign");
public sealed class RedactPage() : ToolPage("redact");
public sealed class ComparePage() : ToolPage("compare");
public sealed class SummarizePage() : ToolPage("summarize");
public sealed class TranslatePage() : ToolPage("translate");

public static class ToolPages
{
    private static readonly Dictionary<string, Type> Types = new()
    {
        ["merge"] = typeof(MergePage), ["split"] = typeof(SplitPage), ["remove-pages"] = typeof(RemovePagesPage),
        ["extract-pages"] = typeof(ExtractPagesPage), ["organize"] = typeof(OrganizePage), ["scan"] = typeof(ScanPage),
        ["compress"] = typeof(CompressPage), ["repair"] = typeof(RepairPage), ["ocr"] = typeof(OcrPage),
        ["jpg-to-pdf"] = typeof(JpgToPdfPage), ["word-to-pdf"] = typeof(WordToPdfPage), ["powerpoint-to-pdf"] = typeof(PowerPointToPdfPage),
        ["excel-to-pdf"] = typeof(ExcelToPdfPage), ["html-to-pdf"] = typeof(HtmlToPdfPage), ["pdf-to-jpg"] = typeof(PdfToJpgPage),
        ["pdf-to-word"] = typeof(PdfToWordPage), ["pdf-to-powerpoint"] = typeof(PdfToPowerPointPage), ["pdf-to-excel"] = typeof(PdfToExcelPage),
        ["pdf-to-pdfa"] = typeof(PdfToPdfAPage), ["pdf-to-markdown"] = typeof(PdfToMarkdownPage), ["rotate"] = typeof(RotatePage),
        ["page-numbers"] = typeof(PageNumbersPage), ["watermark"] = typeof(WatermarkPage), ["crop"] = typeof(CropPage),
        ["edit"] = typeof(EditPage), ["forms"] = typeof(FormsPage), ["unlock"] = typeof(UnlockPage), ["protect"] = typeof(ProtectPage),
        ["sign"] = typeof(SignPage), ["redact"] = typeof(RedactPage), ["compare"] = typeof(ComparePage),
        ["summarize"] = typeof(SummarizePage), ["translate"] = typeof(TranslatePage),
    };

    public static Type TypeFor(string toolId) => Types[toolId];
}

/// <summary>Creates pages once and keeps them, so each tool keeps its files and options while you move around.</summary>
public sealed class CachingPageProvider : INavigationViewPageProvider
{
    private readonly Dictionary<Type, object> _pages = [];

    /// <summary>Pages created so far.</summary>
    public IEnumerable<object> Created => _pages.Values;

    public object? GetPage(Type pageType)
    {
        if (!_pages.TryGetValue(pageType, out var page))
        {
            page = Activator.CreateInstance(pageType)!;
            _pages[pageType] = page;
        }
        return page;
    }
}
