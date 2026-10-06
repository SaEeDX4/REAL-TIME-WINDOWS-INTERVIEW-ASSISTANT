using System.Text;
using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>Normalises spoken transcripts: removes fillers, fixes domain-term misrecognitions, tokenises.</summary>
public static class TextNormalizer
{
    private static readonly Regex FillerRegex = new(
        @"\b(um+|uh+|erm+|hmm+|ah+|you know|i mean|kind of|sort of|basically|actually|like,|okay so|ok so|so,|well,|right,)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Common ASR confusions for domain terms. Keys are lower-case regex patterns.
    private static readonly (Regex Pattern, string Replacement)[] TermFixes =
    {
        (new Regex(@"\b(terox|teroks|terrox|teraxx|ter ox|terroxx)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Teroxx"),
        (new Regex(@"\b(ab ?locks|a ?blocks|ablox|ablocks|abloks)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Abloxx"),
        (new Regex(@"\b(x a b|x\.a\.b\.?|ex ay bee|zab)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "XAB"),
        (new Regex(@"\b(micar|mica r|mika|mica regulation|mi ca)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "MiCA"),
        (new Regex(@"\b(cysec|cy sec|sci sec|psy sec)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "CySEC"),
        (new Regex(@"\b(arziff?|ar zif|arsif)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Arzif"),
        (new Regex(@"\b(jira|gira|jeera)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Jira"),
        (new Regex(@"\b(coin ex)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "CoinEx"),
        (new Regex(@"\b(ku coin|cu coin|coo coin)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "KuCoin"),
        (new Regex(@"\b(item potency|idem potency|ident potency)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "idempotency"),
        (new Regex(@"\b(token omics|tokonomics)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "tokenomics"),
        (new Regex(@"\b(v i p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "VIP"),
    };

    public static string CleanTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = FillerRegex.Replace(text, " ");
        foreach (var (pattern, replacement) in TermFixes) t = pattern.Replace(t, replacement);
        t = Regex.Replace(t, @"^\s*(so|and so|okay|ok|well|right|alright)\b[\s,]*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"\s+([,.?!])", "$1");
        t = Regex.Replace(t, @",{2,}", ",");
        t = t.TrimStart(',', ' ');
        if (t.Length > 0) t = char.ToUpperInvariant(t[0]) + t[1..];
        return t;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","an","the","and","or","of","to","in","on","for","with","is","are","was","were","be","been","do","does","did",
        "you","your","we","our","i","me","my","it","this","that","at","by","as","from","about","us","so","can","could","would",
        "will","should","there","their","they","them","some","any","if","then","than","just","also","have","has","had","into",
        "please","tell","maybe","bit","little","really","very","what","how","why","when","which","who","s","d","ll","ve","re",
    };

    /// <summary>Lower-cased content tokens with light stemming. Question words are kept separately by the classifier.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9][a-z0-9\-]*"))
        {
            var w = m.Value.Trim('-');
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            tokens.Add(Stem(w));
        }
        return tokens;
    }

    public static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        if (w.Length > 4 && w.EndsWith("ise")) return w[..^3] + "ize";
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    /// <summary>Stable fingerprint used for duplicate detection: sorted unique content tokens.</summary>
    public static string Fingerprint(string text)
    {
        var tokens = Tokenize(text).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    public static HashSet<string> CharTrigrams(string text)
    {
        var s = " " + Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9 ]", "") + " ";
        var set = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }

    public static double Jaccard<T>(ISet<T> a, ISet<T> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    public static int WordCount(string text) => Regex.Matches(text, @"[A-Za-z0-9’'\-]+").Count;

    public static string StripBullet(string line)
    {
        var sb = new StringBuilder(line.Trim());
        while (sb.Length > 0 && (sb[0] == '•' || sb[0] == '-' || sb[0] == '*' || sb[0] == '·' || char.IsWhiteSpace(sb[0]))) sb.Remove(0, 1);
        return sb.ToString().Trim().Trim('"', '“', '”');
    }
}
