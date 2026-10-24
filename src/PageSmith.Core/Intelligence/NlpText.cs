using System.Text.RegularExpressions;

namespace PageSmith.Core.Intelligence;

/// <summary>Lightweight, offline text processing: sentences, tokens, stop words, stemming, language guess.</summary>
public static partial class NlpText
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "fig", "no", "vol", "inc", "ltd", "co",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "approx", "dept", "est", "u.s", "u.k", "p", "pp",
    };

    /// <summary>Splits text into sentences, respecting abbreviations, decimals and initials.</summary>
    public static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        foreach (var paragraph in ParagraphBreak().Split(text.Replace("\r\n", "\n")))
        {
            var p = Whitespace().Replace(paragraph, " ").Trim();
            if (p.Length == 0) continue;
            var start = 0;
            for (var i = 0; i < p.Length; i++)
            {
                var c = p[i];
                if (c is not ('.' or '!' or '?' or '。' or '！' or '？' or '।')) continue;
                // Continue through closing quotes/brackets and repeated punctuation.
                var end = i + 1;
                while (end < p.Length && (p[end] is '"' or '\'' or ')' or ']' or '”' or '’' or '.' or '!' or '?')) end++;
                var atEnd = end >= p.Length;
                var nextIsBoundary = atEnd || (char.IsWhiteSpace(p[end]) && end + 1 < p.Length && (char.IsUpper(p[end + 1]) || char.IsDigit(p[end + 1]) || p[end + 1] is '"' or '“' or '(' or '['));
                var cjk = c is '。' or '！' or '？';
                if (!nextIsBoundary && !cjk) continue;
                if (c == '.')
                {
                    var word = PreviousWord(p, i);
                    if (Abbreviations.Contains(word.TrimEnd('.')) || (word.Length == 1 && char.IsUpper(word[0]))) continue;
                    if (i > 0 && char.IsDigit(p[i - 1]) && !atEnd && end < p.Length && char.IsDigit(p[Math.Min(p.Length - 1, end)])) continue;
                }
                var sentence = p[start..end].Trim();
                if (sentence.Length > 0) sentences.Add(sentence);
                start = end;
                i = end - 1;
            }
            if (start < p.Length)
            {
                var rest = p[start..].Trim();
                if (rest.Length > 0) sentences.Add(rest);
            }
        }
        return sentences;
    }

    private static string PreviousWord(string s, int dot)
    {
        var j = dot - 1;
        while (j >= 0 && (char.IsLetter(s[j]) || s[j] == '.')) j--;
        return s[(j + 1)..dot];
    }

    /// <summary>Lower-cased word tokens (letters and digits), apostrophes kept inside words.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match m in WordRx().Matches(text)) yield return m.Value.ToLowerInvariant();
    }

    public static bool IsStopWord(string word, string language = "en") =>
        (StopWords.TryGetValue(language, out var set) ? set : StopWords["en"]).Contains(word) || word.Length < 2;

    /// <summary>A light English stemmer (common inflectional suffixes); other languages pass through.</summary>
    public static string Stem(string word, string language = "en")
    {
        if (language != "en" || word.Length <= 4) return word;
        foreach (var (suffix, replacement) in Suffixes)
        {
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 3)
                return word[..^suffix.Length] + replacement;
        }
        return word;
    }

    private static readonly (string, string)[] Suffixes =
    [
        ("ational", "ate"), ("ization", "ize"), ("fulness", "ful"), ("ousness", "ous"), ("iveness", "ive"),
        ("tional", "tion"), ("ements", "ement"), ("ement", "ement"), ("ments", "ment"), ("ities", "ity"), ("ingly", ""),
        ("ings", ""), ("ing", ""), ("edly", ""), ("ied", "y"), ("ies", "y"), ("ed", ""), ("ly", ""), ("es", "e"), ("s", ""),
    ];

    /// <summary>Guesses the language of a text: script first, then stop-word overlap for Latin scripts. Returns an ISO 639-1 code.</summary>
    public static string DetectLanguage(string text)
    {
        var sample = text.Length > 6000 ? text[..6000] : text;
        var scripts = new Dictionary<string, int>();
        foreach (var ch in sample)
        {
            var s = Script(ch);
            if (s != null) scripts[s] = scripts.GetValueOrDefault(s) + 1;
        }
        if (scripts.Count > 0)
        {
            var (script, count) = scripts.MaxBy(kv => kv.Value);
            var letters = sample.Count(char.IsLetter);
            if (count > letters * 0.3)
            {
                if (script == "ja-kana") return "ja";
                if (script == "han") return scripts.ContainsKey("ja-kana") ? "ja" : "zh";
                return script;
            }
        }

        var words = Tokenize(sample).ToList();
        if (words.Count == 0) return "en";
        var best = "en";
        var bestScore = 0.0;
        foreach (var (lang, set) in StopWords)
        {
            var score = words.Count(set.Contains) / (double)words.Count;
            if (score > bestScore)
            {
                bestScore = score;
                best = lang;
            }
        }
        return best;
    }

    private static string? Script(char c) => c switch
    {
        >= '඀' and <= '෿' => "si",
        >= '஀' and <= '௿' => "ta",
        >= 'ऀ' and <= 'ॿ' => "hi",
        >= 'ঀ' and <= '৿' => "bn",
        >= '฀' and <= '๿' => "th",
        >= '؀' and <= 'ۿ' => "ar",
        >= '֐' and <= '׿' => "he",
        >= 'Ѐ' and <= 'ӿ' => "ru",
        >= 'Ͱ' and <= 'Ͽ' => "el",
        >= '가' and <= '힯' => "ko",
        >= '぀' and <= 'ヿ' => "ja-kana",
        >= '一' and <= '鿿' => "han",
        >= 'Ⴀ' and <= 'ჿ' => "ka",
        >= '԰' and <= '֏' => "hy",
        _ => null,
    };

    public static readonly IReadOnlyDictionary<string, HashSet<string>> StopWords = new Dictionary<string, HashSet<string>>
    {
        ["en"] = Set("a about above after again against all also am an and any are aren't as at be because been before being below between both but by can can't cannot could couldn't did didn't do does doesn't doing don't down during each few for from further had hadn't has hasn't have haven't having he he'd he'll he's her here here's hers herself him himself his how how's however i i'd i'll i'm i've if in into is isn't it it's its itself let's may me might more most must mustn't my myself no nor not of off on once only or other ought our ours ourselves out over own same shall shan't she she'd she'll she's should shouldn't so some such than that that's the their theirs them themselves then there there's these they they'd they'll they're they've this those through thus to too under until up upon us very was wasn't we we'd we'll we're we've were weren't what what's when when's where where's which while who who's whom why why's will with won't would wouldn't yet you you'd you'll you're you've your yours yourself yourselves also within without per via one two three"),
        ["es"] = Set("a al algo algunas algunos ante antes como con contra cual cuando de del desde donde durante e el ella ellas ellos en entre era es esa esas ese eso esos esta estaba estado estas este esto estos fue fueron ha han hasta hay la las le les lo los mas me mi mucho muy nada ni no nos nosotros o os otra otro para pero poco por porque que quien se sea ser si sin sobre son su sus también tanto te tiene todo todos tu un una unas uno unos y ya"),
        ["fr"] = Set("a au aux avec ce ces cette comme dans de des du elle en est et eux il ils je la le les leur lui ma mais me même mes moi mon ne nos notre nous on ou par pas pour qu que qui sa se ses son sont sur ta te tes toi ton tu un une vos votre vous y été être avoir fait plus été"),
        ["de"] = Set("aber als am an auch auf aus bei bin bis bist da damit dann der den des dem die das dass du durch ein eine einem einen einer eines er es für hat hatte ich ihr im in ist ja kann mit nach nicht noch nur oder sein sich sie sind so über um und uns von vor war was wenn wer wie wir wird zu zum zur"),
        ["pt"] = Set("a ao aos as com como da das de do dos e ela ele eles em entre era essa esse esta este eu foi há isso já mais mas me mesmo muito na nas não no nos o os ou para pela pelo por que se sem ser seu sua são também tem um uma você"),
        ["it"] = Set("a al alla alle anche che chi ci con da dal dalla de del della delle di e è gli ha hanno il in io la le lei lo loro lui ma mi ne nel nella non noi per più quella questo se si sono su tra tu un una uno voi"),
        ["nl"] = Set("aan al als bij dat de den der des die dit door een en er geen het hij hun ik in is je maar met mij niet nog of om ook op over te tot u uit van voor want was wat we wel wie wij zal ze zij zijn"),
    };

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?")]
    private static partial Regex WordRx();
}
