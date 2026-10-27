using System.Net.Http;
using System.Text.Json;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

/// <summary>Google's Gemini, through the Gemini API (generateContent) and the user's own API key.</summary>
public sealed class GeminiService(string apiKey, string model = GeminiService.DefaultModel) : AiService(Normalize(model))
{
    public const string DefaultModel = "gemini-3.8-flash";

    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/";

    public override AiProvider Provider => AiProvider.Gemini;

    /// <summary>Model names may be given as the API lists them ("models/…").</summary>
    private static string Normalize(string model) => model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;

    protected override async Task<string> CompleteAsync(string instructions, string prompt, StringListAnswer? answer, Depth depth, string feature, CancellationToken ct)
    {
        await Connectivity.EnsureOnlineAsync(feature, ct).ConfigureAwait(false);
        using var doc = await AiHttp.SendAsync(HttpMethod.Post, $"{Endpoint}models/{Uri.EscapeDataString(Model)}:generateContent",
            RequestBody(instructions, prompt, answer), h => h.Add("x-goog-api-key", apiKey), Name, feature, ct).ConfigureAwait(false);
        return ReadAnswer(doc.RootElement, answer != null);
    }

    internal static Dictionary<string, object> RequestBody(string instructions, string prompt, StringListAnswer? answer)
    {
        var body = new Dictionary<string, object>
        {
            ["systemInstruction"] = new { parts = new[] { new { text = instructions } } },
            ["contents"] = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
        };
        if (answer != null)
        {
            body["generationConfig"] = new Dictionary<string, object>
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = new Dictionary<string, object>
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new Dictionary<string, object> { [answer.Property] = new { type = "ARRAY", items = new { type = "STRING" } } },
                    ["required"] = new[] { answer.Property },
                },
            };
        }
        return body;
    }

    /// <summary>The text of the first candidate, or a message saying why there is none.</summary>
    internal static string ReadAnswer(JsonElement root, bool json)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var blocked))
            throw new UserFacingException($"Gemini declined to process this document ({blocked.GetString()}).");
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new UserFacingException("Gemini returned no answer. Try again.");
        var candidate = candidates[0];
        var finish = candidate.TryGetProperty("finishReason", out var reason) ? reason.GetString() : null;
        var text = new StringBuilder();
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                // Thought summaries are not part of the answer.
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
            }
        }
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION")
            throw new UserFacingException($"Gemini declined to process this document ({finish}).");
        if (finish == "MAX_TOKENS" && json)
            throw new UserFacingException("Gemini's answer was cut off. Try fewer pages.");
        if (text.Length == 0 || string.IsNullOrWhiteSpace(text.ToString()))
            throw new UserFacingException("Gemini returned an empty answer. Try again.");
        return text.ToString();
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        await Connectivity.EnsureOnlineAsync(Name, ct).ConfigureAwait(false);
        using var doc = await AiHttp.SendAsync(HttpMethod.Get, $"{Endpoint}models?pageSize=1000", null,
            h => h.Add("x-goog-api-key", apiKey), Name, Name, ct).ConfigureAwait(false);
        return ReadModels(doc.RootElement);
    }

    /// <summary>Models that write text (generateContent), without the speech, image and embedding ones.</summary>
    internal static IReadOnlyList<string> ReadModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models)) return [];
        string[] notText = ["embedding", "tts", "image", "live", "transcribe", "robotics", "aqa", "imagen", "veo", "banana"];
        return models.EnumerateArray()
            .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                        methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
            .Select(m => Normalize(m.GetProperty("name").GetString() ?? ""))
            .Where(name => name.Length > 0 && !notText.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
    }
}
