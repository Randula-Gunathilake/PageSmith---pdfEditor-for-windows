using System.Text.Json;
using PageSmith.Core.Common;
using PageSmith.Core.Intelligence;

namespace PageSmith.Tests;

/// <summary>
/// The AI services' requests and answers, checked offline against the documented JSON shapes. No request
/// is sent: these tests need neither the internet nor API keys.
/// </summary>
public class AiServiceTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData(AiProvider.Claude, typeof(ClaudeService), ClaudeService.DefaultModel)]
    [InlineData(AiProvider.Gemini, typeof(GeminiService), GeminiService.DefaultModel)]
    [InlineData(AiProvider.OpenAI, typeof(OpenAiService), OpenAiService.DefaultModel)]
    public void Each_provider_is_created_with_its_default_model(AiProvider provider, Type type, string model)
    {
        var ai = AiService.Create(provider, " key ", null);
        Assert.IsType(type, ai);
        Assert.Equal(model, ai.Model);
        Assert.Equal(provider, ai.Provider);
        Assert.Equal("my-model", AiService.Create(provider, "key", " my-model ").Model);
    }

    [Fact]
    public void Gemini_model_names_may_carry_the_models_prefix() =>
        Assert.Equal("gemini-3.8-flash", new GeminiService("key", "models/gemini-3.8-flash").Model);

    [Fact]
    public void Lists_are_read_from_json_answers_even_inside_a_code_fence()
    {
        Assert.Equal(["Hola", "Mundo"], AiService.ReadStringList("""{"translations":["Hola","Mundo"]}""", "translations"));
        Assert.Equal(["Hola"], AiService.ReadStringList("```json\n{\"translations\":[\"Hola\"]}\n```", "translations"));
        Assert.Throws<UserFacingException>(() => AiService.ReadStringList("not json", "translations"));
    }

    [Fact]
    public void Gemini_requests_ask_for_json_in_the_documented_shape()
    {
        var body = Json(GeminiService.RequestBody("Be brief.", "Translate this.", new StringListAnswer("translations")));
        Assert.Equal("Be brief.", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var content = body.GetProperty("contents")[0];
        Assert.Equal("user", content.GetProperty("role").GetString());
        Assert.Equal("Translate this.", content.GetProperty("parts")[0].GetProperty("text").GetString());
        var config = body.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("ARRAY", config.GetProperty("responseSchema").GetProperty("properties").GetProperty("translations").GetProperty("type").GetString());
        // Plain text requests carry no generation config.
        Assert.False(Json(GeminiService.RequestBody("a", "b", null)).TryGetProperty("generationConfig", out _));
    }

    [Fact]
    public void Gemini_answers_skip_thoughts_and_explain_blocks()
    {
        var ok = Parse("""{"candidates":[{"content":{"parts":[{"text":"thinking…","thought":true},{"text":"## Summary\n"},{"text":"Done."}]},"finishReason":"STOP"}]}""");
        Assert.Equal("## Summary\nDone.", GeminiService.ReadAnswer(ok, json: false));

        var blocked = Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""");
        Assert.Contains("declined", Assert.Throws<UserFacingException>(() => GeminiService.ReadAnswer(blocked, false)).Message);
        var unsafeAnswer = Parse("""{"candidates":[{"content":{"parts":[]},"finishReason":"SAFETY"}]}""");
        Assert.Contains("declined", Assert.Throws<UserFacingException>(() => GeminiService.ReadAnswer(unsafeAnswer, false)).Message);
        var cut = Parse("""{"candidates":[{"content":{"parts":[{"text":"{\"translations\":[\"Ho"}]},"finishReason":"MAX_TOKENS"}]}""");
        Assert.Contains("cut off", Assert.Throws<UserFacingException>(() => GeminiService.ReadAnswer(cut, json: true)).Message);
    }

    [Fact]
    public void Gemini_model_list_keeps_text_models()
    {
        var list = Parse("""
            {"models":[
              {"name":"models/gemini-3.8-flash","supportedGenerationMethods":["generateContent","countTokens"]},
              {"name":"models/gemini-4-argon","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.8-flash-tts","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-embedding-2-preview","supportedGenerationMethods":["embedContent"]}
            ]}
            """);
        Assert.Equal(["gemini-3.8-flash", "gemini-4-argon"], GeminiService.ReadModels(list));
    }

    [Fact]
    public void OpenAI_requests_use_the_responses_api_with_a_strict_schema_and_no_storage()
    {
        var body = Json(OpenAiService.RequestBody("gpt-6-astra", "Be brief.", "Translate this.", new StringListAnswer("translations")));
        Assert.Equal("gpt-6-astra", body.GetProperty("model").GetString());
        Assert.Equal("Be brief.", body.GetProperty("instructions").GetString());
        Assert.Equal("Translate this.", body.GetProperty("input").GetString());
        Assert.False(body.GetProperty("store").GetBoolean());
        var format = body.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.False(Json(OpenAiService.RequestBody("m", "a", "b", null)).TryGetProperty("text", out _));
    }

    [Fact]
    public void OpenAI_answers_come_from_the_message_after_reasoning()
    {
        var ok = Parse("""
            {"status":"completed","output":[
              {"type":"reasoning","summary":[]},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"translations\":[\"Hola\"]}"}]}
            ]}
            """);
        Assert.Equal("""{"translations":["Hola"]}""", OpenAiService.ReadAnswer(ok));

        var refused = Parse("""{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"I can't help with that."}]}]}""");
        Assert.Contains("declined", Assert.Throws<UserFacingException>(() => OpenAiService.ReadAnswer(refused)).Message);
        var cut = Parse("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""");
        Assert.Contains("cut off", Assert.Throws<UserFacingException>(() => OpenAiService.ReadAnswer(cut)).Message);
    }

    [Fact]
    public void OpenAI_model_list_keeps_text_models()
    {
        var list = Parse("""{"data":[{"id":"gpt-6-luna"},{"id":"gpt-6-astra"},{"id":"gpt-image-2.5-flare"},{"id":"gpt-realtime-2"},{"id":"o3"},{"id":"text-embedding-3-small"},{"id":"omni-moderation-latest"}]}""");
        Assert.Equal(["gpt-6-astra", "gpt-6-luna", "o3"], OpenAiService.ReadModels(list));
    }

    [Theory]
    [InlineData(401, """{"error":{"message":"Incorrect API key provided."}}""", "API key was rejected")]
    [InlineData(400, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""", "API key was rejected")]
    [InlineData(404, """{"error":{"message":"models/nope is not found"}}""", "doesn't offer the model")]
    [InlineData(429, """{"error":{"message":"You exceeded your current quota."}}""", "rate limit or your quota")]
    [InlineData(503, "Service Unavailable", "temporarily unavailable")]
    [InlineData(400, """{"error":{"message":"Invalid value for 'input'."}}""", "returned an error (Invalid value for 'input'.)")]
    public void Http_errors_become_messages_that_say_what_to_do(int status, string body, string expected) =>
        Assert.Contains(expected, AiHttp.Explain(status, body, "Gemini").Message);
}
