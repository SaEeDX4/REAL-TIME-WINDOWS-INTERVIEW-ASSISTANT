using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using InterviewAssistant.Core.Persistence;

namespace InterviewAssistant.Core.Reports;

/// <summary>Plan and billing state the desktop already shows in words (from the signed-in account); optional.</summary>
public sealed record AccountExportContext(string? PlanName = null, string? BillingSummary = null);

/// <summary>
/// Readable, printable HTML of the account data export (GET me/export): account, plan and billing in words, usage as a
/// human duration, devices, live sessions and saved items, with identifiers in a collapsed "technical details" section.
/// Self-contained (no scripts, no remote content), localized, mirrored for Arabic/Persian. The service JSON first goes
/// through <see cref="SecretFilter"/> and every value is HTML-encoded.
/// </summary>
public static class AccountExportRenderer
{
    public static string Render(JsonElement export, string lang, string productName = "Interview Assistant", AccountExportContext? context = null,
        TimeZoneInfo? zone = null, DateTime? nowUtc = null)
    {
        lang = ExportLabels.Code(lang);
        var root = SecretFilter.Clean(export);
        string L(string k) => ExportLabels.Get(lang, k);
        string F(string k, params object?[] a) => ExportLabels.Format(lang, k, a);
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string D(DateTime? d) => d is { } v ? ExportLabels.Date(lang, v, zone) : L("exp_none");
        string Dt(DateTime? d) => d is { } v ? ExportLabels.DateAndTime(lang, v, zone) : L("exp_none");

        var exported = Time(root, "exportedUtc") ?? nowUtc ?? DateTime.UtcNow;
        var sb = new StringBuilder();
        ExportLabels.Head(sb, lang, L("exp_acct_title"));
        sb.Append($"<h1>{E(L("exp_acct_title"))}</h1><p class=\"meta\">{E(F("exp_acct_intro", productName, Dt(exported)))}</p>");
        sb.Append($"<p class=\"note\">{E(L("exp_no_secrets"))}</p>");

        // ---- account
        var account = Obj(root, "account");
        sb.Append($"<h2>{E(L("exp_section_account"))}</h2><dl>");
        Row(sb, L("exp_email"), $"<span class=\"ltr\">{E(Str(account, "email"))}</span>");
        Row(sb, L("exp_account_status"), E(AccountStatus(lang, Str(account, "status"))));
        Row(sb, L("exp_member_since"), E(D(Time(account, "createdUtc"))));
        sb.Append("</dl>");

        // ---- plan and billing
        var sub = Obj(root, "subscription");
        var subStatus = Str(sub, "status");
        sb.Append($"<h2>{E(L("exp_section_plan"))}</h2><dl>");
        var plan = !string.IsNullOrWhiteSpace(context?.PlanName) ? context!.PlanName! : Str(sub, "planCode");
        Row(sb, L("exp_plan"), plan.Length > 0 ? $"<bdi>{E(plan)}</bdi>" : E(L("exp_sub_none")));
        Row(sb, L("exp_billing_state"), E(!string.IsNullOrWhiteSpace(context?.BillingSummary) ? context!.BillingSummary! : SubscriptionState(lang, sub == null ? null : subStatus)));
        if (Time(sub, "currentPeriodEndUtc") is { } periodEnd)
            Row(sb, L("exp_period_end"), E(F(subStatus is "active" or "trialing" or "past_due" ? "exp_renews_on" : "exp_ends_on", D(periodEnd))));
        sb.Append("</dl>");

        // ---- usage
        var usage = Arr(root, "usage").GroupBy(u => Str(u, "kind"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(u => Long(u, "total") ?? 0), StringComparer.OrdinalIgnoreCase);
        sb.Append($"<h2>{E(L("exp_section_usage"))}</h2><dl>");
        Row(sb, L("exp_usage_live"), E(ExportLabels.Duration(lang, usage.GetValueOrDefault("live_seconds"))));
        Row(sb, L("exp_usage_answers"), E(ExportLabels.Number(lang, usage.GetValueOrDefault("answer_request"))));
        Row(sb, L("exp_usage_prep"), E(ExportLabels.Number(lang, usage.GetValueOrDefault("prep_job"))));
        sb.Append("</dl>");

        // ---- devices
        var devices = Arr(root, "devices").ToList();
        sb.Append($"<h2>{E(L("exp_section_devices"))} ({devices.Count})</h2>");
        if (devices.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        else
        {
            sb.Append($"<table><thead><tr><th>{E(L("exp_device_name"))}</th><th>{E(L("exp_device_platform"))}</th><th>{E(L("exp_device_added"))}</th><th>{E(L("exp_device_last_seen"))}</th><th>{E(L("exp_device_state"))}</th></tr></thead><tbody>");
            foreach (var d in devices)
            {
                var revoked = Time(d, "revokedUtc");
                sb.Append($"<tr><td><bdi>{E(Str(d, "name"))}</bdi></td><td><bdi>{E(Str(d, "platform"))}</bdi></td><td>{E(D(Time(d, "createdUtc")))}</td><td>{E(Dt(Time(d, "lastSeenUtc")))}</td>");
                sb.Append($"<td>{E(revoked is { } r ? F("exp_device_removed", D(r)) : L("exp_device_active"))}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        // ---- live sessions (newest first)
        var sessions = Arr(root, "sessions").OrderByDescending(s => Time(s, "startedUtc") ?? DateTime.MinValue).ToList();
        sb.Append($"<h2>{E(L("exp_section_sessions"))} ({sessions.Count})</h2>");
        if (sessions.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        else
        {
            sb.Append($"<table><thead><tr><th>{E(L("exp_col_date"))}</th><th>{E(L("exp_col_duration"))}</th><th>{E(L("exp_col_metered"))}</th><th>{E(L("exp_col_status"))}</th></tr></thead><tbody>");
            foreach (var s in sessions)
            {
                var started = Time(s, "startedUtc"); var ended = Time(s, "endedUtc");
                var duration = started is { } a && ended is { } b ? ExportLabels.Duration(lang, (long)Math.Max(0, (b - a).TotalSeconds)) : "—";
                sb.Append($"<tr><td>{E(Dt(started))}</td><td>{E(duration)}</td><td>{E(ExportLabels.Duration(lang, Long(s, "meteredSeconds") ?? 0))}</td><td>{E(EndReason(lang, ended, Str(s, "endReason")))}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        // ---- items saved in the account (counts; the content itself is in the raw data export)
        var documents = Arr(root, "documents").ToList();
        sb.Append($"<h2>{E(L("exp_section_documents"))}</h2>");
        if (documents.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        else
        {
            sb.Append("<dl>");
            foreach (var g in documents.GroupBy(d => Str(d, "kind") switch { "profile" => "exp_docs_profile", "target" => "exp_docs_target", "report" => "exp_docs_report", _ => "exp_docs_other" }).OrderBy(g => Array.IndexOf(DocumentOrder, g.Key)))
            {
                var last = g.Select(d => Time(d, "updatedUtc")).Where(t => t != null).Max();
                Row(sb, L(g.Key), E(ExportLabels.Number(lang, g.Count())) + (last is { } lu ? $" <span class=\"src\">· {E(F("exp_last_updated", D(lu)))}</span>" : ""));
            }
            sb.Append("</dl>");
        }

        // ---- technical details (identifiers only; collapsed)
        sb.Append($"<details><summary>{E(L("exp_technical"))}</summary><p>{E(L("exp_technical_note"))}</p><table><thead><tr><th>{E(L("exp_tech_item"))}</th><th>{E(L("exp_tech_id"))}</th></tr></thead><tbody>");
        void Tech(string label, string value) { if (value.Length > 0) sb.Append($"<tr><td>{E(label)}</td><td><code>{E(value)}</code></td></tr>"); }
        Tech(L("exp_tech_format"), Str(root, "format"));
        Tech(L("exp_tech_account"), Str(account, "id"));
        foreach (var s in sessions) Tech(L("exp_tech_session") + " · " + Dt(Time(s, "startedUtc")), Str(s, "id"));
        foreach (var d in documents) Tech(L("exp_tech_document") + " · " + Str(d, "kind"), Str(d, "id"));
        foreach (var (kind, total) in usage.Where(u => u.Key is not ("live_seconds" or "answer_request" or "prep_job"))) Tech(kind, total.ToString(CultureInfo.InvariantCulture));
        sb.Append("</tbody></table></details>");
        sb.Append($"<p class=\"meta\">{E(F("exp_footer", productName, Dt(nowUtc ?? exported)))}</p></body></html>");
        return sb.ToString();
    }

    private static readonly string[] DocumentOrder = { "exp_docs_profile", "exp_docs_target", "exp_docs_report", "exp_docs_other" };

    private static void Row(StringBuilder sb, string label, string htmlValue) => sb.Append($"<dt>{WebUtility.HtmlEncode(label)}</dt><dd>{htmlValue}</dd>");

    public static string AccountStatus(string lang, string status) => ExportLabels.Get(lang, status switch
    {
        "active" => "exp_status_active", "disabled" => "exp_status_disabled", "deleted" => "exp_status_deleted", _ => "exp_status_unknown",
    });

    /// <summary>Subscription row status in words; null = no subscription row at all.</summary>
    public static string SubscriptionState(string lang, string? status) => ExportLabels.Get(lang, status switch
    {
        null or "" or "none" => "exp_sub_none", "trialing" => "exp_sub_trialing", "active" => "exp_sub_active", "past_due" => "exp_sub_past_due",
        "paused" => "exp_sub_paused", "canceled" or "cancelled" => "exp_sub_canceled", _ => "exp_sub_unknown",
    });

    /// <summary>Why a live session ended, in words.</summary>
    public static string EndReason(string lang, DateTime? endedUtc, string reason) => ExportLabels.Get(lang, endedUtc == null && reason.Length == 0 ? "exp_end_active" : reason switch
    {
        "client_stop" => "exp_end_client_stop", "heartbeat_timeout" => "exp_end_heartbeat_timeout", "allowance_exhausted" => "exp_end_allowance_exhausted",
        "daily_cap" => "exp_end_daily_cap", "global_cap" => "exp_end_global_cap", "max_duration" => "exp_end_max_duration",
        "device_revoked" => "exp_end_device_revoked", "replaced_on_device" => "exp_end_replaced_on_device", _ => "exp_end_other",
    });

    // ---- tolerant JSON access (camelCase from the service; any casing accepted)
    private static JsonElement? Prop(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static JsonElement? Obj(JsonElement? e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    private static IEnumerable<JsonElement> Arr(JsonElement? e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement? e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => "",
    };

    private static long? Long(JsonElement? e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v
        : Prop(e, name) is { ValueKind: JsonValueKind.Number } d && d.TryGetDouble(out var dv) ? (long)dv : null;

    private static DateTime? Time(JsonElement? e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } s && DateTime.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
}
