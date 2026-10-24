using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PageSmith.Core.Common;
using PageSmith.Core.Intelligence;
using PageSmith.Core.Tools.Convert;

namespace PageSmith.App.Infrastructure;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum OutputLocation
{
    /// <summary>Next to the first input file.</summary>
    SameFolder,

    /// <summary>A fixed folder chosen in Settings.</summary>
    Folder,

    /// <summary>Ask with a Save dialog each time.</summary>
    Ask,
}

public enum TranslationProvider
{
    MyMemory,
    LibreTranslate,
    Claude,
}

/// <summary>User preferences, saved as JSON in %APPDATA%\PageSmith. Secrets are DPAPI-encrypted.</summary>
public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public OutputLocation Output { get; set; } = OutputLocation.SameFolder;
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PageSmith");
    public bool OpenFolderAfterSaving { get; set; }

    public ConversionEngine OfficeEngine { get; set; } = ConversionEngine.Auto;
    public ConversionEngine PdfToWordEngine { get; set; } = ConversionEngine.BuiltIn;
    public string? OcrLanguage { get; set; }

    public TranslationProvider Translator { get; set; } = TranslationProvider.MyMemory;
    public string MyMemoryEmail { get; set; } = "";
    public string LibreTranslateUrl { get; set; } = "";
    public string ProtectedLibreTranslateKey { get; set; } = "";
    public string ProtectedAnthropicKey { get; set; } = "";
    public string ClaudeModel { get; set; } = ClaudeService.DefaultModel;
    public bool UseClaudeForSummaries { get; set; }

    public List<string> RecentFiles { get; set; } = [];
    public List<string> FavoriteTools { get; set; } = ["merge", "split", "compress", "pdf-to-word", "edit", "sign"];

    [JsonIgnore]
    public string AnthropicKey
    {
        get
        {
            var stored = SecretStore.Unprotect(ProtectedAnthropicKey);
            return stored.Length > 0 ? stored : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        }
        set => ProtectedAnthropicKey = SecretStore.Protect(value);
    }

    [JsonIgnore]
    public string LibreTranslateKey
    {
        get => SecretStore.Unprotect(ProtectedLibreTranslateKey);
        set => ProtectedLibreTranslateKey = SecretStore.Protect(value);
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PageSmith");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // A damaged settings file falls back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public void AddRecent(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            RecentFiles.RemoveAll(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, p);
        }
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
    }

    /// <summary>The translator the user picked in Settings.</summary>
    public ITranslator CreateTranslator() => Translator switch
    {
        TranslationProvider.LibreTranslate => new LibreTranslateTranslator(LibreTranslateUrl, LibreTranslateKey),
        TranslationProvider.Claude => AnthropicKey.Length > 0
            ? new ClaudeTranslator(AnthropicKey, ClaudeModel)
            : throw new UserFacingException("Add your Anthropic API key in Settings › AI services to translate with Claude."),
        _ => new MyMemoryTranslator(MyMemoryEmail),
    };
}
