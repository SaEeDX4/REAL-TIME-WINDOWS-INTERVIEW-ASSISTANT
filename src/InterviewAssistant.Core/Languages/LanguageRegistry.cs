namespace InterviewAssistant.Core.Languages;

public sealed record LanguageInfo(string Code, string EnglishName, string NativeName, bool IsRightToLeft, string Script, string CultureName);

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
