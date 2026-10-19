using System.Text.RegularExpressions;
using InterviewAssistant.Core.Languages;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Localization gate for every supported UI language: complete key coverage, matching placeholders, no section left
/// in English (fallback detection with an explicit allow-list of product names, acronyms and hotkeys), and a source
/// scan for hard-coded English user-facing text in the desktop app.
/// </summary>
public class LocalizationCoverageTests
{
    private readonly ITestOutputHelper _out;
    public LocalizationCoverageTests(ITestOutputHelper o) => _out = o;

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    /// <summary>Values that are legitimately identical across languages (names, acronyms, hotkeys, formats, symbols).</summary>
    private static readonly HashSet<string> SameInEveryLanguage = new(StringComparer.Ordinal)
    {
        "history_label", "min_label", "svc_ref", "set_key_saved", "fact_line", "device_row", "about_product",
        "coach", "pf_answer_ok", "prep_progress", "acct_signed_in_as", "start_signed_in", "min_today", "bill_payment_problem",
    };
    /// <summary>
    /// Reviewed cognates: words that are spelled the same as English and are the natural term in that language
    /// (e.g. German "Interviews", French "Version", Italian "Account"). Anything else identical to English fails.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> Cognates = new()
    {
        ["es"] = new() { "fact_kind_personal", "channel_beta_build", "font_normal", "set_nav_audio" },
        ["pt"] = new() { "channel_beta_build", "font_normal" },
        ["it"] = new() { "account", "password", "channel_beta_build", "set_nav_audio", "exp_section_account", "exp_tech_account" },
        ["de"] = new() { "upgrade", "interviews", "report_default_label", "channel_beta_build", "about_version", "font_normal", "set_nav_audio",
                         "exp_docs_target", "exp_prev_interviews" },
        ["fr"] = new() { "question", "pause", "structure", "file_filter_documents", "fact_kind", "fact_kind_certification", "report_preview_meta",
                         "channel_stable_build", "about_version", "about_install", "about_service", "pf_check_stt", "font_compact", "font_normal", "set_nav_audio",
                         "exp_col_date", "exp_tech_session", "exp_tech_document" },
    };
    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    /// <summary>
    /// rc.5: a key defined twice in one table silently keeps only one value (System.Text.Json takes the last). Parallel
    /// feature work merged the same keys twice once; every table must define each key exactly once.
    /// </summary>
    [Fact]
    public void NoTableDefinesAKeyTwice()
    {
        var dir = Path.Combine(RepoRoot, "src", "InterviewAssistant.Core", "Languages", "i18n");
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var keys = Regex.Matches(File.ReadAllText(file), @"^\s*""([^""]+)""\s*:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            var duplicates = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, $"{Path.GetFileName(file)} defines {string.Join(", ", duplicates)} more than once");
        }
    }

    [Fact]
    public void EnglishTableIsTheReference()
    {
        Assert.True(Loc.Keys.Count > 600, $"{Loc.Keys.Count} keys");
        Assert.All(Loc.Keys, k => Assert.Matches("^[a-z0-9_]+$", k));
    }

