using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PageSmith.App.Documents;
using PageSmith.Core.Rendering;
using Wpf.Ui.Controls;

namespace PageSmith.App.Infrastructure;

/// <summary>Prints an open document through the Windows print dialog.</summary>
public static class PdfPrinter
{
    public static async Task PrintAsync(DocumentSession session, int currentPage)
    {
        if (session.Renderer is not { } renderer) return;
        var count = session.PageCount;
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)count,
            PageRange = new System.Windows.Controls.PageRange(1, count),
        };
        if (dialog.ShowDialog() != true) return;
        IReadOnlyList<int> pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [Math.Clamp(currentPage, 0, count - 1)],
            PageRangeSelection.UserPages => Enumerable.Range(
                    Math.Clamp(dialog.PageRange.PageFrom, 1, count) - 1,
                    Math.Max(1, Math.Clamp(dialog.PageRange.PageTo, 1, count) - Math.Clamp(dialog.PageRange.PageFrom, 1, count) + 1))
                .ToList(),
            _ => Enumerable.Range(0, count).ToList(),
        };
        var paper = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        AppServices.Notify("Printing", $"Sending {pages.Count} page{(pages.Count == 1 ? "" : "s")} of \"{session.Title}\" to {dialog.PrintQueue.Name}.", ControlAppearance.Secondary, SymbolRegular.Print24);
        // Let the notification draw before the pages are prepared.
        await Task.Delay(50);
        try
        {
            dialog.PrintDocument(new Paginator(renderer, session.PageSizes, pages, paper), session.Title);
        }
        catch (PrintingCanceledException)
        {
            // Cancelled from the printer's own dialog.
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            AppServices.LogError(ex);
            AppServices.Notify("Couldn't print", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }

    /// <summary>
    /// One page per sheet, turned to match the paper's orientation, shrunk to fit inside a small margin
    /// (pages are never enlarged), and centred.
    /// </summary>
    private sealed class Paginator(PdfRenderer renderer, IReadOnlyList<Size> sizes, IReadOnlyList<int> pages, Size paper) : DocumentPaginator
    {
        private const double Margin = 18;

        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Count;
        public override Size PageSize { get; set; } = paper;
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var index = pages[pageNumber];
            var size = sizes[index];
            var paper = PageSize;
            var turn = size.Width > size.Height != paper.Width > paper.Height;
            // The page's footprint on the paper, in DIPs.
            var width = (turn ? size.Height : size.Width) * 96 / 72;
            var height = (turn ? size.Width : size.Height) * 96 / 72;
            var scale = Math.Min(1, Math.Min((paper.Width - 2 * Margin) / width, (paper.Height - 2 * Margin) / height));
            width *= scale;
            height *= scale;
            var center = new Point(paper.Width / 2, paper.Height / 2);

            var dpi = Math.Min(300, 6000 * 72 / Math.Max(size.Width, size.Height));
            // Printing calls this on the UI thread; the renderer itself works on the thread pool.
            var bitmap = Task.Run(() => renderer.RenderAsync(index, dpi)).GetAwaiter().GetResult();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (turn)
                {
                    dc.PushTransform(new RotateTransform(90, center.X, center.Y));
                    dc.DrawImage(bitmap, new Rect(center.X - height / 2, center.Y - width / 2, height, width));
                    dc.Pop();
                }
                else
                {
                    dc.DrawImage(bitmap, new Rect(center.X - width / 2, center.Y - height / 2, width, height));
                }
            }
            return new DocumentPage(visual, paper, new Rect(paper), new Rect(paper));
        }
    }
}
