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
        ["en"] = "the and you your what how with for would about have this that are is of to in me tell why can did do does was were has been an a it on at by from which who".Split(' '),
        ["es"] = "el la los las de del que y en por para con cómo qué usted tu su sus una un es son experiencia sobre nos puede ha han hay lo le se fue cuál cuénteme hábleme nunca también muy este esta alguna vez ya".Split(' '),
        ["fr"] = "le la les des de du et vous votre vos que qui pour avec est une un dans comment pourquoi sur nous pouvez quelle quel avez êtes été ce cette au aux parlez jamais déjà aussi très".Split(' '),
        ["de"] = "der die das und sie ihre ihr wie was mit für ist ein eine einen nicht auf warum würden über uns können haben hat von zu im den dem des bei erzählen nie schon auch sehr".Split(' '),
        ["pt"] = "o a os as de do da que e em para com você sua seu uma um como por não sobre nos pode já foi são está na no dos das conte fale nunca também este esta alguma vez".Split(' '),
        ["it"] = "il lo la gli le di del della che e per con lei sua suo una un come perché non sulla ci può ha ho sono è nel nella dei degli mi parli mai già anche questo questa cosa quale".Split(' '),
    };


    // Morphology: frequent derivational suffixes, weighted lower than function words.
    private static readonly Dictionary<string, string[]> Suffixes = new()
    {
        ["en"] = new[] { "tion", "ing", "ness", "ship", "ly" },
        ["es"] = new[] { "ción", "dad", "ado", "ido", "ías" },
        ["fr"] = new[] { "tion", "ment", "eux", "aire", "ais", "ez" },
        ["de"] = new[] { "ung", "keit", "heit", "lich", "isch", "chen" },
        ["pt"] = new[] { "ção", "ções", "dade", "ões", "ado" },
        ["it"] = new[] { "zione", "ario", "ito", "ato", "ale", "ità", "ebbe" },
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

        var words = text.ToLowerInvariant().Split(new[] { ' ', '\n', '\r', '\t', ',', '.', '?', '!', ';', ':', '(', ')', '"', '\'', '’', '-', '¿', '¡' }, StringSplitOptions.RemoveEmptyEntries);
        var scores = new Dictionary<string, double>();
        foreach (var (lang, list) in StopWords)
        {
            var set = list.ToHashSet();
            double s = words.Count(w => set.Contains(w));
            s += text.ToLowerInvariant().Count(c => Diacritics.TryGetValue(lang, out var d) && d.Contains(c)) * 0.6;
            s += words.Count(w => w.Length > 4 && Suffixes[lang].Any(x => w.EndsWith(x, StringComparison.Ordinal))) * 0.3;
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
