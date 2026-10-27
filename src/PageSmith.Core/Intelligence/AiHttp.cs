using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PageSmith.Core.Net;

namespace PageSmith.Core.Intelligence;

/// <summary>Sends JSON requests to AI services over HTTP and turns their errors into messages for the user.</summary>
internal static class AiHttp
{
    /// <summary>Long documents and thinking models can take minutes.</summary>
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<JsonDocument> SendAsync(HttpMethod method, string url, object? body, Action<HttpRequestHeaders> authorize, string service, string feature, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        authorize(request.Headers);
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OfflineException(feature, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UserFacingException($"{service} took too long to answer. Try again, or pick a faster model in Settings › AI services.", ex);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Explain((int)response.StatusCode, text, service);
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new UserFacingException($"{service} sent an answer PageSmith couldn't read. Try again.", ex);
            }
        }
    }

    /// <summary>A message for an error response; both Google's and OpenAI's APIs put the reason in error.message.</summary>
    internal static UserFacingException Explain(int status, string body, string service)
    {
        var message = ErrorMessage(body);
        var detail = message.Length > 0 ? $" ({message})" : "";
        var keyProblem = status is 401 or 403 ||
                         status == 400 && message.Contains("API key", StringComparison.OrdinalIgnoreCase);
        if (keyProblem) return new UserFacingException($"The {service} API key was rejected. Check it in Settings › AI services.{detail}");
        return status switch
        {
            404 => new UserFacingException($"{service} doesn't offer the model picked in Settings › AI services. Choose another one.{detail}"),
            429 => new UserFacingException($"{service} says the rate limit or your quota was reached. Wait a moment and retry, or check your plan.{detail}"),
            >= 500 => new UserFacingException($"The {service} service is temporarily unavailable. Retry in a few minutes.{detail}"),
            _ => new UserFacingException($"The {service} API returned an error{detail}."),
        };
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "";
                if (error.TryGetProperty("message", out var message)) return message.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the start of the text.
        }
        return body.Length > 200 ? body[..200] : body.Trim();
    }
}