    [Theory]
    [InlineData("es")] [InlineData("fr")] [InlineData("de")] [InlineData("pt")] [InlineData("it")]
    [InlineData("ar")] [InlineData("fa")] [InlineData("zh")] [InlineData("hi")]
    public void EverySupportedLanguageIsComplete(string lang)
    {
        var missing = Loc.MissingKeys(lang);
        Assert.True(missing.Count == 0, $"{lang}: {missing.Count} missing keys: {string.Join(", ", missing.Take(40))}");
        var t = Loc.TableFor(lang);
        var en = Loc.TableFor("en");
        var placeholderMismatch = en.Keys.Where(k => t.ContainsKey(k) &&
            !Placeholder.Matches(en[k]).Select(m => m.Value).OrderBy(x => x).SequenceEqual(Placeholder.Matches(t[k]).Select(m => m.Value).OrderBy(x => x))).ToList();
        Assert.True(placeholderMismatch.Count == 0, $"{lang}: placeholder mismatch in {string.Join(", ", placeholderMismatch)}");
        // Fallback-only detection: a value identical to English means the section was not translated.
        var untranslated = en.Keys.Where(k => t.TryGetValue(k, out var v) && v == en[k] && !SameInEveryLanguage.Contains(k) && !(Cognates.TryGetValue(lang, out var c) && c.Contains(k)) && HasEnglishWords(v)).ToList();
        _out.WriteLine($"{lang}: {t.Count} keys, {untranslated.Count} identical to English");
        Assert.True(untranslated.Count == 0, $"{lang}: untranslated (identical to English): {string.Join(", ", untranslated.Take(40))}");
        var extra = t.Keys.Where(k => !en.ContainsKey(k)).ToList();
        Assert.True(extra.Count == 0, $"{lang}: keys not in English: {string.Join(", ", extra)}");
    }

    /// <summary>True when the value contains ordinary English words (not only names, acronyms, numbers or hotkeys).</summary>
    private static bool HasEnglishWords(string v)
    {
        var cleaned = Regex.Replace(v, @"\{\d+\}|Ctrl\+Alt\+\w+|Interview Assistant|OpenAI|Paddle|Chrome|Meet|Zoom|Teams|Windows|SmartScreen|DPAPI|API|PDF|DOCX|TXT|ZIP|HTML|UTC|dBFS|Hz|AI|PASS|FAIL|WARNING|Bluetooth|Coach|Pro|ms|min|·|—|…|[^\p{L}\s]", " ");
        return Regex.Matches(cleaned, @"\b[a-zA-Z]{3,}\b").Count > 0;
    }

    [Theory]
    [InlineData("fa")] [InlineData("ar")]
    public void RightToLeftLanguagesAreRegisteredAsRtl(string lang) => Assert.True(LanguageRegistry.IsRtl(lang));

    [Fact]
    public void LiveLanguageSwitchRaisesAnEventAndChangesText()
    {
        var before = Loc.Language;
        int raised = 0;
        void Handler() => raised++;
        Loc.LanguageChanged += Handler;
        try
        {
            Loc.Language = "de";
            var de = Loc.T("start");
            Loc.Language = "fa";
            Assert.True(Loc.IsRtl);
            Assert.NotEqual(de, Loc.T("start"));
            Assert.Equal(2, raised);
        }
        finally { Loc.LanguageChanged -= Handler; Loc.Language = before; }
    }

    [Fact]
    public void MessagesAlreadyOnScreen_FollowALanguageSwitch()
    {
        var before = Loc.Language;
        try
        {
            Loc.Language = "en";
            var shown = Loc.T("svc_unreachable") + " " + Loc.F("svc_ref", "1a2b3c4d");
            Loc.Language = "fa";
            var now = Loc.Relocalize(shown)!;
            Assert.StartsWith(Loc.T("svc_unreachable"), now);
            Assert.EndsWith(Loc.F("svc_ref", "1a2b3c4d"), now);
            Assert.Equal(Loc.T("toast_dismiss"), Loc.Relocalize(Loc.TableFor("hi")["toast_dismiss"]));   // from any language
            Assert.Equal("How would you prioritise the backlog?", Loc.Relocalize("How would you prioritise the backlog?"));   // user data unchanged
            Assert.Null(Loc.Relocalize(null));
        }
        finally { Loc.Language = before; }
    }

    [Fact]
    public void UiLanguageNeverChangesTheAnswerLanguage()
    {
        var before = Loc.Language;
        try
        {
            Loc.Language = "fa";
            Assert.Equal("en", LanguageRegistry.ResolveAnswerLanguage("same", "en", "en"));
            Assert.Equal("de", LanguageRegistry.ResolveAnswerLanguage("same", "en", "de"));
        }
        finally { Loc.Language = before; }
    }

    // ---------------- source scans (hard-coded English in the desktop UI) ----------------

