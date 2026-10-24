using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PageSmith.Core.Office;

/// <summary>
/// Converts documents by automating the Microsoft Office apps installed on the PC (late-bound COM).
/// Each call starts the app invisibly, converts, and closes only what it opened. A watchdog ends the
/// Office process it started if the conversion hangs (for example on an unexpected dialog).
/// </summary>
public static class MicrosoftOffice
{
    // Passing a password that cannot be right makes Office fail fast on protected files instead of prompting.
    private const string NoPassword = "\u0001pagesmith";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    public static Task ToPdfAsync(string input, string output, OfficeApp app, string? password = null, CancellationToken ct = default) =>
        RunGuardedAsync(app, () =>
        {
            switch (app)
            {
                case OfficeApp.Word:
                    WordToPdf(input, output, password);
                    break;
                case OfficeApp.Excel:
                    ExcelToPdf(input, output, password);
                    break;
                default:
                    PowerPointToPdf(input, output, password);
                    break;
            }
        }, ct);

    /// <summary>Opens a PDF in Word (PDF Reflow) and saves it as DOCX.</summary>
    public static Task PdfToDocxAsync(string inputPdf, string outputDocx, CancellationToken ct = default) =>
        RunGuardedAsync(OfficeApp.Word, () =>
        {
            dynamic? word = null;
            dynamic? doc = null;
            try
            {
                word = Create(OfficeApp.Word);
                word.Visible = false;
                word.DisplayAlerts = 0;
                doc = word.Documents.Open(FileName: inputPdf, ConfirmConversions: false, ReadOnly: true,
                    AddToRecentFiles: false, Visible: false, NoEncodingDialog: true);
                doc.SaveAs2(FileName: outputDocx, FileFormat: 16 /* wdFormatDocumentDefault */, AddToRecentFiles: false);
            }
            finally
            {
                CloseWord(word, doc);
            }
        }, ct);

    /// <summary>
    /// Runs Office automation on an STA thread. If it does not finish in time (or is cancelled), the Office
    /// processes started during the call are ended and a <see cref="TimeoutException"/> is thrown.
    /// Processes that were already running (the user's own windows) are never touched.
    /// </summary>
    private static async Task RunGuardedAsync(OfficeApp app, Action work, CancellationToken ct)
    {
        var exe = app switch { OfficeApp.Word => "WINWORD", OfficeApp.Excel => "EXCEL", _ => "POWERPNT" };
        var before = Process.GetProcessesByName(exe).Select(p => p.Id).ToHashSet();
        var task = StaRunner.RunAsync(work);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            foreach (var p in Process.GetProcessesByName(exe))
            {
                if (before.Contains(p.Id)) continue;
                try { p.Kill(); } catch { /* already gone */ }
            }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"Microsoft {app} did not finish within {Timeout.TotalMinutes:0} minutes.");
        }
    }

    private static void WordToPdf(string input, string output, string? password)
    {
        dynamic? word = null;
        dynamic? doc = null;
        try
        {
            word = Create(OfficeApp.Word);
            word.Visible = false;
            word.DisplayAlerts = 0; // wdAlertsNone
            try
            {
                doc = word.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                    PasswordDocument: password ?? NoPassword, Visible: false, NoEncodingDialog: true);
            }
            catch (COMException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new PasswordRequiredException(input, password != null);
            }
            doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17 /* wdExportFormatPDF */, OpenAfterExport: false,
                OptimizeFor: 0, CreateBookmarks: 1 /* headings */, DocStructureTags: true, IncludeDocProps: true);
        }
        finally
        {
            CloseWord(word, doc);
        }
    }

    private static void CloseWord(dynamic? word, dynamic? doc)
    {
        try { doc?.Close(SaveChanges: 0); } catch { /* ignore */ }
        try
        {
            if (word != null && (int)word.Documents.Count == 0) word.Quit(SaveChanges: 0);
        }
        catch
        {
            // The app may already be gone.
        }
        Release(doc);
        Release(word);
    }

    private static void ExcelToPdf(string input, string output, string? password)
    {
        dynamic? excel = null;
        dynamic? book = null;
        try
        {
            excel = Create(OfficeApp.Excel);
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.ScreenUpdating = false;
            try
            {
                book = excel.Workbooks.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: password ?? NoPassword,
                    IgnoreReadOnlyRecommended: true, AddToMru: false, Notify: false);
            }
            catch (COMException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new PasswordRequiredException(input, password != null);
            }
            book.ExportAsFixedFormat(Type: 0 /* xlTypePDF */, Filename: output, Quality: 0, IncludeDocProperties: true,
                IgnorePrintAreas: false, OpenAfterPublish: false);
        }
        finally
        {
            try { book?.Close(SaveChanges: false); } catch { /* ignore */ }
            try
            {
                if (excel != null && (int)excel.Workbooks.Count == 0) excel.Quit();
            }
            catch
            {
                // ignore
            }
            Release(book);
            Release(excel);
        }
    }

    private static void PowerPointToPdf(string input, string output, string? password)
    {
        dynamic? ppt = null;
        dynamic? pres = null;
        try
        {
            ppt = Create(OfficeApp.PowerPoint);
            var file = password != null ? $"{input}::{password}" : input;
            try
            {
                pres = ppt.Presentations.Open(FileName: file, ReadOnly: -1 /* msoTrue */, Untitled: 0, WithWindow: 0);
            }
            catch (COMException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new PasswordRequiredException(input, password != null);
            }
            pres.SaveAs(FileName: output, FileFormat: 32 /* ppSaveAsPDF */);
        }
        finally
        {
            try { pres?.Close(); } catch { /* ignore */ }
            try
            {
                // PowerPoint is single-instance: only quit if the user has nothing else open.
                if (ppt != null && (int)ppt.Presentations.Count == 0) ppt.Quit();
            }
            catch
            {
                // ignore
            }
            Release(pres);
            Release(ppt);
        }
    }

    private static dynamic Create(OfficeApp app)
    {
        var type = Type.GetTypeFromProgID(OfficeDetection.ProgId(app))
                   ?? throw new UserFacingException($"Microsoft {app} is not installed.");
        return Activator.CreateInstance(type) ?? throw new UserFacingException($"Microsoft {app} could not be started.");
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            try { Marshal.FinalReleaseComObject(com); } catch { /* ignore */ }
        }
    }
}
