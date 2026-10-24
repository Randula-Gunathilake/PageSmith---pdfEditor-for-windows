using System.IO;
using System.Text.Json;
using PageSmith.Core.Common;
using PageSmith.Core.Intelligence;
using PageSmith.Core.Net;
using PageSmith.Core.Tools.Intelligence;
using PageSmith.Core.Workflows;

namespace PageSmith.Tests;

public class IntelligenceTests
{
    private const string Article =
        "Solar power capacity grew faster than any other energy source last year. " +
        "Engineers installed record numbers of solar panels across Asia, Europe and North America. " +
        "The cost of solar panels fell by nearly twenty percent, making solar the cheapest electricity in many regions. " +
        "Grid operators warn that storage must expand to balance solar output in the evening. " +
        "Battery storage projects doubled, led by large installations in California and Australia. " +
        "Meanwhile, coal generation declined in most developed economies. " +
        "Analysts expect solar capacity to triple by the end of the decade if current policies continue. " +
        "Some regions still face permitting delays that slow new solar farms. " +
        "Governments are simplifying permits and investing in transmission lines. " +
        "The report concludes that solar and storage together will reshape electricity markets.";

    [Fact]
    public void Sentences_respect_abbreviations_and_decimals()
    {
        var s = NlpText.SplitSentences("Dr. Smith paid $3.50 for it. It worked well! Did it? Yes, e.g. this one.");
        Assert.Equal(["Dr. Smith paid $3.50 for it.", "It worked well!", "Did it?", "Yes, e.g. this one."], s);
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog and the cat.", "en")]
    [InlineData("El perro come la comida que está en la mesa de la cocina.", "es")]
    [InlineData("ශ්‍රී ලංකාව ඉන්දියන් සාගරයේ පිහිටි දූපතකි.", "si")]
    [InlineData("これは日本語の文章です。ひらがなとカタカナがあります。", "ja")]
    [InlineData("Привет, как дела? Это русский текст.", "ru")]
    public void Language_detection(string text, string expected) =>
        Assert.Equal(expected, NlpText.DetectLanguage(text));

    [Fact]
    public void Extractive_summary_picks_central_sentences()
    {
        var shortSummary = ExtractiveSummarizer.Summarize(Article, SummaryLength.Short);
        var longSummary = ExtractiveSummarizer.Summarize(Article, SummaryLength.Long);
        Assert.Contains("solar", shortSummary.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(shortSummary.Summary.Length <= longSummary.Summary.Length);
        Assert.Contains("solar", shortSummary.Keywords);
        Assert.NotEmpty(shortSummary.KeyPoints);
        Assert.Equal("en", shortSummary.Language);
        Assert.Contains("## Key points", shortSummary.ToMarkdown("Report"));
    }

    [Fact]
    public async Task Summarize_tool_writes_markdown_and_pdf()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("article.pdf", 1, body: _ => string.Join("\n", NlpText.SplitSentences(Article)));
        var md = await SummarizeTool.RunAsync(new SummarizeOptions { Input = pdf, OutputPath = files.PathFor("s.md") });
        Assert.Contains("solar", File.ReadAllText(md.OutputFiles[0]), StringComparison.OrdinalIgnoreCase);
        var asPdf = await SummarizeTool.RunAsync(new SummarizeOptions { Input = pdf, OutputPath = files.PathFor("s.pdf"), Length = SummaryLength.Long });
        Assert.Contains("Summary", OrganizeTests.PageTexts(asPdf.OutputFiles[0])[0]);
    }

    private sealed class FakeTranslator : ITranslator
    {
        public string Name => "Fake";
        public int MaxBatchCharacters => 200;

        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> segments, string? sourceLanguage, string targetLanguage, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(segments.Select(s => $"[{targetLanguage}] " + new string(s.Reverse().ToArray())).ToList());
    }

    [Fact]
    public async Task Translate_replaces_text_in_place()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("en.pdf", 2, body: i => $"Hello world from page {i}.\nThis line is translated too.");
        var output = files.PathFor("es.pdf");
        var r = await TranslateTool.RunAsync(new TranslateOptions { Input = pdf, OutputPath = output, Translator = new FakeTranslator(), TargetLanguage = "es" });
        var text = OrganizeTests.PageTexts(output)[1];
        Assert.Contains("[es]", text);
        Assert.Contains("2 egap", text); // the reversed "page 2"
        Assert.Contains("English", r.Summary);
    }

    [Fact]
    public async Task Workflow_runs_steps_in_order_and_round_trips_json()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("doc.pdf", 2, body: _ => "Write to alice@example.com for details.");
        var share = WorkflowStore.Presets.Single(w => w.Id == "preset-share");
        var result = await WorkflowRunner.RunAsync(share, [pdf], files.Dir);
        var text = OrganizeTests.PageTexts(Assert.Single(result.OutputFiles))[0];
        Assert.Contains("CONFIDENTIAL", text);
        Assert.DoesNotContain("alice@example.com", text);

        var store = new WorkflowStore(files.PathFor("wf.json"));
        var custom = new Workflow { Name = "Mine", Steps = [new RotateStep { Degrees = 180 }, new ProtectStep { ProtectedOpenPassword = SecretStore.Protect("pw") }] };
        store.Save([custom]);
        var loaded = store.Load().Single(w => w.Name == "Mine");
        Assert.IsType<RotateStep>(loaded.Steps[0]);
        Assert.Equal(180, ((RotateStep)loaded.Steps[0]).Degrees);
        Assert.Equal("pw", SecretStore.Unprotect(((ProtectStep)loaded.Steps[1]).ProtectedOpenPassword));
        Assert.DoesNotContain("\"pw\"", File.ReadAllText(files.PathFor("wf.json")));
    }

    [Fact]
    public async Task Connectivity_check_returns_quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = await Connectivity.HasInternetAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12));
    }
}
