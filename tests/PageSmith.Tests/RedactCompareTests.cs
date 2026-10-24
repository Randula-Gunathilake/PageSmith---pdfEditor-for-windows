using System.IO;
using System.Windows;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Security;

namespace PageSmith.Tests;

public class RedactCompareTests
{
    [Theory]
    [InlineData("the quick brown fox", "the quick red fox jumps")]
    [InlineData("", "a b c")]
    [InlineData("a b c", "")]
    [InlineData("a b c d e f g", "x a c d y f g z")]
    [InlineData("same words here", "same words here")]
    public void Diff_reconstructs_new_sequence(string oldText, string newText)
    {
        var a = oldText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var segments = WordDiff.Diff(a, b);
        var rebuilt = new List<string>();
        var oldUsed = 0;
        foreach (var s in segments)
        {
            if (s.Op != DiffOp.Insert) oldUsed += s.OldLength;
            if (s.Op != DiffOp.Delete) rebuilt.AddRange(b.Skip(s.NewStart).Take(s.NewLength));
            if (s.Op == DiffOp.Equal) Assert.Equal(a.Skip(s.OldStart).Take(s.OldLength), b.Skip(s.NewStart).Take(s.NewLength));
        }
        Assert.Equal(b, rebuilt);
        Assert.Equal(a.Length, oldUsed);
    }

    [Fact]
    public void Diff_handles_long_documents_quickly()
    {
        var rnd = new Random(7);
        var a = Enumerable.Range(0, 20000).Select(_ => "w" + rnd.Next(500)).ToList();
        var b = a.ToList();
        for (var i = 0; i < 400; i++) b[rnd.Next(b.Count)] = "changed" + i;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segments = WordDiff.Diff(a, b);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.InRange(segments.Count(s => s.Op == DiffOp.Insert), 300, 400);
    }

    [Fact]
    public void Luhn_accepts_only_valid_cards()
    {
        Assert.True(RedactTool.IsLuhnValid("4111 1111 1111 1111"));
        Assert.False(RedactTool.IsLuhnValid("4111 1111 1111 1112"));
    }

    [Fact]
    public async Task Redaction_removes_text_and_keeps_the_rest_searchable()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("pii.pdf", 2, body: i => i == 1
            ? "Contact: jane.doe@example.com or +94 77 123 4567\nCard 4111 1111 1111 1111 expires soon\nNothing to hide here"
            : "Second page stays untouched");
        var marks = RedactTool.FindPatterns(src, [RedactionPattern.Email, RedactionPattern.CreditCard, RedactionPattern.Phone]);
        Assert.Contains(marks, m => m.MatchedText == "jane.doe@example.com");
        Assert.Contains(marks, m => m.MatchedText!.Replace(" ", "") == "4111111111111111");
        Assert.Contains(marks, m => m.Reason == "Phone number");
        var manual = RedactTool.FindText(src, "expires", wholeWord: true);
        Assert.Single(manual);

        var output = files.PathFor("redacted.pdf");
        await RedactTool.RunAsync(new RedactOptions { Input = src, OutputPath = output, Marks = [.. marks, .. manual] });
        var texts = OrganizeTests.PageTexts(output);
        Assert.DoesNotContain("jane.doe", texts[0]);
        Assert.DoesNotContain("4111", texts[0]);
        Assert.DoesNotContain("expires", texts[0]);
        Assert.Contains("Nothing to hide here", texts[0]);
        Assert.Contains("Second page stays untouched", texts[1]);

        // The box is black in the rendered page.
        var mark = marks.First(m => m.MatchedText == "jane.doe@example.com");
        using var renderer = await PdfRenderer.OpenAsync(output);
        var px = ImageIO.ToPixels(await renderer.RenderAsync(0, 72));
        var cx = (int)(mark.Bounds.X + mark.Bounds.Width / 2);
        var cy = (int)(mark.Bounds.Y + mark.Bounds.Height / 2);
        Assert.True(px.Pixels[(cy * px.Width + cx) * 4] < 30);
    }

    [Fact]
    public async Task Compare_reports_changes_and_writes_highlights()
    {
        using var files = new TestFiles();
        var v1 = files.CreatePdf("v1.pdf", 1, body: _ => "The contract term is twelve months.\nPayment is due within thirty days.");
        var v2 = files.CreatePdf("v2.pdf", 1, body: _ => "The contract term is twenty four months.\nPayment is due within thirty days.\nLate fees apply.");
        var result = await CompareTool.RunAsync(new CompareOptions { OldFile = v1, NewFile = v2, OutputDirectory = files.Dir });
        var report = Assert.IsType<CompareReport>(result.Data);
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.Replaced && c.OldText == "twelve" && c.NewText == "twenty four");
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.Inserted && c.NewText.Contains("Late fees apply."));
        Assert.Equal(3, result.OutputFiles.Count);
        Assert.Contains("twenty four", result.Text);

        var overlay = await CompareTool.RunAsync(new CompareOptions { OldFile = v1, NewFile = v2, OutputDirectory = files.Dir, Overlay = true });
        Assert.Contains("1 page", overlay.Summary);
    }
}
