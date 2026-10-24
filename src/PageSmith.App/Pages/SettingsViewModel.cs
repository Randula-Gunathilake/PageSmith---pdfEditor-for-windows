using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
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

    public bool UseClaudeForSummaries
    {
        get => S.UseClaudeForSummaries;
        set { S.UseClaudeForSummaries = value; Save(); }
    }

    public string KeyStatus => S.AnthropicKey.Length == 0
        ? "No key saved. Claude features stay off; everything else works."
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

    private static void Save() => AppServices.SaveSettings();
}
