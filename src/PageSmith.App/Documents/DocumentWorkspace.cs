using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Office;
using PageSmith.Core.Tools.Convert;
using Wpf.Ui.Controls;

namespace PageSmith.App.Documents;

/// <summary>The documents open in tabs, and opening, saving and closing them.</summary>
public sealed partial class DocumentWorkspace : ObservableObject
{
    public ObservableCollection<DocumentSession> Documents { get; } = [];

    /// <summary>The document tab in front, or null when the Home tab (the tools) is.</summary>
    [ObservableProperty] public partial DocumentSession? Active { get; set; }

    /// <summary>Documents being opened or converted, for a progress indicator.</summary>
    [ObservableProperty] public partial string? Opening { get; set; }

    private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml", ".mht", ".mhtml"];

    /// <summary>Everything PageSmith can open as a document: PDFs, and files it can turn into PDFs.</summary>
    public static bool CanOpen(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".pdf" || ImageIO.IsSupported(path) || OfficeDetection.AppFor(path) != null || HtmlExtensions.Contains(ext);
    }

    public const string OpenFilter =
        "All supported files|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.doc;*.docx;*.rtf;*.odt;*.txt;*.xls;*.xlsx;*.csv;*.ods;*.ppt;*.pptx;*.odp;*.html;*.htm" +
        "|PDF documents (*.pdf)|*.pdf" +
        "|Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic" +
        "|Office documents|*.doc;*.docx;*.rtf;*.odt;*.txt;*.xls;*.xlsx;*.csv;*.ods;*.ppt;*.pptx;*.odp" +
        "|Web pages|*.html;*.htm" +
        "|All files|*.*";

    /// <summary>Shows the Open dialog and opens the chosen files in tabs.</summary>
    public async Task BrowseAsync()
    {
        var dialog = new OpenFileDialog { Title = "Open", Filter = OpenFilter, Multiselect = true };
        if (dialog.ShowDialog(AppServices.Window) == true) await OpenAsync(dialog.FileNames);
    }

    /// <summary>Opens files in tabs (bringing an already open one to the front) and shows the last one.</summary>
    public async Task OpenAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            var open = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (open != null)
            {
                Active = open;
                continue;
            }
            if (!File.Exists(path))
            {
                AppServices.Notify("File not found", path, ControlAppearance.Caution, SymbolRegular.DocumentError24);
                continue;
            }
            if (!CanOpen(path))
            {
                AppServices.Notify("Can't open this file", $"\"{Path.GetFileName(path)}\" isn't a PDF, image, Office document or web page.", ControlAppearance.Caution);
                continue;
            }
            Opening = Path.GetFileName(path);
            try
            {
                var session = await OpenOneAsync(path);
                if (session == null) continue;
                Documents.Add(session);
                Active = session;
                if (session.FilePath != null)
                {
                    AppServices.Settings.AddRecent([session.FilePath]);
                    AppServices.SaveSettings();
                }
            }
            catch (Exception ex) when (ex is UserFacingException or IOException or UnauthorizedAccessException)
            {
                AppServices.Notify($"Couldn't open {Path.GetFileName(path)}", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            catch (Exception ex)
            {
                AppServices.LogError(ex);
                AppServices.Notify($"Couldn't open {Path.GetFileName(path)}", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                Opening = null;
            }
        }
    }

    /// <summary>Opens a PDF (asking for its password) or converts another file into a new, unsaved PDF.</summary>
    private static async Task<DocumentSession?> OpenOneAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            string? password = null;
            var wrong = false;
            while (true)
            {
                try
                {
                    return await DocumentSession.OpenAsync(path, path, Path.GetFileName(path), password);
                }
                catch (PasswordRequiredException)
                {
                    password = await AppServices.AskPasswordAsync(Path.GetFileName(path), wrong);
                    if (password == null) return null;
                    wrong = true;
                }
            }
        }

