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
    Gemini,
    OpenAI,
}

/// <summary>Who writes a summary: this PC (offline), or an online AI service.</summary>
public enum SummaryService
{
    OnThisPc,
    Claude,
    Gemini,
    OpenAI,
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
    public string ProtectedGeminiKey { get; set; } = "";
    public string GeminiModel { get; set; } = GeminiService.DefaultModel;
    public string ProtectedOpenAiKey { get; set; } = "";
    public string OpenAiModel { get; set; } = OpenAiService.DefaultModel;

    /// <summary>Who wrote the last summary; offered again next time.</summary>
    public SummaryService Summarizer { get; set; } = SummaryService.OnThisPc;

    public string SummaryLanguage { get; set; } = "English";

    /// <summary>Where the main window was when PageSmith last closed; null until then.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The reader's left panel: "pages", "bookmarks", or "" when closed.</summary>
    public string ReaderPanel { get; set; } = "pages";

    /// <summary>Whether the reader shows the All tools pane.</summary>
    public bool ReaderToolsPane { get; set; } = true;

    public List<string> RecentFiles { get; set; } = [];
    public List<string> FavoriteTools { get; set; } = ["merge", "split", "compress", "pdf-to-word", "edit", "sign"];

    /// <summary>The saved API key for an AI service, or the one in its usual environment variable.</summary>
    public string KeyFor(AiProvider provider)
    {
        var stored = SecretStore.Unprotect(provider switch
        {
            AiProvider.Claude => ProtectedAnthropicKey,
            AiProvider.Gemini => ProtectedGeminiKey,
            _ => ProtectedOpenAiKey,
        });
        return stored.Length > 0 ? stored : AiService.KeyFromEnvironment(provider) ?? "";
    }

    /// <summary>Whether the key was typed into Settings (rather than coming from an environment variable).</summary>
    public bool HasSavedKey(AiProvider provider) => provider switch
    {
        AiProvider.Claude => ProtectedAnthropicKey,
        AiProvider.Gemini => ProtectedGeminiKey,
        _ => ProtectedOpenAiKey,
    } != "";

    public void SetKey(AiProvider provider, string key)
    {
        var stored = string.IsNullOrWhiteSpace(key) ? "" : SecretStore.Protect(key.Trim());
        switch (provider)
        {
            case AiProvider.Claude: ProtectedAnthropicKey = stored; break;
            case AiProvider.Gemini: ProtectedGeminiKey = stored; break;
            default: ProtectedOpenAiKey = stored; break;
        }
    }

    public string ModelFor(AiProvider provider) => provider switch
    {
        AiProvider.Claude => ClaudeModel,
        AiProvider.Gemini => GeminiModel,
        _ => OpenAiModel,
    };

    public void SetModel(AiProvider provider, string model)
    {
        model = string.IsNullOrWhiteSpace(model) ? AiService.DefaultModel(provider) : model.Trim();
        switch (provider)
        {
            case AiProvider.Claude: ClaudeModel = model; break;
            case AiProvider.Gemini: GeminiModel = model; break;
            default: OpenAiModel = model; break;
        }
    }

    /// <summary>The AI service with the saved key and model; explains where to add a missing key.</summary>
    public AiService CreateAi(AiProvider provider)
    {
        var key = KeyFor(provider);
        if (!AiService.HasKey(key))
            throw new UserFacingException($"Add your {AiService.Company(provider)} API key in Settings › AI services to use {AiService.Describe(provider)}.");
        return AiService.Create(provider, key, ModelFor(provider));
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
        TranslationProvider.Claude => new AiTranslator(CreateAi(AiProvider.Claude)),
        TranslationProvider.Gemini => new AiTranslator(CreateAi(AiProvider.Gemini)),
        TranslationProvider.OpenAI => new AiTranslator(CreateAi(AiProvider.OpenAI)),
        _ => new MyMemoryTranslator(MyMemoryEmail),
    };
}
