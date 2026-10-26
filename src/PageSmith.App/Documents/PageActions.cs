using System.IO;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Tools.Convert;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Organize;
using PageSmith.Core.Tools.Security;
using Wpf.Ui.Controls;

namespace PageSmith.App.Documents;

/// <summary>
/// Quick changes made right in the viewer (page thumbnails, selected text). Each one becomes a new
/// version of the document, so it can be undone like a tool's result.
/// </summary>
public static class PageActions
{
    public static Task RotateAsync(DocumentSession session, IReadOnlyCollection<int> pages, int degrees) =>
        ApplyAsync(session, pages.Count == 1 ? "Rotate page" : "Rotate pages", async folder =>
        {
            var turns = pages.ToDictionary(p => p, _ => degrees);
            var result = await RotateTool.RunAsync(new RotateOptions { Items = [new RotateItem(session.CurrentInput, 0, turns)], OutputDirectory = folder });
            return result.OutputFiles[0];
        });

    public static Task DeleteAsync(DocumentSession session, IReadOnlyCollection<int> pages) =>
        ApplyAsync(session, pages.Count == 1 ? "Delete page" : "Delete pages", async folder =>
        {
            var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
            await RemovePagesTool.RunAsync(new RemovePagesOptions { Input = session.CurrentInput, Pages = Ranges(pages), OutputPath = output });
            return output;
        });

    /// <summary>Moves pages so they come before page <paramref name="before"/> (the page count moves them to the end).</summary>
    public static Task MoveAsync(DocumentSession session, IReadOnlyCollection<int> pages, int before)
    {
        var moving = pages.Distinct().Order().ToList();
        var order = Enumerable.Range(0, session.PageCount).Where(i => !moving.Contains(i)).ToList();
        var at = Math.Clamp(before - moving.Count(p => p < before), 0, order.Count);
        order.InsertRange(at, moving);
        if (order.SequenceEqual(Enumerable.Range(0, session.PageCount))) return Task.CompletedTask;
        return ApplyAsync(session, moving.Count == 1 ? "Move page" : "Move pages", async folder =>
        {
            var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
            await OrganizeTool.RunAsync(new OrganizeOptions
            {
                Sources = [session.CurrentInput],
                Pages = order.Select(p => new OrganizedPage(0, p)).ToList(),
                OutputPath = output,
            });
            return output;
        });
    }

    /// <summary>Whether a file can be inserted as pages: a PDF, or an image.</summary>
    public static bool CanInsert(string file) =>
        file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || ImageIO.IsSupported(file);

    /// <summary>Inserts the pages of a PDF (or images, as pages) before page <paramref name="at"/>.</summary>
    public static async Task InsertFileAsync(DocumentSession session, int at, string file)
    {
        string? password = null;
        var wrong = false;
        while (true)
        {
            try
            {
                await ApplyCoreAsync(session, "Insert pages", async folder =>
                {
                    var source = file;
                    if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        source = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".pdf");
                        await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions { Images = [file], OutputPath = source, PageSize = ImagePageSize.Fit });
                    }
                    var inserted = new InputFile(source, password);
                    var count = PdfDocs.CountPages(inserted);
                    var pages = new List<OrganizedPage>();
                    for (var i = 0; i < session.PageCount; i++)
                    {
                        if (i == at) pages.AddRange(Enumerable.Range(0, count).Select(p => new OrganizedPage(1, p)));
                        pages.Add(new OrganizedPage(0, i));
                    }
                    if (at >= session.PageCount) pages.AddRange(Enumerable.Range(0, count).Select(p => new OrganizedPage(1, p)));
                    var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
                    await OrganizeTool.RunAsync(new OrganizeOptions { Sources = [session.CurrentInput, inserted], Pages = pages, OutputPath = output });
                    return output;
                });
                return;
            }
            catch (PasswordRequiredException ex) when (!string.Equals(ex.FilePath, session.CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                password = await AppServices.AskPasswordAsync(Path.GetFileName(file), wrong);
                if (password == null) return;
                wrong = true;
            }
            catch (Exception ex)
            {
                Report(ex);
                return;
            }
        }
    }

    /// <summary>Inserts a blank page, the size of its neighbour, before page <paramref name="at"/>.</summary>
    public static Task InsertBlankAsync(DocumentSession session, int at) =>
        ApplyAsync(session, "Insert blank page", async folder =>
        {
            var like = session.PageSizes[Math.Clamp(at, 0, session.PageCount - 1)];
            var pages = Enumerable.Range(0, session.PageCount).Select(i => new OrganizedPage(0, i)).ToList();
            pages.Insert(Math.Clamp(at, 0, pages.Count), OrganizedPage.Blank(like.Width, like.Height));
            var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
            await OrganizeTool.RunAsync(new OrganizeOptions { Sources = [session.CurrentInput], Pages = pages, OutputPath = output });
            return output;
        });

    /// <summary>Copies pages into a new, unsaved document in its own tab.</summary>
    public static async Task ExtractAsync(DocumentSession session, IReadOnlyCollection<int> pages)
    {
        try
        {
            var folder = session.NewOutputFolder();
            var stem = Path.GetFileNameWithoutExtension(session.Title);
            var title = pages.Count == 1 ? $"{stem} (page {pages.First() + 1}).pdf" : $"{stem} (pages {Ranges(pages)}).pdf";
            var output = Path.Combine(folder, "extracted.pdf");
            await OrganizeTool.RunAsync(new OrganizeOptions
            {
                Sources = [session.CurrentInput],
                Pages = pages.Order().Select(p => new OrganizedPage(0, p)).ToList(),
                OutputPath = output,
            });
            await AppServices.Documents.OpenAsNewAsync(output, title);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    public static Task MarkupAsync(DocumentSession session, MarkupKind kind, IReadOnlyList<MarkupMark> marks) =>
        ApplyAsync(session, MarkupTool.Describe(kind), async folder =>
        {
            var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
            await MarkupTool.RunAsync(new MarkupOptions { Input = session.CurrentInput, OutputPath = output, Kind = kind, Marks = marks });
            return output;
        });

    public static Task RedactAsync(DocumentSession session, IReadOnlyList<RedactionMark> marks) =>
        ApplyAsync(session, "Redact", async folder =>
        {
            var output = Path.Combine(folder, Path.GetFileName(session.CurrentPath));
            await RedactTool.RunAsync(new RedactOptions { Input = session.CurrentInput, OutputPath = output, Marks = marks });
            return output;
        });

    /// <summary>"1, 3-5" style list of 0-based pages, numbered from 1.</summary>
    public static string Ranges(IEnumerable<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == sorted[i] ? $"{start + 1}" : $"{start + 1}-{sorted[i] + 1}");
        }
        return string.Join(", ", parts);
    }

    private static async Task ApplyAsync(DocumentSession session, string description, Func<string, Task<string>> make)
    {
        try
        {
            await ApplyCoreAsync(session, description, make);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private static async Task ApplyCoreAsync(DocumentSession session, string description, Func<string, Task<string>> make)
    {
        var output = await make(session.NewOutputFolder());
        await session.CommitAsync(output, description, wrong => AppServices.AskPasswordAsync(session.Title, wrong));
    }

    private static void Report(Exception ex)
    {
        if (ex is not (UserFacingException or IOException or UnauthorizedAccessException)) AppServices.LogError(ex);
        AppServices.Notify("Couldn't change the document", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
    }
}
