using Microsoft.Win32;

namespace PageSmith.Core.Office;

public enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>Finds the office suites installed on this PC.</summary>
public static class OfficeDetection
{
    private static readonly Lazy<string?> LibreOffice = new(FindLibreOffice);

    public static bool IsMicrosoftOfficeAppInstalled(OfficeApp app) =>
        Type.GetTypeFromProgID(ProgId(app)) != null;

    public static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    /// <summary>Path of soffice.exe, or null when LibreOffice is not installed.</summary>
    public static string? LibreOfficePath => LibreOffice.Value;

    public static OfficeApp? AppFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".odt" or ".ott" or ".rtf" or ".txt" or ".wpd" or ".wps" or ".xml" or ".htm" or ".html" or ".mht" or ".mhtml" => OfficeApp.Word,
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xlt" or ".xltx" or ".ods" or ".ots" or ".csv" => OfficeApp.Excel,
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".pot" or ".potx" or ".odp" => OfficeApp.PowerPoint,
        _ => null,
    };

    private static string? FindLibreOffice()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var key in new[] { @"SOFTWARE\LibreOffice\UNO\InstallPath", @"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath" })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(null) is string dir)
                {
                    var exe = Path.Combine(dir, "soffice.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch
            {
                // Registry access is best effort.
            }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var exe = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}
