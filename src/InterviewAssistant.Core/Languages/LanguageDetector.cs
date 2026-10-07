namespace InterviewAssistant.Core.Languages;

public sealed record DetectedLanguage(string Code, double Confidence);

/// <summary>
/// Lightweight, dependency-free language identification for the 10 launch languages.
/// Non-Latin scripts are identified by Unicode block (Persian vs Arabic by Persian-only letters);
/// Latin-script languages by stop-word and diacritic profiles. Returns "und" when unsure — callers then
/// fall back to the user's locked/expected language. Good for paragraphs and full questions; short
/// fragments (&lt; 3 words) are reported with low confidence.
/// </summary>
public static class LanguageDetector
{
    private static readonly Dictionary<string, string[]> StopWords = new()
    {
        ["en"] = new[] { "the", "and", "you", "your", "what", "how", "with", "for", "would", "about", "have", "this", "that", "are", "is", "of", "to", "in", "me", "tell", "why", "can" },
        ["es"] = new[] { "el", "la", "los", "las", "de", "que", "y", "en", "por", "para", "con", "cómo", "qué", "usted", "tu", "su", "una", "es", "experiencia", "sobre", "nos", "puede" },
        ["fr"] = new[] { "le", "la", "les", "des", "de", "et", "vous", "votre", "que", "qui", "pour", "avec", "est", "une", "dans", "comment", "pourquoi", "sur", "nous", "pouvez", "quelle" },
        ["de"] = new[] { "der", "die", "das", "und", "sie", "ihre", "ihr", "wie", "was", "mit", "für", "ist", "ein", "eine", "nicht", "auf", "warum", "würden", "über", "uns", "können" },
        ["pt"] = new[] { "o", "a", "os", "as", "de", "que", "e", "em", "para", "com", "você", "sua", "seu", "uma", "como", "por", "não", "sobre", "nos", "pode", "experiência" },
        ["it"] = new[] { "il", "lo", "la", "gli", "le", "di", "che", "e", "per", "con", "lei", "sua", "suo", "una", "come", "perché", "non", "sulla", "ci", "può", "esperienza" },
    };

    private static readonly Dictionary<string, string> Diacritics = new()
    {
        ["es"] = "ñ¿¡áéíóú", ["fr"] = "çàâèéêëîïôœùûÿ", ["de"] = "äöüß", ["pt"] = "ãõçáâêéíóôú", ["it"] = "àèéìíòù",
    };

    private const string PersianOnly = "پچژگکی";      // letters absent from standard Arabic orthography
    private const string ArabicOnly = "ةيكىأإؤئ";      // typical Arabic-only forms (ي/ك differ from Persian ی/ک)

    public static DetectedLanguage Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new("und", 0);
        int arabicScript = 0, han = 0, devanagari = 0, latin = 0, persianMarks = 0, arabicMarks = 0, letters = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch >= '؀' && ch <= 'ۿ' || ch >= 'ݐ' && ch <= 'ݿ' || ch >= 'ﭐ' && ch <= '﻿')
            {
                arabicScript++;
                if (PersianOnly.Contains(ch)) persianMarks++;
                if (ArabicOnly.Contains(ch)) arabicMarks++;
            }
            else if (ch >= '一' && ch <= '鿿' || ch >= '㐀' && ch <= '䶿') han++;
            else if (ch >= 'ऀ' && ch <= 'ॿ') devanagari++;
            else if (ch < 'ɐ') latin++;
        }
        if (letters == 0) return new("und", 0);
        double share(int n) => (double)n / letters;
        if (share(han) > 0.3) return new("zh", Math.Min(1, 0.6 + share(han)));
        if (share(devanagari) > 0.3) return new("hi", Math.Min(1, 0.6 + share(devanagari)));
        if (share(arabicScript) > 0.3)
        {
            if (persianMarks > arabicMarks) return new("fa", persianMarks >= 2 ? 0.9 : 0.6);
            if (arabicMarks > persianMarks) return new("ar", arabicMarks >= 2 ? 0.9 : 0.6);
            return new("ar", 0.4);
        }
        if (share(latin) < 0.5) return new("und", 0.2);

        var words = text.ToLowerInvariant().Split(new[] { ' ', '\n', '\r', '\t', ',', '.', '?', '!', ';', ':', '(', ')', '"', '\'', '¿', '¡' }, StringSplitOptions.RemoveEmptyEntries);
        var scores = new Dictionary<string, double>();
        foreach (var (lang, list) in StopWords)
        {
            var set = list.ToHashSet();
            double s = words.Count(w => set.Contains(w));
            s += text.ToLowerInvariant().Count(c => Diacritics.TryGetValue(lang, out var d) && d.Contains(c)) * 0.6;
            scores[lang] = s;
        }
        var ordered = scores.OrderByDescending(kv => kv.Value).ToList();
        var best = ordered[0];
        if (best.Value < 1) return new("und", 0.1);
        var margin = best.Value - ordered[1].Value;
        var conf = Math.Min(0.95, 0.35 + margin / Math.Max(1, words.Length) * 2 + (words.Length >= 5 ? 0.2 : 0));
        return new(best.Key, words.Length < 3 ? Math.Min(conf, 0.4) : conf);
    }

    /// <summary>Detect, but only trust results above the threshold; otherwise return the fallback (e.g. locked language).</summary>
    public static string DetectOr(string? text, string fallback, double threshold = 0.5)
    {
        var d = Detect(text);
        return d.Confidence >= threshold && d.Code != "und" ? d.Code : fallback;
    }
}
