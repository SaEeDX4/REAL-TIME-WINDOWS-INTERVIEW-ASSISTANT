using System.Net.Http;
using InterviewAssistant.Client;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.UiState;

/// <summary>
/// One mapping from a failed service call to the words the user sees. Plain class (no WPF) so every case — offline,
/// timeout, one endpoint failing, 5xx, cancellation, plan and quota states — is unit-tested.
/// </summary>
public static class ErrorCopy
{
    /// <summary>
    /// User-facing text for any service error, mapped by error code and HTTP status (401/403/404/409/429/5xx, network,
    /// timeout, device, plan, quota, maintenance, minimum version) — never a raw exception, never "Unknown". A short
    /// reference id is appended for support (the full correlation id is in the log).
    /// <paramref name="includeReference"/> = false for inline section messages: the correlation id then goes to the log
    /// only (the caller logs it), the user sees plain copy.
    /// </summary>
    public static string Describe(Exception ex, bool includeReference) => includeReference ? DescribeWithReference(ex) : StripReference(DescribeWithReference(ex), ex);

    private static string StripReference(string text, Exception ex) =>
        ex is BackendException b && Ref(b) is { Length: > 0 } r && text.EndsWith(r, StringComparison.Ordinal) ? text[..^r.Length] : text;

    private static string DescribeWithReference(Exception ex) => ex switch
    {
        BackendException { Status: 0 } => Loc.T("svc_unreachable"),
        BackendException { IsUpdateRequired: true } => Loc.T("err_update_required_long"),
        BackendException { Code: "maintenance" } b => (b.Message.Length > 0 ? b.Message : Loc.T("svc_maintenance")) + Ref(b),
        BackendException { Code: "device_revoked" or "device_unknown" } b => Loc.T("svc_device_revoked") + Ref(b),
        BackendException { Code: "device_limit" } b => Loc.T("svc_device_limit") + Ref(b),
        BackendException { Code: "plan_required" or "trial_exhausted" or "subscription_expired" or "subscription_paused" } b => Loc.T("svc_plan_required") + Ref(b),
        BackendException { Code: "allowance_exhausted" } b => Loc.T("lease_allowance") + Ref(b),
        BackendException { Code: "daily_live_cap" } b => Loc.T("lease_daily_cap") + Ref(b),
        BackendException { Code: "manual_daily_cap" } b => Loc.T("svc_manual_cap") + Ref(b),
        BackendException { Code: "reauth_required" } b => Loc.T("err_reauth_unavailable") + Ref(b),
        BackendException { Code: "timeout" } b => Loc.T("svc_timeout") + Ref(b),
        BackendException { Code: "session_start_rate" } b => Loc.T("svc_session_start_rate") + Ref(b),
        BackendException { Code: "subscription_active" } b => Loc.T("svc_subscription_active") + Ref(b),
        BackendException { Code: "prep_limit" } b => Loc.T("warn_prep_limit") + Ref(b),
        BackendException { Code: "document_limit" } b => Loc.T("svc_document_limit") + Ref(b),
        BackendException { Code: "account_disabled" } b => Loc.T("acct_msg_disabled") + Ref(b),
        BackendException { Status: 401 } => Loc.T("acct_msg_expired"),
        BackendException { Status: 403 } b => Loc.T("svc_forbidden") + Ref(b),
        BackendException { Status: 404 } b => Loc.T("svc_not_found") + Ref(b),
        BackendException { Status: 409 } b => Loc.T("svc_conflict") + Ref(b),
        BackendException { Status: 429 } b => Loc.T("svc_rate_limited") + Ref(b),
        BackendException { Status: >= 500 } b => Loc.T("svc_unavailable") + Ref(b),
        BackendException b => (b.Message.Length > 0 ? b.Message : Loc.T("err_generic")) + Ref(b),
        AuthException a => a.Message,
        TimeoutException or TaskCanceledException => Loc.T("svc_timeout"),
        HttpRequestException => Loc.T("svc_unreachable"),
        // not the service's answer (a captive Wi-Fi login page, a proxy error page): the service is not reachable from here
        System.Text.Json.JsonException or NotSupportedException => Loc.T("svc_unreachable"),
        _ => Loc.T("err_generic"),
    };

    private static string Ref(BackendException b) => b.CorrelationId is { Length: >= 8 } c ? " " + Loc.F("svc_ref", c[..8]) : "";

    /// <summary>A call the user (or a page change) cancelled is not a failure: nothing is shown for it.</summary>
    public static bool IsUserCancellation(Exception ex, CancellationToken token) => ex is OperationCanceledException && token.IsCancellationRequested;
}
