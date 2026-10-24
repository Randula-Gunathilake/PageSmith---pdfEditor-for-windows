using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

/// <summary>
/// Optional online AI features through Anthropic's Claude API (the user's own API key):
/// abstractive summaries and high-quality translation. Every call checks connectivity first.
/// </summary>
public sealed class ClaudeService(string apiKey, string model = ClaudeService.DefaultModel)
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public static bool HasKey(string? apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<string> SummarizeAsync(string documentText, SummaryLength length, string outputLanguage, CancellationToken ct = default)
    {
        var words = length switch { SummaryLength.Short => "about 100 words", SummaryLength.Long => "about 450 words", _ => "about 220 words" };
        var system = "You summarize documents for busy readers. Write in clear, neutral prose. " +
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
        return await SendAsync(system, prompt, null, Effort.Medium, "AI summaries", ct).ConfigureAwait(false);
    }

    /// <summary>Translates segments in one request; the answer has exactly one translation per segment, in order.</summary>
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (segments.Count == 0) return [];
        var system = "You are a professional translator. Translate faithfully and fluently, keeping names, numbers, " +
                     "units, codes, URLs and email addresses unchanged. Keep line breaks inside a segment. " +
                     "If a segment is already in the target language or is not translatable (a number, a code), return it unchanged.";
        var from = string.IsNullOrEmpty(sourceLanguage) ? "the language it is written in" : sourceLanguage;
        var prompt = $"Translate each segment from {from} into {targetLanguage}. " +
                     $"Return a JSON object whose \"translations\" array has exactly {segments.Count} strings, one per segment, in the same order.\n\n" +
                     JsonSerializer.Serialize(new { segments });
        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new { translations = new { type = "array", items = new { type = "string" } } }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "translations" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
        var json = await SendAsync(system, prompt, schema, Effort.Low, "Translation with Claude", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("translations").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (result.Count != segments.Count)
            throw new UserFacingException("Claude returned a different number of translated segments than were sent. Try again.");
        return result;
    }

    private async Task<string> SendAsync(string system, string prompt, Dictionary<string, JsonElement>? schema, Effort effort, string feature, CancellationToken ct)
    {
        await Connectivity.EnsureOnlineAsync(feature, ct).ConfigureAwait(false);
        try
        {
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 16000,
                System = system,
                Messages = [new BetaMessageParam(new BetaUserMessageParam { Content = new BetaMessageContentParam(prompt, null) }, null)],
                OutputConfig = schema == null
                    ? new BetaOutputConfig { Effort = effort }
                    : new BetaOutputConfig { Effort = effort, Format = new BetaJsonOutputFormat { Schema = schema } },
                // If a safety classifier declines, the server retries on its default fallback model.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new BetaFallbacksParam(new Default(), null),
            };
            var response = await _client.Beta.Messages.Create(parameters, ct).ConfigureAwait(false);
            if (response.StopReason == "refusal")
                throw new UserFacingException("Claude declined to process this document.");
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            if (string.IsNullOrWhiteSpace(text)) throw new UserFacingException("Claude returned an empty answer. Try again.");
            return text;
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new UserFacingException("The Anthropic API key was rejected. Check it in Settings › AI services.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new UserFacingException("The Anthropic API rate limit was reached. Wait a moment and retry.", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new OfflineException(feature, ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new UserFacingException("The Claude service is temporarily unavailable. Retry in a few minutes.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new UserFacingException($"The Claude API returned an error: {ex.Message}", ex);
        }
    }
}
