using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Reports;

/// <summary>
/// Labels and human formatting for the readable exports (account data, profile). The texts live in the shared
/// localization tables (Languages/i18n, keys "exp_*") so the coverage tests check all 10 languages; <see cref="Keys"/>
/// lists every label the renderers use, like <see cref="ReportLabels"/> does for interview reports.
/// </summary>
public static class ExportLabels
{
    public static readonly string[] Keys =
    {
        "exp_acct_title", "exp_acct_intro", "exp_no_secrets", "exp_section_account", "exp_email", "exp_account_status", "exp_member_since",
        "exp_status_active", "exp_status_disabled", "exp_status_deleted", "exp_status_unknown",
        "exp_section_plan", "exp_plan", "exp_billing_state", "exp_period_end", "exp_renews_on", "exp_ends_on",
        "exp_sub_none", "exp_sub_trialing", "exp_sub_active", "exp_sub_past_due", "exp_sub_paused", "exp_sub_canceled", "exp_sub_unknown",
        "exp_section_usage", "exp_usage_live", "exp_usage_answers", "exp_usage_prep",
        "exp_section_devices", "exp_device_name", "exp_device_platform", "exp_device_added", "exp_device_last_seen", "exp_device_state",
        "exp_device_active", "exp_device_removed",
        "exp_section_sessions", "exp_col_date", "exp_col_duration", "exp_col_metered", "exp_col_status",
        "exp_end_active", "exp_end_client_stop", "exp_end_heartbeat_timeout", "exp_end_allowance_exhausted", "exp_end_daily_cap",
        "exp_end_global_cap", "exp_end_max_duration", "exp_end_device_revoked", "exp_end_replaced_on_device", "exp_end_other",
        "exp_section_documents", "exp_docs_profile", "exp_docs_target", "exp_docs_report", "exp_docs_other", "exp_last_updated",
        "exp_none", "exp_technical", "exp_technical_note", "exp_tech_item", "exp_tech_id", "exp_tech_format", "exp_tech_account",
        "exp_tech_session", "exp_tech_document", "exp_footer",
        "exp_dur_hr", "exp_dur_min", "exp_dur_sec",
        "exp_prof_title", "exp_prof_intro", "exp_location", "exp_preferred_name", "exp_prof_counts",
        "exp_section_experience", "exp_prof_other_experience", "exp_section_answers", "exp_section_review", "exp_review_note",
        "exp_badge_to_review", "exp_source", "exp_confirmed_on", "exp_section_source_docs", "exp_doc_added",
        "exp_tech_profile", "exp_tech_fact",
    };

    public static string Get(string? lang, string key) => Loc.Get(lang, key);
    public static string Format(string? lang, string key, params object?[] args) => Loc.Format(lang, key, args);

    /// <summary>True when every export label has a non-empty text in <paramref name="lang"/> (not the English fallback).</summary>
    public static bool IsComplete(string lang)
    {
        var t = Loc.TableFor(lang);
        return LanguageRegistry.Find(lang)?.Code == lang && Keys.All(k => t.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
    }

    public static string Code(string? lang) => LanguageRegistry.Find(lang)?.Code ?? "en";
    public static bool IsRtl(string? lang) => LanguageRegistry.IsRtl(Code(lang));

    /// <summary>
    /// Culture for numbers and dates (<see cref="LanguageRegistry.FormattingCulture"/>): Persian keeps its own (Solar
    /// Hijri) calendar; Arabic uses the Gregorian calendar so dates match payment receipts.
    /// </summary>
    public static CultureInfo Culture(string? lang) => LanguageRegistry.FormattingCulture(Code(lang));

    /// <summary>"0 sec", "59 sec", "1 hr 20 min 43 sec" (localized units; zero parts left out).</summary>
    public static string Duration(string? lang, long seconds)
    {
        if (seconds < 0) seconds = 0;
        var h = seconds / 3600; var m = seconds % 3600 / 60; var s = seconds % 60;
        var parts = new List<string>();
        if (h > 0) parts.Add(Format(lang, "exp_dur_hr", h));
        if (m > 0) parts.Add(Format(lang, "exp_dur_min", m));
        if (s > 0 || parts.Count == 0) parts.Add(Format(lang, "exp_dur_sec", s));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Calendar date in the user's culture and time zone (the stored value is UTC): the culture's long date without the
    /// weekday ("18 ottobre 2026", not "domenica 18 ottobre 2026"), so it reads inside sentences like "Si rinnova il {0}".
    /// </summary>
    public static string Date(string? lang, DateTime utc, TimeZoneInfo? zone = null)
    {
        var culture = Culture(lang);
        return Local(utc, zone).ToString(LongDateWithoutWeekday(culture), culture);
    }

    private static readonly ConcurrentDictionary<string, string> LongDatePatterns = new();

    /// <summary>
    /// <see cref="DateTimeFormatInfo.LongDatePattern"/> with the weekday ("ddd"/"dddd") and the separator after it (or
    /// before it, at the end) removed: "dddd, MMMM d, yyyy" → "MMMM d, yyyy", "dddd, d 'de' MMMM 'de' yyyy" →
    /// "d 'de' MMMM 'de' yyyy", "yyyy年M月d日dddd" → "yyyy年M月d日". Quoted literals are kept as they are. Falls back to
    /// "d MMMM yyyy" when what is left has no day, month and year.
    /// </summary>
    public static string LongDateWithoutWeekday(CultureInfo culture)
    {
        var dtf = culture.DateTimeFormat;
        return LongDatePatterns.GetOrAdd(dtf.LongDatePattern, StripWeekday);
    }

    private static string StripWeekday(string pattern)
    {
        // Split into quoted literals (kept) and pattern runs; drop each run of 3+ 'd' with its adjacent separator.
        var parts = new List<(string Text, bool Literal)>();
        var sb = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '\'' or '"')
            {
                if (sb.Length > 0) { parts.Add((sb.ToString(), false)); sb.Clear(); }
                var end = pattern.IndexOf(c, i + 1);
                if (end < 0) end = pattern.Length - 1;
                parts.Add((pattern[i..(end + 1)], true));
                i = end;
                continue;
            }
            if (c == '\\' && i + 1 < pattern.Length) { sb.Append(c).Append(pattern[++i]); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add((sb.ToString(), false));

        var removed = false;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Literal || !WeekdayRun.IsMatch(parts[i].Text)) continue;
            parts[i] = (WeekdayRun.Replace(parts[i].Text, ""), false);
            removed = true;
        }
        if (!removed) return pattern;
        // A weekday at the end leaves its separator before it ("d MMMM yyyy, dddd"): trim it.
        var result = string.Concat(parts.Select(p => p.Text)).Trim().TrimEnd(',', '\u060C', ' ', '\u00A0').Trim();
        var unquoted = string.Concat(parts.Where(p => !p.Literal).Select(p => p.Text));
        return unquoted.Contains('d') && unquoted.Contains('M') && unquoted.Contains('y') ? result : "d MMMM yyyy";
    }

