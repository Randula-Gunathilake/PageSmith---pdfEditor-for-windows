using System.Text.Json;

namespace PageSmith.Core.Intelligence;

/// <summary>The online AI services PageSmith can use with the user's own API key.</summary>
public enum AiProvider
{
    Claude,
    Gemini,
    OpenAI,
}

/// <summary>An answer asked for as JSON: an object whose one property holds a list of strings.</summary>
public sealed record StringListAnswer(string Property);

/// <summary>
/// An online AI service used with the user's own API key, for summaries and translations. Every provider
/// gets the same instructions, so results read alike whichever one is picked; a subclass only knows how to
/// send one request to its own API. Every request checks the internet connection first.
/// </summary>
public abstract class AiService(string model)
{
    /// <summary>Longest document text sent in one request (about 300,000 tokens), within every provider's limits.</summary>
    public const int MaxDocumentCharacters = 1_200_000;

    public abstract AiProvider Provider { get; }

    public string Model { get; } = model;

    public string Name => Describe(Provider);

    public static string Describe(AiProvider provider) => provider switch
    {
        AiProvider.Claude => "Claude",
        AiProvider.Gemini => "Gemini",
        _ => "OpenAI",
    };

    /// <summary>Who runs the service, i.e. where document text goes.</summary>
    public static string Company(AiProvider provider) => provider switch
    {
        AiProvider.Claude => "Anthropic",
        AiProvider.Gemini => "Google",
        _ => "OpenAI",
    };

    public static string DefaultModel(AiProvider provider) => provider switch
    {
        AiProvider.Claude => ClaudeService.DefaultModel,
        AiProvider.Gemini => GeminiService.DefaultModel,
        _ => OpenAiService.DefaultModel,
    };

    /// <summary>Where to create an API key.</summary>
    public static string KeyPage(AiProvider provider) => provider switch
    {
        AiProvider.Claude => "https://console.anthropic.com/settings/keys",
        AiProvider.Gemini => "https://aistudio.google.com/apikey",
        _ => "https://platform.openai.com/api-keys",
    };

    /// <summary>The key from the provider's usual environment variable, if one is set.</summary>
    public static string? KeyFromEnvironment(AiProvider provider) => provider switch
    {
        AiProvider.Claude => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        AiProvider.Gemini => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
        _ => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
    };

    public static bool HasKey(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    public static AiService Create(AiProvider provider, string apiKey, string? model = null)
    {
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel(provider) : model.Trim();
        return provider switch
        {
            AiProvider.Claude => new ClaudeService(apiKey.Trim(), model),
            AiProvider.Gemini => new GeminiService(apiKey.Trim(), model),
            _ => new OpenAiService(apiKey.Trim(), model),
        };
    }

    /// <summary>How much a request asks of the model: a summary needs more thought than translating segments.</summary>
    protected enum Depth
    {
        Light,
        Normal,
    }

    /// <summary>Sends one request. With <paramref name="answer"/>, the reply is JSON in that shape; otherwise plain text.</summary>
    protected abstract Task<string> CompleteAsync(string instructions, string prompt, StringListAnswer? answer, Depth depth, string feature, CancellationToken ct);

    /// <summary>The models this key can use, for picking one in Settings.</summary>
    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>A tiny request that shows whether the key and model work.</summary>
    public Task<string> TestAsync(CancellationToken ct = default) =>
        CompleteAsync("Answer with a single word.", "Reply with the word OK.", null, Depth.Light, Name, ct);

    /// <summary>A Markdown summary with "Summary" and "Key points" sections, in <paramref name="outputLanguage"/>.</summary>
    public Task<string> SummarizeAsync(string documentText, SummaryLength length, string outputLanguage, CancellationToken ct = default)
    {
        if (documentText.Length > MaxDocumentCharacters)
            throw new UserFacingException($"This document is too long to summarize with {Name} in one go. Pick a range of pages and try again.");
        var words = length switch { SummaryLength.Short => "about 100 words", SummaryLength.Long => "about 450 words", _ => "about 220 words" };
        var instructions = "You summarize documents for busy readers. Write in clear, neutral prose. " +
                           "Use only information in the document; do not add facts or opinions.";
        var prompt = $"""
            Summarize the document below in {outputLanguage}.

            Format the answer in Markdown with exactly these sections:
            ## Summary
            A summary of {words}.
            ## Key points
            3 to 7 bullet points with the most important facts, decisions, figures and dates.

            <document>
            {documentText}
            </document>
            """;
        return CompleteAsync(instructions, prompt, null, Depth.Normal, $"Summaries with {Name}", ct);
    }

    /// <summary>Translates segments in one request; the answer has exactly one translation per segment, in order.</summary>
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (segments.Count == 0) return [];
        var instructions = "You are a professional translator. Translate faithfully and fluently, keeping names, numbers, " +
                           "units, codes, URLs and email addresses unchanged. Keep line breaks inside a segment. " +
                           "If a segment is already in the target language or is not translatable (a number, a code), return it unchanged.";
        var from = string.IsNullOrEmpty(sourceLanguage) ? "the language it is written in" : sourceLanguage;
        var prompt = $"Translate each segment from {from} into {targetLanguage}. " +
                     $"Return a JSON object whose \"translations\" array has exactly {segments.Count} strings, one per segment, in the same order.\n\n" +
                     JsonSerializer.Serialize(new { segments });
        var json = await CompleteAsync(instructions, prompt, new StringListAnswer("translations"), Depth.Light, $"Translation with {Name}", ct).ConfigureAwait(false);
        var result = ReadStringList(json, "translations");
        if (result.Count != segments.Count)
            throw new UserFacingException($"{Name} returned a different number of translated segments than were sent. Try again.");
        return result;
    }

    /// <summary>Reads the list from a JSON answer, tolerating a Markdown code fence around it.</summary>
    internal static IReadOnlyList<string> ReadStringList(string json, string property)
    {
        var text = json.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[(start + 1)..end];
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty(property).EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UserFacingException("The AI service answered in an unexpected format. Try again.", ex);
        }
    }
}
