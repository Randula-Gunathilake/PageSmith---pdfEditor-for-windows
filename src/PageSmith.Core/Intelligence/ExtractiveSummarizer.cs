namespace PageSmith.Core.Intelligence;

public enum SummaryLength
{
    Short,
    Medium,
    Long,
}

public sealed record SummaryResult(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<string> Keywords, int SourceWords, string Language, string Method)
{
    public string ToMarkdown(string? title = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine("## Summary").AppendLine().AppendLine(Summary).AppendLine();
        if (KeyPoints.Count > 0)
        {
            sb.AppendLine("## Key points").AppendLine();
            foreach (var p in KeyPoints) sb.AppendLine($"- {p}");
            sb.AppendLine();
        }
        if (Keywords.Count > 0) sb.AppendLine("## Keywords").AppendLine().AppendLine(string.Join(", ", Keywords));
        return sb.ToString().TrimEnd() + "\n";
    }
}

/// <summary>
/// Offline extractive summarizer: sentences are scored by the weight of their content words
/// (term frequency with stemming, stop words removed), position and length, then the best
/// non-redundant sentences are returned in document order.
/// </summary>
public static class ExtractiveSummarizer
{
    public static SummaryResult Summarize(string text, SummaryLength length = SummaryLength.Medium)
    {
        var language = NlpText.DetectLanguage(text);
        var sentences = NlpText.SplitSentences(text)
            .Where(s => s.Length is >= 25 and <= 600 && s.Count(char.IsLetter) > s.Length / 2)
            .ToList();
        var sourceWords = NlpText.Tokenize(text).Count();
        if (sentences.Count == 0) return new SummaryResult(text.Trim(), [], [], sourceWords, language, "extractive");

        var tokens = sentences.Select(s => ContentTerms(s, language).ToList()).ToList();
        var tf = new Dictionary<string, double>();
        foreach (var t in tokens.SelectMany(x => x)) tf[t] = tf.GetValueOrDefault(t) + 1;
        var max = tf.Count == 0 ? 1 : tf.Values.Max();
        foreach (var k in tf.Keys.ToList()) tf[k] /= max;

        var scores = new double[sentences.Count];
        for (var i = 0; i < sentences.Count; i++)
        {
            if (tokens[i].Count == 0) continue;
            var weight = tokens[i].Distinct().Sum(t => tf[t]);
            scores[i] = weight / Math.Sqrt(tokens[i].Count + 4);
            // Opening sentences of a document tend to state its purpose.
            if (i < 3) scores[i] *= 1.25 - 0.08 * i;
            if (sentences[i].Any(char.IsDigit)) scores[i] *= 1.05;
        }

        var target = Target(sentences.Count, length);
        var chosen = PickDiverse(sentences, tokens, scores, target);
        var summary = string.Join(" ", chosen.Order().Select(i => sentences[i]));

        var keyPoints = PickDiverse(sentences, tokens, scores, Math.Min(length == SummaryLength.Short ? 3 : length == SummaryLength.Medium ? 5 : 7, sentences.Count))
            .Select(i => Shorten(sentences[i]))
            .ToList();
        var keywords = Keywords(sentences, language, 8);
        return new SummaryResult(summary, keyPoints, keywords, sourceWords, language, "extractive");
    }

    internal static int Target(int sentenceCount, SummaryLength length)
    {
        var fraction = length switch { SummaryLength.Short => 0.06, SummaryLength.Long => 0.2, _ => 0.12 };
        var min = length switch { SummaryLength.Short => 2, SummaryLength.Long => 6, _ => 4 };
        var max = length switch { SummaryLength.Short => 5, SummaryLength.Long => 25, _ => 12 };
        return Math.Clamp((int)Math.Round(sentenceCount * fraction), Math.Min(min, sentenceCount), Math.Min(max, sentenceCount));
    }

    /// <summary>Greedy selection by score, skipping sentences that mostly repeat one already chosen.</summary>
    internal static List<int> PickDiverse(List<string> sentences, List<List<string>> tokens, double[] scores, int count)
    {
        var chosen = new List<int>();
        foreach (var i in Enumerable.Range(0, sentences.Count).OrderByDescending(i => scores[i]))
        {
            if (chosen.Count >= count) break;
            var set = tokens[i].ToHashSet();
            if (set.Count == 0) continue;
            var redundant = chosen.Any(j =>
            {
                var other = tokens[j].ToHashSet();
                var inter = set.Count(other.Contains);
                return inter / (double)Math.Max(1, Math.Min(set.Count, other.Count)) > 0.7;
            });
            if (!redundant) chosen.Add(i);
        }
        return chosen;
    }

    public static IEnumerable<string> ContentTerms(string sentence, string language) =>
        NlpText.Tokenize(sentence)
            .Where(w => !NlpText.IsStopWord(w, language) && !w.All(char.IsDigit))
            .Select(w => NlpText.Stem(w, language));

    /// <summary>Most frequent content words, shown in their most common surface form.</summary>
    public static List<string> Keywords(IEnumerable<string> sentences, string language, int count)
    {
        var forms = new Dictionary<string, Dictionary<string, int>>();
        var docFreq = new Dictionary<string, int>();
        foreach (var s in sentences)
        {
            var seen = new HashSet<string>();
            foreach (var w in NlpText.Tokenize(s))
            {
                if (NlpText.IsStopWord(w, language) || w.Length < 3 || w.All(char.IsDigit)) continue;
                var stem = NlpText.Stem(w, language);
                if (!forms.TryGetValue(stem, out var f)) forms[stem] = f = [];
                f[w] = f.GetValueOrDefault(w) + 1;
                if (seen.Add(stem)) docFreq[stem] = docFreq.GetValueOrDefault(stem) + 1;
            }
        }
        return docFreq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(count)
            .Select(kv => forms[kv.Key].MaxBy(f => f.Value).Key)
            .ToList();
    }

    private static string Shorten(string sentence)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 32 ? sentence : string.Join(' ', words.Take(30)) + "…";
    }
}