    /// <summary>A weekday field ("ddd" or "dddd", not part of a longer run) and the separator that follows it.</summary>
    private static readonly Regex WeekdayRun = new(@"(?<!d)d{3,4}(?!d)[\s\u00A0,\u060C.]*", RegexOptions.Compiled);

    /// <summary>Date and time in the user's culture and time zone.</summary>
    public static string DateAndTime(string? lang, DateTime utc, TimeZoneInfo? zone = null) => Local(utc, zone).ToString("g", Culture(lang));

    /// <summary>Short numeric date (tables).</summary>
    public static string ShortDate(string? lang, DateTime utc, TimeZoneInfo? zone = null) => Local(utc, zone).ToString("d", Culture(lang));

    public static string Number(string? lang, long n) => n.ToString("N0", Culture(lang));

    private static System.DateTime Local(System.DateTime utc, TimeZoneInfo? zone)
    {
        var u = utc.Kind == DateTimeKind.Unspecified ? System.DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(u, zone ?? TimeZoneInfo.Local);
    }

    /// <summary>Shared page skeleton: self-contained, printable, no scripts or remote content, mirrored for RTL languages.</summary>
    internal static void Head(System.Text.StringBuilder sb, string lang, string title)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var code = Code(lang);
        sb.Append($"<!doctype html><html lang=\"{E(code)}\" dir=\"{(IsRtl(code) ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>{E(title)}</title><style>");
        sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;max-width:860px;margin:32px auto;padding:0 20px;color:#1b1f24;line-height:1.5}");
        sb.Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;border-bottom:1px solid #e3e6ea;padding-bottom:4px}h3{font-size:14px;margin:16px 0 4px}");
        sb.Append(".meta{color:#5b6470;font-size:13px}.note{background:#f3f6fb;border:1px solid #d5deeb;border-radius:8px;padding:10px 14px;font-size:13px;margin:16px 0}");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px;margin:6px 0}th,td{text-align:start;padding:6px 8px;border-bottom:1px solid #e3e6ea;vertical-align:top}th{color:#5b6470;font-weight:600}");
        sb.Append("dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:6px 0;font-size:14px}dt{color:#5b6470}dd{margin:0}");
        sb.Append("ul{margin:6px 0;padding-inline-start:22px}li{margin:3px 0}.src{color:#5b6470;font-size:12px}.badge{display:inline-block;font-size:11px;font-weight:600;color:#8a5a00;background:#fff7e6;border:1px solid #f2d091;border-radius:6px;padding:0 6px;margin-inline-start:6px}");
        sb.Append("details{margin:28px 0 8px;font-size:12px;color:#5b6470}summary{cursor:pointer;font-size:13px}code,.ltr{font-family:Consolas,monospace;direction:ltr;unicode-bidi:isolate}bdi{unicode-bidi:isolate}");
        sb.Append("@media print{body{margin:0}}</style></head><body>");
    }
}
