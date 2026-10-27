using System.Net.Http;
using System.Text.Json;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

/// <summary>OpenAI's models, through the Responses API and the user's own API key.</summary>
public sealed class OpenAiService(string apiKey, string model = OpenAiService.DefaultModel) : AiService(model)
{
    public const string DefaultModel = "gpt-6-astra";

    private const string Endpoint = "https://api.openai.com/v1/";

    public override AiProvider Provider => AiProvider.OpenAI;

    protected override async Task<string> CompleteAsync(string instructions, string prompt, StringListAnswer? answer, Depth depth, string feature, CancellationToken ct)
    {
        await Connectivity.EnsureOnlineAsync(feature, ct).ConfigureAwait(false);
        using var doc = await AiHttp.SendAsync(HttpMethod.Post, $"{Endpoint}responses", RequestBody(Model, instructions, prompt, answer),
            h => h.Authorization = new("Bearer", apiKey), Name, feature, ct).ConfigureAwait(false);
        return ReadAnswer(doc.RootElement);
    }

    internal static Dictionary<string, object> RequestBody(string model, string instructions, string prompt, StringListAnswer? answer)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["instructions"] = instructions,
            ["input"] = prompt,
            // Documents are private: OpenAI need not keep the response for later retrieval.
            ["store"] = false,
        };
        if (answer != null)
        {
            body["text"] = new
            {
                format = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["name"] = answer.Property,
                    ["strict"] = true,
                    ["schema"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object> { [answer.Property] = new { type = "array", items = new { type = "string" } } },
                        ["required"] = new[] { answer.Property },
                        ["additionalProperties"] = false,
                    },
                },
            };
        }
        return body;
    }

    /// <summary>The text of the message in the output, or a message saying why there is none.</summary>
    internal static string ReadAnswer(JsonElement root)
    {
        var text = new StringBuilder();
        string? refusal = null;
        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                // Reasoning items come first; only the message is the answer.
                if (!item.TryGetProperty("type", out var type) || type.GetString() != "message" || !item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                {
                    switch (part.TryGetProperty("type", out var kind) ? kind.GetString() : null)
                    {
                        case "output_text" when part.TryGetProperty("text", out var t):
                            text.Append(t.GetString());
                            break;
                        case "refusal" when part.TryGetProperty("refusal", out var r):
                            refusal = r.GetString();
                            break;
                    }
                }
            }
        }
        if (refusal != null && text.Length == 0)
            throw new UserFacingException($"OpenAI declined to process this document ({refusal}).");
        if (root.TryGetProperty("status", out var status) && status.GetString() == "incomplete")
        {
            var why = root.TryGetProperty("incomplete_details", out var details) && details.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
            throw new UserFacingException(why == "max_output_tokens"
                ? "OpenAI's answer was cut off. Try fewer pages."
                : $"OpenAI stopped before finishing{(why != null ? $" ({why})" : "")}. Try again.");
        }
        if (string.IsNullOrWhiteSpace(text.ToString())) throw new UserFacingException("OpenAI returned an empty answer. Try again.");
        return text.ToString();
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        await Connectivity.EnsureOnlineAsync(Name, ct).ConfigureAwait(false);
        using var doc = await AiHttp.SendAsync(HttpMethod.Get, $"{Endpoint}models", null,
            h => h.Authorization = new("Bearer", apiKey), Name, Name, ct).ConfigureAwait(false);
        return ReadModels(doc.RootElement);
    }

    /// <summary>Text models ("gpt-…" and "o…" reasoning models), without audio, image, embedding and moderation ones.</summary>
    internal static IReadOnlyList<string> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return [];
        string[] notText = ["image", "realtime", "audio", "transcribe", "tts", "embedding", "whisper", "dall-e", "moderation", "live", "search", "codex"];
        return data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "")
            .Where(id => (id.StartsWith("gpt-", StringComparison.Ordinal) || id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])) &&
                         !notText.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