    private static readonly Regex XamlLiteral = new(@"\b(Text|Content|ToolTip|Header|Title|AutomationProperties\.Name)=""(?<v>[^""{][^""]*)""", RegexOptions.Compiled);
    private static readonly HashSet<string> XamlAllowed = new(StringComparer.Ordinal) { "Interview Assistant", "●  ●  ●", ": " };

    [Fact]
    public void XamlHasNoHardCodedUserText()
    {
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "InterviewAssistant.App"), "*.xaml", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.EndsWith("Theme.xaml") || f.EndsWith("App.xaml")) continue;
            foreach (Match m in XamlLiteral.Matches(File.ReadAllText(f)))
            {
                var v = m.Groups["v"].Value;
                if (v.StartsWith("&#x") || XamlAllowed.Contains(v) || !Regex.IsMatch(v, @"[A-Za-z]{2,}")) continue;
                bad.Add($"{Path.GetFileName(f)}: {m.Value}");
            }
        }
        Assert.True(bad.Count == 0, "Hard-coded XAML text:\n" + string.Join("\n", bad));
    }

    // UI-facing calls whose string argument must come from Loc (logs, exceptions and diagnostics are exempt).
    private static readonly Regex CsUiLiteral = new(
        @"((?<![\w""])\.?(Text|Content|ToolTip|Header|Title)\s*=\s*\$?""[^""]*[A-Za-z]{3,}|\b(Notify|Toast|Warn|Confirm|ShowLocalNote|ShowToast|ShowTransientError|Toasts\.Show|ShowResult)\(\s*\$?""[^""]*[A-Za-z]{3,}|\bNote\s*=\s*\$?""[^""]*[A-Za-z]{3,}|\b(Pass|Fail|Busy)\(\s*\$?""[^""]*[A-Za-z]{3,})",
        RegexOptions.Compiled);

    [Fact]
    public void DesktopCodeHasNoHardCodedUserText()
    {
        var bad = new List<string>();
        var exempt = new[] { "SelfTest.cs", "AppLog.cs" };
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "InterviewAssistant.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || exempt.Contains(Path.GetFileName(f))) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || lines[i].Contains("AppLog.")) continue;
                var withoutInterpolation = Regex.Replace(lines[i], @"\{[^{}""]*\}", "");   // {expressions} inside $"..." are data, not text
                if (CsUiLiteral.IsMatch(withoutInterpolation)) bad.Add($"{Path.GetFileName(f)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(bad.Count == 0, "Hard-coded UI text in code:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryKeyUsedInTheAppExistsInEnglish()
    {
        var used = new HashSet<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var s = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(s, @"\{v:Loc ([a-z0-9_]+)\}")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(s, @"\bLoc\.(?:T|F)\(\s*""([a-z][a-z0-9_]+)""")) used.Add(m.Groups[1].Value);
        }
        var missing = used.Where(k => !k.EndsWith('_') && !Loc.Has(k)).OrderBy(k => k).ToList();   // "fact_kind_" + kind: prefixes are checked below
        Assert.True(missing.Count == 0, "Keys missing from en.json: " + string.Join(", ", missing));
        // Dynamic keys built from enums/categories.
        foreach (var k in Enum.GetNames<InterviewAssistant.Core.Domain.FactKind>()) Assert.True(Loc.Has("fact_kind_" + k.ToLowerInvariant()), k);
        foreach (var c in InterviewAssistant.Core.Domain.PersonalCategories.All)
        {
            Assert.True(Loc.Has($"pc_{c.Key}_label") && Loc.Has($"pc_{c.Key}_prompt"), c.Key);
            Assert.True(Loc.Has("pcs_" + c.Section), c.Section);
        }
        foreach (var s in new[] { "active", "stopped", "device_error", "no_playback_device", "reconnecting_device" }) Assert.True(Loc.Has("audio_" + s), s);
        foreach (var f in new[] { "compact", "normal", "large", "extralarge" }) Assert.True(Loc.Has("font_" + f), f);
    }
}
