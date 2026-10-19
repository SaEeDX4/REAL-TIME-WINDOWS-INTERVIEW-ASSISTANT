using System.Collections.Concurrent;
using System.Globalization;

namespace InterviewAssistant.Core.Languages;

public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, bool IsRightToLeft, string Script, string CultureName)
{
    /// <summary>Human label for pickers: "English", "Persian — فارسی".</summary>
    public string DisplayName => string.Equals(NativeName, EnglishName, StringComparison.OrdinalIgnoreCase) ? EnglishName : $"{EnglishName} — {NativeName}";
    public override string ToString() => DisplayName;   // never the record's debug format in UI
}

/// <summary>
/// Supported interview/answer/UI languages. Adding a language = one entry here + a UI resource file
/// (+ optional stop-word profile in LanguageDetector). The engine itself is language-agnostic.
/// </summary>
public static class LanguageRegistry
{
    public static readonly IReadOnlyList<LanguageInfo> All = new[]
    {
        new LanguageInfo("en", "English", "English", false, "Latin", "en-US"),
        new LanguageInfo("es", "Spanish", "Español", false, "Latin", "es-ES"),
        new LanguageInfo("fr", "French", "Français", false, "Latin", "fr-FR"),
        new LanguageInfo("de", "German", "Deutsch", false, "Latin", "de-DE"),
        new LanguageInfo("pt", "Portuguese", "Português", false, "Latin", "pt-BR"),
        new LanguageInfo("it", "Italian", "Italiano", false, "Latin", "it-IT"),
        new LanguageInfo("ar", "Arabic", "العربية", true, "Arabic", "ar-SA"),
        new LanguageInfo("fa", "Persian", "فارسی", true, "Arabic", "fa-IR"),
        new LanguageInfo("zh", "Chinese (Simplified)", "简体中文", false, "Han", "zh-CN"),
        new LanguageInfo("hi", "Hindi", "हिन्दी", false, "Devanagari", "hi-IN"),
    };

    public static LanguageInfo? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        if (c.Length > 2 && (c[2] == '-' || c[2] == '_')) c = c[..2];
        return All.FirstOrDefault(l => l.Code == c);
    }

    public static bool IsSupported(string? code) => Find(code) != null;
    public static bool IsRtl(string? code) => Find(code)?.IsRightToLeft ?? false;
    public static string EnglishName(string? code) => Find(code)?.EnglishName ?? "English";

    private static readonly ConcurrentDictionary<string, CultureInfo> FormattingCultures = new();

    /// <summary>
    /// Read-only culture for numbers and dates in <paramref name="code"/> (English for an unknown code). Arabic uses
    /// a clone of ar-SA on the Gregorian calendar: ar-SA defaults to the Umm al-Qura (Hijri) calendar, and billing
    /// and export dates must match the Gregorian dates on payment receipts. Persian keeps its Solar Hijri calendar.
    /// </summary>
    public static CultureInfo FormattingCulture(string? code)
    {
        var info = Find(code) ?? All[0];
        return FormattingCultures.GetOrAdd(info.Code, _ =>
        {
            var culture = CultureInfo.GetCultureInfo(info.CultureName);
            if (info.Code != "ar" || culture.DateTimeFormat.Calendar is GregorianCalendar) return culture;
            var gregorian = culture.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault();
            if (gregorian == null) return culture;
            var clone = (CultureInfo)culture.Clone();
            clone.DateTimeFormat.Calendar = gregorian;
            return CultureInfo.ReadOnly(clone);
        });
    }

    /// <summary>
    /// Resolves the language answers must be written in.
    /// answerSetting: "same" → follow detected interviewer language (fallback: locked interview language, then English).
    /// </summary>
    public static string ResolveAnswerLanguage(string answerSetting, string interviewSetting, string? detected)
    {
        if (!string.IsNullOrWhiteSpace(answerSetting) && answerSetting != "same" && IsSupported(answerSetting)) return Find(answerSetting)!.Code;
        if (IsSupported(detected)) return Find(detected)!.Code;
        if (IsSupported(interviewSetting)) return Find(interviewSetting)!.Code;
        return "en";
    }

    /// <summary>Prompt instruction that makes the model write natively in the target language (not translate from English).</summary>
    public static string AnswerLanguageRule(string code)
    {
        var l = Find(code) ?? All[0];
        if (l.Code == "en") return "LANGUAGE: Write in natural spoken English.";
        return $"LANGUAGE: Write the bullets directly in natural, native-sounding spoken {l.EnglishName} ({l.NativeName}) — do not translate word-for-word from English. " +
               "Keep the MODE line in English. Keep company names, product names, acronyms, metrics, dates and established professional terms " +
               "(e.g. Product Owner, KPI, OKR, API, Jira) in their usual professional form for that language.";
    }
}
