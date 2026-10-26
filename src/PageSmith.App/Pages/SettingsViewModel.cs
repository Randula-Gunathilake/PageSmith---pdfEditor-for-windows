using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Net;
using PageSmith.Core.Ocr;
using PageSmith.Core.Office;
using PageSmith.Core.Tools.Convert;

namespace PageSmith.App.Pages;

/// <summary>Edits <see cref="AppSettings"/>; every change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static AppSettings S => AppServices.Settings;

    public ThemePreference Theme
    {
        get => S.Theme;
        set { S.Theme = value; Save(); AppServices.ApplyTheme(); }
    }

    public OutputLocation Output
    {
        get => S.Output;
        set { S.Output = value; Save(); }
    }

    public string OutputFolder
    {
        get => S.OutputFolder;
        set { S.OutputFolder = value; Save(); }
    }

    public bool OpenFolderAfterSaving
    {
        get => S.OpenFolderAfterSaving;
        set { S.OpenFolderAfterSaving = value; Save(); }
    }

    public ConversionEngine OfficeEngine
    {
        get => S.OfficeEngine;
        set { S.OfficeEngine = value; Save(); }
    }

    public bool UseWordForPdfToWord
    {
        get => S.PdfToWordEngine != ConversionEngine.BuiltIn;
        set { S.PdfToWordEngine = value ? ConversionEngine.Auto : ConversionEngine.BuiltIn; Save(); }
    }

    public IReadOnlyList<OcrLanguage> OcrLanguages { get; } = OcrService.AvailableLanguages();

    public OcrLanguage? OcrLanguage
    {
        get => OcrLanguages.FirstOrDefault(l => l.Tag == S.OcrLanguage) ?? OcrLanguages.FirstOrDefault();
        set { S.OcrLanguage = value?.Tag; Save(); }
    }

    public TranslationProvider Translator
    {
        get => S.Translator;
        set { S.Translator = value; Save(); }
    }

    public string MyMemoryEmail
    {
        get => S.MyMemoryEmail;
        set { S.MyMemoryEmail = value; Save(); }
    }

    public string LibreTranslateUrl
    {
        get => S.LibreTranslateUrl;
        set { S.LibreTranslateUrl = value; Save(); }
    }

    public string LibreTranslateKey
    {
        get => S.LibreTranslateKey;
        set { S.LibreTranslateKey = value; Save(); }
    }

    public string AnthropicKey
    {
        get => S.AnthropicKey;
        set { S.AnthropicKey = value; Save(); OnPropertyChanged(nameof(KeyStatus)); }
    }

    public string ClaudeModel
    {
        get => S.ClaudeModel;
        set { S.ClaudeModel = string.IsNullOrWhiteSpace(value) ? Core.Intelligence.ClaudeService.DefaultModel : value.Trim(); Save(); }
    }

    public string KeyStatus => S.AnthropicKey.Length == 0
        ? "No key saved. Claude translation stays off; everything else works."
        : "A key is saved for this Windows account (encrypted with Windows Data Protection).";

    public string OfficeStatus
    {
        get
        {
            var apps = new[] { OfficeApp.Word, OfficeApp.Excel, OfficeApp.PowerPoint }.Where(OfficeDetection.IsMicrosoftOfficeAppInstalled).ToList();
            var ms = apps.Count > 0 ? $"Microsoft {string.Join(", ", apps)} found." : "Microsoft Office not found.";
            var lo = LibreOffice.IsInstalled ? " LibreOffice found." : " LibreOffice not found.";
            return ms + lo + " The built-in converter is always available.";
        }
    }

    [ObservableProperty] public partial string ConnectivityText { get; set; } = "";

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Default folder for results", InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null };
        if (dialog.ShowDialog(AppServices.Window) == true)
        {
            OutputFolder = dialog.FolderName;
            Output = OutputLocation.Folder;
            OnPropertyChanged(nameof(OutputFolder));
            OnPropertyChanged(nameof(Output));
        }
    }

    [RelayCommand]
    private async Task CheckConnectivityAsync()
    {
        ConnectivityText = "Checking…";
        ConnectivityText = await Connectivity.HasInternetAsync()
            ? "Connected to the internet. Online features are available."
            : "No internet connection. Local tools work normally; translation, web pages and Claude need a connection.";
    }

    [RelayCommand]
    private static void OpenNetworkSettings() => AppServices.OpenUri("ms-settings:network-status");

    [RelayCommand]
    private static void OpenLanguageSettings() => AppServices.OpenUri("ms-settings:regionlanguage");

    [RelayCommand]
    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        AppServices.ShowInFolder(AppSettings.Folder);
    }

    [RelayCommand]
    private static void GetAnthropicKey() => AppServices.OpenUri("https://console.anthropic.com/settings/keys");

    // ------------------------------------------------------------------ opening PDFs

    private static string Exe => Environment.ProcessPath ?? "";

    public bool IsPdfAppRegistered => PdfAppRegistration.IsRegistered(Exe);

    public string PdfAppStatus => IsPdfAppRegistered
        ? "PageSmith is in the “Open with” list for PDFs. To open every PDF with it, choose it in Windows’ Default apps."
        : "Add PageSmith to the “Open with” list for PDFs, then choose it as the default PDF app in Windows’ Default apps.";

    /// <summary>Registers PageSmith for PDFs and opens Windows' default apps page, where the user makes it the default.</summary>
    [RelayCommand]
    private void RegisterPdfApp()
    {
        try
        {
            PdfAppRegistration.Register(Exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppServices.Notify("Couldn't register PageSmith", ex.Message, Wpf.Ui.Controls.ControlAppearance.Danger, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24);
            return;
        }
        OnPropertyChanged(nameof(IsPdfAppRegistered));
        OnPropertyChanged(nameof(PdfAppStatus));
        AppServices.OpenUri(PdfAppRegistration.DefaultAppsSettings);
    }

    [RelayCommand]
    private void UnregisterPdfApp()
    {
        try
        {
            PdfAppRegistration.Unregister(Exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AppServices.Notify("Couldn't remove PageSmith", ex.Message, Wpf.Ui.Controls.ControlAppearance.Danger, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24);
            return;
        }
        OnPropertyChanged(nameof(IsPdfAppRegistered));
        OnPropertyChanged(nameof(PdfAppStatus));
    }

    private static void Save() => AppServices.SaveSettings();
}
