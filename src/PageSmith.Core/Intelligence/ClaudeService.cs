using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

/// <summary>Claude, through Anthropic's official SDK and the user's own API key.</summary>
public sealed class ClaudeService(string apiKey, string model = ClaudeService.DefaultModel) : AiService(model)
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public override AiProvider Provider => AiProvider.Claude;

    protected override async Task<string> CompleteAsync(string instructions, string prompt, StringListAnswer? answer, Depth depth, string feature, CancellationToken ct)
    {
        await Connectivity.EnsureOnlineAsync(feature, ct).ConfigureAwait(false);
        try
        {
            var effort = depth == Depth.Light ? Effort.Low : Effort.Medium;
            BetaJsonOutputFormat? format = answer == null ? null : new BetaJsonOutputFormat { Schema = Schema(answer) };
            var parameters = new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 16000,
                System = instructions,
                Messages = [new BetaMessageParam(new BetaUserMessageParam { Content = new BetaMessageContentParam(prompt, null) }, null)],
                OutputConfig = SupportsEffort(Model)
                    ? new BetaOutputConfig { Effort = effort, Format = format }
                    : new BetaOutputConfig { Format = format },
            };
            if (SupportsFallbacks(Model))
            {
                // If a safety classifier declines, the server retries on its default fallback model.
                parameters = parameters with
                {
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new BetaFallbacksParam(new Default(), null),
                };
            }
            var response = await _client.Beta.Messages.Create(parameters, ct).ConfigureAwait(false);
            if (response.StopReason == "refusal")
                throw new UserFacingException("Claude declined to process this document.");
            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            if (string.IsNullOrWhiteSpace(text)) throw new UserFacingException("Claude returned an empty answer. Try again.");
            return text;
        }
        catch (AnthropicApiException ex)
        {
            throw Explain(ex, feature);
        }
        catch (AnthropicIOException ex)
        {
            throw new OfflineException(feature, ex);
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        await Connectivity.EnsureOnlineAsync(Name, ct).ConfigureAwait(false);
        try
        {
            var page = await _client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (AnthropicApiException ex)
        {
            throw Explain(ex, Name);
        }
        catch (AnthropicIOException ex)
        {
            throw new OfflineException(Name, ex);
        }
    }

    private static UserFacingException Explain(AnthropicApiException ex, string feature) => ex switch
    {
        AnthropicUnauthorizedException => new UserFacingException("The Anthropic API key was rejected. Check it in Settings › AI services.", ex),
        AnthropicNotFoundException => new UserFacingException("Anthropic doesn't offer the Claude model picked in Settings › AI services. Choose another one.", ex),
        AnthropicRateLimitException => new UserFacingException("The Anthropic API rate limit was reached. Wait a moment and retry.", ex),
        Anthropic5xxException => new UserFacingException("The Claude service is temporarily unavailable. Retry in a few minutes.", ex),
        _ => new UserFacingException($"The Claude API returned an error: {ex.Message}", ex),
    };

    private static Dictionary<string, JsonElement> Schema(StringListAnswer answer) => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [answer.Property] = new { type = "array", items = new { type = "string" } } }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { answer.Property }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    /// <summary>Effort is rejected by Claude 3 models, Haiku 4.5 and Sonnet 4.5 and older 4.x models.</summary>
    private static bool SupportsEffort(string model) =>
        !new[] { "claude-3", "claude-haiku-4", "claude-sonnet-4-5", "claude-sonnet-4-0", "claude-sonnet-4-2", "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-2" }
            .Any(prefix => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The server-side refusal fallback ("default" form) is for the current Opus, Sonnet and Fable models.</summary>
    private static bool SupportsFallbacks(string model) =>
        model is "claude-opus-5-5" or "claude-opus-5" or "claude-sonnet-5-5" or "claude-fable-5-1";
}
