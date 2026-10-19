using System.Globalization;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Reports;

namespace InterviewAssistant.Tests;

/// <summary>
/// Localized dates in the exports and the Account page: long dates without the weekday (they sit inside sentences
/// such as "Si rinnova il {0}"), Arabic on the Gregorian calendar (so billing dates match payment receipts), and
/// versions and e-mail addresses isolated left-to-right inside localized Account sentences.
/// </summary>
public class L10nDateAndBidiTests
{
    private static readonly string[] Languages = { "en", "es", "fr", "de", "pt", "it", "ar", "fa", "zh", "hi" };

    // Sunday 18 October 2026, midday UTC (the same calendar day in every time zone used here).
    private static readonly DateTime Sunday = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    [Theory]
    [InlineData("it")] [InlineData("pt")] [InlineData("de")] [InlineData("en")] [InlineData("fr")]
    public void ExportDates_HaveNoWeekday(string lang)
    {
        var culture = ExportLabels.Culture(lang);
        var text = ExportLabels.Date(lang, Sunday, TimeZoneInfo.Utc);
        foreach (var day in culture.DateTimeFormat.DayNames)
            Assert.DoesNotContain(day, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("18", text);
        Assert.Contains("2026", text);
        Assert.Contains(culture.DateTimeFormat.GetMonthName(10), text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void ExportDates_HaveNoWeekday_InAnyLanguage(string lang)
    {
        var culture = ExportLabels.Culture(lang);
        var text = ExportLabels.Date(lang, Sunday, TimeZoneInfo.Utc);
        foreach (var day in culture.DateTimeFormat.DayNames)
            Assert.DoesNotContain(day, text, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    public static IEnumerable<object[]> AllLanguages => Languages.Select(l => new object[] { l });

    [Fact]
    public void ExportDates_ReadInsideTheSentence()
    {
        Assert.Equal("Si rinnova il 18 ottobre 2026", ExportLabels.Format("it", "exp_renews_on", ExportLabels.Date("it", Sunday, TimeZoneInfo.Utc)));
        Assert.Equal("Renova em 18 de outubro de 2026", ExportLabels.Format("pt", "exp_renews_on", ExportLabels.Date("pt", Sunday, TimeZoneInfo.Utc)));
        Assert.Equal("October 18, 2026", ExportLabels.Date("en", Sunday, TimeZoneInfo.Utc));
        Assert.Equal("2026年10月18日", ExportLabels.Date("zh", Sunday, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ExportDates_KeepTheCulturesOwnOrder()
    {
        // Only the weekday goes: the rest of each culture's long date pattern is used as it is (no generic fallback).
        foreach (var lang in Languages)
        {
            var pattern = ExportLabels.Culture(lang).DateTimeFormat.LongDatePattern;
            var stripped = ExportLabels.LongDateWithoutWeekday(ExportLabels.Culture(lang));
            _out.WriteLine($"{lang}: {pattern} -> {stripped} = {ExportLabels.Date(lang, Sunday, TimeZoneInfo.Utc)}");
            Assert.True(stripped.Length < pattern.Length || !pattern.Contains("ddd"), $"{lang}: {pattern} -> {stripped}");
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public L10nDateAndBidiTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("dddd, MMMM d, yyyy", "MMMM d, yyyy")]
    [InlineData("dddd d MMMM yyyy", "d MMMM yyyy")]
    [InlineData("dddd, d. MMMM yyyy", "d. MMMM yyyy")]
    [InlineData("dddd, d 'de' MMMM 'de' yyyy", "d 'de' MMMM 'de' yyyy")]
    [InlineData("yyyy年M月d日dddd", "yyyy年M月d日")]
    [InlineData("d MMMM yyyy, dddd", "d MMMM yyyy")]
    [InlineData("d MMMM yyyy", "d MMMM yyyy")]
    public void LongDatePattern_LosesOnlyTheWeekday(string pattern, string expected)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.LongDatePattern = pattern;
        Assert.Equal(expected, ExportLabels.LongDateWithoutWeekday(culture));
    }

    [Fact]
    public void ArabicFormattingCulture_IsGregorian()
    {
        var ar = LanguageRegistry.FormattingCulture("ar");
        Assert.Equal("ar-SA", ar.Name);
        Assert.IsAssignableFrom<GregorianCalendar>(ar.DateTimeFormat.Calendar);
        Assert.True(ar.IsReadOnly);
        var date = new DateTime(2026, 10, 18);
        Assert.Contains("2026", date.ToString("D", ar));
        Assert.Contains("2026", date.ToString("d", ar));
        Assert.Contains("2026", date.ToString("g", ar));
        Assert.Contains("2026", ExportLabels.Date("ar", Sunday, TimeZoneInfo.Utc));
        Assert.DoesNotContain("1448", date.ToString("D", ar));
        // The shared tables still name the culture ar-SA; only the calendar changes.
        Assert.Equal("ar-SA", LanguageRegistry.Find("ar")!.CultureName);
    }

    [Fact]
    public void PersianFormattingCulture_KeepsItsCalendar()
    {
        var fa = LanguageRegistry.FormattingCulture("fa");
        Assert.Equal(CultureInfo.GetCultureInfo("fa-IR").DateTimeFormat.Calendar.GetType(), fa.DateTimeFormat.Calendar.GetType());
    }

    [Fact]
    public void FormattingCulture_OtherLanguagesUseTheirRegisteredCulture()
    {
        foreach (var l in LanguageRegistry.All.Where(l => l.Code != "ar"))
            Assert.Equal(l.CultureName, LanguageRegistry.FormattingCulture(l.Code).Name);
        Assert.Equal("en-US", LanguageRegistry.FormattingCulture("xx").Name);
        Assert.Same(LanguageRegistry.FormattingCulture("ar"), LanguageRegistry.FormattingCulture("ar-EG"));
    }

    [Fact]
    public void AccountPage_IsolatesVersionsAndEmailsInLocalizedSentences()
    {
        var cs = File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", "HomeWindow.Account.cs"));
        Assert.Contains("\"\\u2066\" + text + \"\\u2069\"", cs);
        Assert.Contains("F(\"acct_device_meta\", LtrIsolated(d.ClientVersion),", cs);
        Assert.Contains("F(\"acct_signed_in_email\", LtrIsolated(email))", cs);
        Assert.Contains("F(\"acct_confirm_email_sent\", LtrIsolated(email))", cs);
        Assert.Contains("F(\"legal_support_email\", LtrIsolated(mail))", cs);
        Assert.Contains("F(\"switch_confirm_message\", LtrIsolated(previous))", cs);
        Assert.Contains("F(sub, LtrIsolated(s.PreviousEmail))", cs);
        // Billing and device dates use the Gregorian Arabic culture, not Loc.Culture (ar-SA on Umm al-Qura).
        Assert.DoesNotContain("Loc.Culture", cs);
    }

    [Fact]
    public void ArabicUiFormatting_EverywhereUsesTheGregorianCalendar()
    {
        // Loc.Format and Loc.Culture (billing "ends on", report times) share the formatting culture of the exports
        var c = LanguageRegistry.FormattingCulture("ar");
        Assert.IsType<GregorianCalendar>(c.DateTimeFormat.Calendar);
        Assert.Contains("2026", string.Format(c, "{0:d}", Sunday));
        Assert.IsNotType<GregorianCalendar>(LanguageRegistry.FormattingCulture("fa").DateTimeFormat.Calendar);
        var loc = File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.Core", "Languages", "Loc.cs"));
        Assert.Contains("public static CultureInfo Culture => LanguageRegistry.FormattingCulture(_language);", loc);
        Assert.Contains("var culture = LanguageRegistry.FormattingCulture(code);", loc);
    }
}