        // Anything else becomes a new PDF named after the file; it is unsaved until the user saves it.
        var title = Path.GetFileNameWithoutExtension(path) + ".pdf";
        var temp = Path.Combine(Path.GetTempPath(), "PageSmith", "Converting", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var pdf = Path.Combine(temp, title);
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ImageIO.IsSupported(path))
            {
                await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions
                {
                    Images = [path],
                    OutputPath = pdf,
                    PageSize = ImagePageSize.Fit,
                    Orientation = PaperOrientation.Auto,
                });
            }
            else if (HtmlExtensions.Contains(ext))
            {
                var settings = new WebCaptureSettings();
                await using var page = await WebPageCapture.OpenAsync(new Uri(path), settings, null, CancellationToken.None);
                await page.SaveAsPdfAsync(pdf, settings, null, CancellationToken.None);
            }
            else
            {
                await OfficeToPdfTool.ConvertAsync(new InputFile(path), pdf, AppServices.Settings.OfficeEngine);
            }
            return await DocumentSession.OpenAsync(pdf, null, title, null);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp folder */ }
        }
    }

    /// <summary>Opens a PDF made by PageSmith (extracted pages, say) as a new, unsaved document called <paramref name="title"/>.</summary>
    public async Task OpenAsNewAsync(string pdf, string title, string? password = null)
    {
        var session = await DocumentSession.OpenAsync(pdf, null, title, password);
        Documents.Add(session);
        Active = session;
    }

    /// <summary>Saves to the document's file, or asks where for a new document. Returns false if cancelled or failed.</summary>
    public async Task<bool> SaveAsync(DocumentSession session)
    {
        if (session.FilePath == null) return await SaveAsAsync(session);
        return await TrySaveAsync(session, session.FilePath);
    }

    public async Task<bool> SaveAsAsync(DocumentSession session)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            Filter = "PDF document (*.pdf)|*.pdf",
            FileName = session.Title,
            InitialDirectory = session.FilePath != null ? Path.GetDirectoryName(session.FilePath) : AppServices.Settings.OutputFolder,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(AppServices.Window) != true) return false;
        return await TrySaveAsync(session, dialog.FileName);
    }

    private async Task<bool> TrySaveAsync(DocumentSession session, string path)
    {
        try
        {
            await session.SaveToAsync(path);
            AppServices.Notify("Saved", path, ControlAppearance.Success, SymbolRegular.Save24);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically the file is open in another program, or the folder is read-only.
            if (await AppServices.ConfirmAsync("Couldn't save", $"{ex.Message}\n\nSave a copy somewhere else?", "Save as…"))
                return await SaveAsAsync(session);
            return false;
        }
    }

    /// <summary>Closes a tab, offering to save unsaved changes. Returns false if the user cancelled.</summary>
    public async Task<bool> CloseAsync(DocumentSession session)
    {
        if (session.IsDirty)
        {
            Active = session;
            var dialog = new ContentDialog
            {
                Title = $"Save changes to \"{session.Title}\"?",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = session.FilePath == null ? "This document hasn't been saved yet." : "Your changes will be lost if you don't save them.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            switch (await AppServices.ShowDialogAsync(dialog))
            {
                case ContentDialogResult.Primary:
                    if (!await SaveAsync(session)) return false;
                    break;
                case ContentDialogResult.Secondary:
                    break;
                default:
                    return false;
            }
        }
        var index = Documents.IndexOf(session);
        Documents.Remove(session);
        if (ReferenceEquals(Active, session))
            Active = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        session.Dispose();
        return true;
    }

    /// <summary>Closes every tab (for exit). Returns false if the user cancelled on an unsaved one.</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var session in Documents.ToList())
            if (!await CloseAsync(session)) return false;
        return true;
    }

    /// <summary>Brings the next or previous tab to the front; Home counts as the first tab.</summary>
    public void Cycle(int step)
    {
        var tabs = new List<DocumentSession?> { null };
        tabs.AddRange(Documents);
        var index = tabs.IndexOf(Active);
        Active = tabs[(index + step + tabs.Count) % tabs.Count];
    }
}
