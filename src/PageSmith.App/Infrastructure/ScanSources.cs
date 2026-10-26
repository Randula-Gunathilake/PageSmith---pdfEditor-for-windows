using System.IO;
using System.Runtime.InteropServices;
using PageSmith.Core.Common;

namespace PageSmith.App.Infrastructure;

/// <summary>Gets pages from a scanner through Windows Image Acquisition (WIA), the scanning service built into Windows.</summary>
public static class ScanSources
{
    private const int ScannerDeviceType = 1;
    private const int ColorIntent = 1;
    private const int MaximizeQuality = 131072;
    private const string JpegFormat = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    // WIA error codes worth explaining.
    private const int NoDeviceAvailable = unchecked((int)0x80210015);
    private const int DeviceBusy = unchecked((int)0x80210006);
    private const int PaperEmpty = unchecked((int)0x80210003);
    private const int PaperJam = unchecked((int)0x80210002);
    private const int Offline = unchecked((int)0x80210005);

    /// <summary>Where scanned and photographed pages are kept until they are turned into a PDF.</summary>
    public static string Folder
    {
        get
        {
            var dir = Path.Combine(Path.GetTempPath(), "PageSmith", "Scans");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string NewFileName(string prefix) =>
        OutputPaths.Unique(Folder, $"{prefix} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.jpg");

    /// <summary>
    /// Shows the Windows scan dialog (device choice, preview, resolution, colour) and saves the page.
    /// Returns null when the user cancels. Must run on the UI thread.
    /// </summary>
    public static string? ScanPage()
    {
        var type = Type.GetTypeFromProgID("WIA.CommonDialog")
            ?? throw new UserFacingException("Windows Image Acquisition isn't available on this PC, so scanners can't be used. You can still take photos or add images.");
        dynamic? dialog = null;
        dynamic? image = null;
        try
        {
            dialog = Activator.CreateInstance(type)!;
            image = dialog.ShowAcquireImage(ScannerDeviceType, ColorIntent, MaximizeQuality, JpegFormat, false, true, false);
            if (image == null) return null;
            var path = NewFileName("Scan");
            image.SaveFile(path);
            return path;
        }
        catch (COMException ex)
        {
            throw new UserFacingException(ex.HResult switch
            {
                NoDeviceAvailable => "No scanner was found. Connect and switch on your scanner (or install its driver), then try again.",
                DeviceBusy => "The scanner is busy. Wait until it finishes, then try again.",
                PaperEmpty => "The document feeder is empty. Load the pages and try again.",
                PaperJam => "The scanner reports a paper jam.",
                Offline => "The scanner is offline. Check its cable or network connection.",
                _ => $"The scanner reported a problem: {ex.Message}",
            }, ex);
        }
        finally
        {
            if (image != null) Marshal.FinalReleaseComObject(image);
            if (dialog != null) Marshal.FinalReleaseComObject(dialog);
        }
    }
}
