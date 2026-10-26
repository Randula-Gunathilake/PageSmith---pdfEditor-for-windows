using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PageSmith.Core.Common;

/// <summary>
/// Registers PageSmith with Windows as a PDF app for the current user, without admin rights. It then
/// appears in "Open with" and in Settings › Apps › Default apps, where the user can make it the default:
/// Windows doesn't let apps make themselves the default, so that last step is the user's.
/// </summary>
public static class PdfAppRegistration
{
    public const string ProgId = "PageSmith.Document";
    public const string AppName = "PageSmith";

    /// <summary>Key under HKEY_CURRENT_USER that holds everything; tests use a scratch key instead.</summary>
    internal static string Root { get; set; } = "Software";

    private static string Classes => $@"{Root}\Classes";
    private static string Capabilities => $@"{Root}\{AppName}\Capabilities";

    public static string Command(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>True when PDFs are registered to open with this copy of PageSmith.</summary>
    public static bool IsRegistered(string exe)
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
        return string.Equals(command?.GetValue("") as string, Command(exe), StringComparison.OrdinalIgnoreCase);
    }

    public static void Register(string exe)
    {
        var icon = $"\"{exe}\",0";
        using (var prog = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            prog.SetValue("", "PDF Document");
            prog.SetValue("FriendlyTypeName", "PDF Document");
            using (var key = prog.CreateSubKey("DefaultIcon")) key.SetValue("", icon);
            using (var key = prog.CreateSubKey(@"shell\open\command")) key.SetValue("", Command(exe));
            using (var key = prog.CreateSubKey("Application"))
            {
                key.SetValue("ApplicationName", AppName);
                key.SetValue("ApplicationIcon", icon);
            }
        }
        using (var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\.pdf\OpenWithProgids")) key.SetValue(ProgId, "");
        using (var app = Registry.CurrentUser.CreateSubKey($@"{Classes}\Applications\{Path.GetFileName(exe)}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var key = app.CreateSubKey(@"shell\open\command")) key.SetValue("", Command(exe));
            using (var key = app.CreateSubKey("SupportedTypes")) key.SetValue(".pdf", "");
        }
        using (var caps = Registry.CurrentUser.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Read, edit, convert, sign and protect PDFs on your PC, offline.");
            caps.SetValue("ApplicationIcon", icon);
            using var key = caps.CreateSubKey("FileAssociations");
            key.SetValue(".pdf", ProgId);
        }
        using (var key = Registry.CurrentUser.CreateSubKey($@"{Root}\RegisteredApplications")) key.SetValue(AppName, Capabilities);
        NotifyShell();
    }

    public static void Unregister(string exe)
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
        using (var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\.pdf\OpenWithProgids", writable: true)) key?.DeleteValue(ProgId, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\Applications\{Path.GetFileName(exe)}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        using (var app = Registry.CurrentUser.OpenSubKey($@"{Root}\{AppName}", writable: true))
        {
            if (app is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey($@"{Root}\{AppName}", throwOnMissingSubKey: false);
        }
        using (var key = Registry.CurrentUser.OpenSubKey($@"{Root}\RegisteredApplications", writable: true)) key?.DeleteValue(AppName, throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>The ms-settings link to Windows' default apps page for PageSmith, where PDFs can be set to open with it.</summary>
    public const string DefaultAppsSettings = "ms-settings:defaultapps?registeredAppUser=" + AppName;

    private static void NotifyShell()
    {
        // Tells Explorer the associations changed; not needed for the scratch key tests use.
        if (Root == "Software") SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
