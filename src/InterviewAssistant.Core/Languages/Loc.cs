using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace InterviewAssistant.Core.Languages;

/// <summary>
/// The single localization system for every user-visible string: one JSON table per supported UI language
/// (Languages/i18n/{code}.json, embedded). English is the reference: every key exists there. A key missing in another
/// language falls back to English and is reported by <see cref="MissingKeys"/> (the coverage tests fail on it).
/// Product names, hotkeys and technical terms inside the strings are kept verbatim by the translations.
/// </summary>
public static class Loc
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Tables = new();
    private static volatile string _language = "en";

    /// <summary>Current UI language (process-wide). Changing it raises <see cref="LanguageChanged"/>.</summary>
    public static string Language
    {
        get => _language;
        set
        {
            var code = LanguageRegistry.Find(value)?.Code ?? "en";
            if (code == _language) return;
            _language = code;
            LanguageChanged?.Invoke();
        }
    }

    public static event Action? LanguageChanged;
    public static bool IsRtl => LanguageRegistry.IsRtl(_language);
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(LanguageRegistry.Find(_language)?.CultureName ?? "en-US");

    /// <summary>Translated text for the current UI language.</summary>
    public static string T(string key) => Get(_language, key);

    /// <summary>Translated, formatted text ({0}, {1} placeholders) for the current UI language.</summary>
    public static string F(string key, params object?[] args) => Format(_language, key, args);

    public static string Get(string? language, string key)
    {
        var code = LanguageRegistry.Find(language)?.Code ?? "en";
        if (Table(code).TryGetValue(key, out var v) && v.Length > 0) return v;
        return Table("en").TryGetValue(key, out var en) ? en : key;
    }

    public static string Format(string? language, string key, params object?[] args)
    {
        var code = LanguageRegistry.Find(language)?.Code ?? "en";
        var culture = CultureInfo.GetCultureInfo(LanguageRegistry.Find(code)?.CultureName ?? "en-US");
        try { return string.Format(culture, Get(code, key), args); }
        catch (FormatException) { return Get("en", key); }
    }

    public static bool Has(string key) => Table("en").ContainsKey(key);
    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)Table("en").Keys;
    public static IReadOnlyDictionary<string, string> TableFor(string language) => Table(LanguageRegistry.Find(language)?.Code ?? "en");

    /// <summary>Keys present in English but missing (or empty) in <paramref name="language"/>.</summary>
    public static IReadOnlyList<string> MissingKeys(string language)
    {
        var t = TableFor(language);
        return Table("en").Keys.Where(k => !t.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).OrderBy(k => k).ToList();
    }

    private static IReadOnlyDictionary<string, string> Table(string code) => Tables.GetOrAdd(code, Load);

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        var asm = typeof(Loc).Assembly;
        using var s = asm.GetManifestResourceStream($"InterviewAssistant.Core.Languages.i18n.{code}.json");
        if (s == null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>();
    }
}
