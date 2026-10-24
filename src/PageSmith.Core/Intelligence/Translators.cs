using System.Net;
using System.Net.Http;
using System.Text.Json;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

public sealed record LanguageInfo(string Code, string Name);

/// <summary>Translates batches of text segments. Implementations are online services.</summary>
public interface ITranslator
{
    string Name { get; }

    /// <summary>Largest number of characters to send in one batch.</summary>
    int MaxBatchCharacters { get; }

    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct);
}

public static class Languages
{
    public static readonly IReadOnlyList<LanguageInfo> All =
    [
        new("ar", "Arabic"), new("bn", "Bengali"), new("bg", "Bulgarian"), new("ca", "Catalan"), new("zh", "Chinese (Simplified)"),
        new("zh-TW", "Chinese (Traditional)"), new("hr", "Croatian"), new("cs", "Czech"), new("da", "Danish"), new("nl", "Dutch"),
        new("en", "English"), new("et", "Estonian"), new("fi", "Finnish"), new("fr", "French"), new("de", "German"),
        new("el", "Greek"), new("he", "Hebrew"), new("hi", "Hindi"), new("hu", "Hungarian"), new("id", "Indonesian"),
        new("it", "Italian"), new("ja", "Japanese"), new("ko", "Korean"), new("lv", "Latvian"), new("lt", "Lithuanian"),
        new("ms", "Malay"), new("no", "Norwegian"), new("fa", "Persian"), new("pl", "Polish"), new("pt", "Portuguese"),
        new("ro", "Romanian"), new("ru", "Russian"), new("sr", "Serbian"), new("si", "Sinhala"), new("sk", "Slovak"),
        new("sl", "Slovenian"), new("es", "Spanish"), new("sw", "Swahili"), new("sv", "Swedish"), new("ta", "Tamil"),
        new("th", "Thai"), new("tr", "Turkish"), new("uk", "Ukrainian"), new("ur", "Urdu"), new("vi", "Vietnamese"),
    ];

    public static string NameOf(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code ?? "";
}

/// <summary>MyMemory (translated.net): free, no key; ~5,000 characters a day, 50,000 with an email address.</summary>
public sealed class MyMemoryTranslator(string? email = null) : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Name => "MyMemory";
    public int MaxBatchCharacters => 450;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        await Connectivity.EnsureOnlineAsync("Translation", ct).ConfigureAwait(false);
        var results = new List<string>();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                results.Add(segment);
                continue;
            }
            var source = string.IsNullOrEmpty(sourceLanguage) ? NlpText.DetectLanguage(segment) : sourceLanguage;
            if (string.Equals(source, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(segment);
                continue;
            }
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(segment)}&langpair={Uri.EscapeDataString(source)}|{Uri.EscapeDataString(targetLanguage)}";
            if (!string.IsNullOrWhiteSpace(email)) url += $"&de={Uri.EscapeDataString(email)}";
            using var response = await Send(() => Http.GetAsync(url, ct)).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("responseStatus", out var s) ? s.ToString() : "200";
            var translated = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
            if (status is "429" or "403" || translated.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("The free MyMemory translation quota for today is used up. Add your email in Settings (raises the limit) or choose LibreTranslate or Claude.");
            results.Add(WebUtility.HtmlDecode(translated));
        }
        return results;
    }

    internal static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> request)
    {
        try
        {
            var response = await request().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new UserFacingException("The translation service is busy (too many requests). Wait a moment and retry.");
            if ((int)response.StatusCode >= 500)
                throw new UserFacingException($"The translation service is unavailable ({(int)response.StatusCode}). Retry later.");
            return response;
        }
        catch (Exception ex) when (Connectivity.IsNetworkFailure(ex) && ex is not UserFacingException)
        {
            throw new OfflineException("Translation", ex);
        }
    }
}

/// <summary>Any LibreTranslate server (self-hosted or a hosted plan with an API key).</summary>
public sealed class LibreTranslateTranslator(string serverUrl, string? apiKey) : ITranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public string Name => "LibreTranslate";
    public int MaxBatchCharacters => 4000;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new UserFacingException("Enter your LibreTranslate server address in Settings › AI services.");
        await Connectivity.EnsureOnlineAsync("Translation", ct).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new
        {
            q = segments,
            source = string.IsNullOrEmpty(sourceLanguage) ? "auto" : sourceLanguage,
            target = targetLanguage,
            format = "text",
            api_key = apiKey ?? "",
        });
        var url = serverUrl.TrimEnd('/') + "/translate";
        using var response = await MyMemoryTranslator.Send(() => Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct)).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!response.IsSuccessStatusCode)
        {
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : response.ReasonPhrase;
            throw new UserFacingException($"LibreTranslate: {error}");
        }
        var translated = doc.RootElement.GetProperty("translatedText");
        return translated.ValueKind == JsonValueKind.Array
            ? translated.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [translated.GetString() ?? ""];
    }
}

/// <summary>Claude (Anthropic API key): best quality, keeps terminology consistent within a batch.</summary>
public sealed class ClaudeTranslator(string apiKey, string model = ClaudeService.DefaultModel) : ITranslator
{
    private readonly ClaudeService _claude = new(apiKey, model);

    public string Name => "Claude";
    public int MaxBatchCharacters => 12000;

    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct) =>
        _claude.TranslateAsync(segments, sourceLanguage == null ? null : Languages.NameOf(sourceLanguage), Languages.NameOf(targetLanguage), ct);
}
