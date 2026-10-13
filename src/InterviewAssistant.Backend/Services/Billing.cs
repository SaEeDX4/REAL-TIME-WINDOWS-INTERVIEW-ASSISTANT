using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Only what the product uses. One production implementation (Paddle, Merchant of Record).</summary>
public interface IBillingProvider
{
    string Name { get; }
    Task<string> CreateCheckoutUrlAsync(UserAccount user, PlanDefinition plan, string? existingCustomerId, CancellationToken ct);
    Task<string> CreatePortalUrlAsync(string customerId, string? subscriptionId, CancellationToken ct);
    /// <summary>Customer-facing price text from Paddle (e.g. "USD 19.99 / month"); null if unavailable.</summary>
    Task<string?> GetPriceDisplayAsync(string priceId, CancellationToken ct);
}

public sealed class PaddleBillingProvider : IBillingProvider
{
    private readonly HttpClient _http;
    private readonly PaddleOptions _opt;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PaddleBillingProvider> _log;
    public string Name => "paddle";
    public PaddleBillingProvider(HttpClient http, IOptions<PaddleOptions> opt, IMemoryCache cache, ILogger<PaddleBillingProvider> log) { _http = http; _opt = opt.Value; _cache = cache; _log = log; }

    private HttpRequestMessage Req(HttpMethod m, string path, JsonNode? body)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) throw new ApiException(503, "billing_not_configured", "Billing is not available right now.");
        var r = new HttpRequestMessage(m, _opt.ApiBaseUrl.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        if (body != null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    private async Task<(bool Ok, int Status, JsonNode? Json)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using (req)
            using (var resp = await _http.SendAsync(req, ct))
            {
                JsonNode? json = null;
                try { json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); } catch (JsonException) { }
                return (resp.IsSuccessStatusCode, (int)resp.StatusCode, json);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Paddle API unreachable: {Error}", ex.GetType().Name);
            throw new ApiException(503, "billing_unavailable", "The billing service is temporarily unavailable. Please try again in a minute.");
        }
    }

    private void LogFailure(string op, int status, JsonNode? json) =>
        _log.LogError("Paddle {Operation} failed: HTTP {Status} {Code} {Detail}", op, status, Str(At(json, "error", "code")), Str(At(json, "error", "detail")));

    /// <summary>Creates a Paddle transaction bound to the authenticated user (custom_data.user_id) and returns the checkout URL.</summary>
    public async Task<string> CreateCheckoutUrlAsync(UserAccount user, PlanDefinition plan, string? existingCustomerId, CancellationToken ct)
    {
        var price = plan.PaddlePriceIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? throw ApiException.BadRequest("plan_not_purchasable", "This plan cannot be purchased.");
        var body = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["price_id"] = price, ["quantity"] = 1 }),
            ["custom_data"] = new JsonObject { ["user_id"] = user.Id.ToString() },
            ["collection_mode"] = "automatic",
        };
        if (existingCustomerId != null) body["customer_id"] = existingCustomerId;
        var (ok, status, json) = await SendAsync(Req(HttpMethod.Post, "/transactions", body), ct);
        if (!ok) { LogFailure("create transaction", status, json); throw new ApiException(502, "checkout_failed", "Checkout could not be started. Please try again."); }
        var url = Str(At(json, "data", "checkout", "url"));
        if (url == null)
        {
            _log.LogError("Paddle returned no checkout URL — set the default payment link (Paddle → Checkout settings) to <PublicBaseUrl>/pay");
            throw new ApiException(502, "checkout_failed", "Checkout is not available right now.");
        }
        return url;
    }

    public async Task<string> CreatePortalUrlAsync(string customerId, string? subscriptionId, CancellationToken ct)
    {
        var body = new JsonObject();
        if (subscriptionId != null) body["subscription_ids"] = new JsonArray(subscriptionId);
        var (ok, status, json) = await SendAsync(Req(HttpMethod.Post, $"/customers/{Uri.EscapeDataString(customerId)}/portal-sessions", body), ct);
        if (!ok) { LogFailure("create portal session", status, json); throw new ApiException(502, "portal_failed", "Billing management could not be opened. Please try again."); }
        return Str(At(json, "data", "urls", "general", "overview")) ?? throw new ApiException(502, "portal_failed", "Billing management could not be opened.");
    }

    public async Task<string?> GetPriceDisplayAsync(string priceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(priceId) || string.IsNullOrWhiteSpace(_opt.ApiKey)) return null;
        if (_cache.TryGetValue("price:" + priceId, out string? cached)) return cached;
        try
        {
            var (ok, status, json) = await SendAsync(Req(HttpMethod.Get, $"/prices/{Uri.EscapeDataString(priceId)}", null), ct);
            if (!ok) { LogFailure("get price", status, json); return null; }
            var display = FormatPrice(At(json, "data"));
            _cache.Set("price:" + priceId, display, TimeSpan.FromHours(1));
            return display;
        }
        catch (ApiException) { return null; }
    }

    internal static string? FormatPrice(JsonNode? price)
    {
        var amount = Str(At(price, "unit_price", "amount"));
        var currency = Str(At(price, "unit_price", "currency_code"));
        if (amount == null || currency == null || !long.TryParse(amount, out var minor)) return null;
        var text = $"{currency} {minor / 100m:0.00}";
        var interval = Str(At(price, "billing_cycle", "interval"));
        var frequency = At(price, "billing_cycle", "frequency") is JsonValue f && f.TryGetValue<int>(out var n) ? n : 1;
        return interval == null ? text : frequency == 1 ? $"{text} / {interval}" : $"{text} / {frequency} {interval}s";
    }

    internal static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Type-safe navigation: a key on a non-object or an index on a non-array yields null instead of throwing.</summary>
    internal static JsonNode? At(JsonNode? n, params object[] path)
    {
        foreach (var step in path)
            n = step switch
            {
                string key => n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null,
                int i => n is JsonArray a && i >= 0 && i < a.Count ? a[i] : null,
                _ => null,
            };
        return n;
    }
}

/// <summary>Paddle-Signature: "ts=...;h1=..." where h1 = hex(HMAC-SHA256(secret, ts + ":" + rawBody)). Constant-time compare + timestamp window.</summary>
public static class PaddleSignature
{
    public static bool Verify(string? header, string rawBody, string secret, DateTimeOffset now, int toleranceSeconds, out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(secret)) { reason = "webhook secret not configured"; return false; }
        if (string.IsNullOrEmpty(header)) { reason = "missing signature"; return false; }
        string? ts = null; var sigs = new List<string>();
        foreach (var part in header.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "ts") ts = kv[1].Trim();
            else if (kv[0].Trim() == "h1") sigs.Add(kv[1].Trim());
        }
        if (ts == null || sigs.Count == 0 || !long.TryParse(ts, out var unix)) { reason = "malformed signature"; return false; }
        if (Math.Abs(now.ToUnixTimeSeconds() - unix) > toleranceSeconds) { reason = "timestamp outside tolerance (possible replay)"; return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + ":" + rawBody));
        foreach (var s in sigs)
        {
            byte[] given;
            try { given = Convert.FromHexString(s); } catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(given, expected)) return true;
        }
        reason = "signature mismatch"; return false;
    }

    public static string Sign(string rawBody, string secret, long ts) =>
        $"ts={ts};h1={Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + ":" + rawBody))).ToLowerInvariant()}";
}

/// <summary>
/// Processes verified Paddle events idempotently: the event row and the resulting state change are committed in ONE
/// transaction, so a failure leaves nothing behind and Paddle's retry is processed normally (never mistaken for a duplicate).
/// </summary>
public sealed class BillingWebhookProcessor
{
    private readonly AppDbContext _db;
    private readonly PlanCatalog _plans;
    private readonly PaddleOptions _opt;
    private readonly TimeProvider _time;
    private readonly ILogger<BillingWebhookProcessor> _log;

    public BillingWebhookProcessor(AppDbContext db, IOptions<PlanCatalog> plans, IOptions<PaddleOptions> opt, TimeProvider time, ILogger<BillingWebhookProcessor> log)
    { _db = db; _plans = plans.Value; _opt = opt.Value; _time = time; _log = log; }

    public enum Outcome { Processed, Duplicate, Ignored }

    public async Task<Outcome> ProcessAsync(string rawBody, CancellationToken ct)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(rawBody); }
        catch (JsonException) { throw ApiException.BadRequest("invalid_payload", "Invalid JSON."); }
        if (root is not JsonObject) throw ApiException.BadRequest("invalid_payload", "Invalid payload.");
        var eventId = Str(At(root, "event_id"));
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 100) throw ApiException.BadRequest("invalid_payload", "event_id missing.");
        var type = Str(At(root, "event_type")) ?? "";
        var occurred = ParseDate(At(root, "occurred_at")) ?? _time.GetUtcNow().UtcDateTime;
        var now = _time.GetUtcNow().UtcDateTime;

        if (await _db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct))
        {
            _log.LogInformation("Paddle webhook {EventId} {EventType}: duplicate", eventId, type);
            return Outcome.Duplicate;
        }
        var evt = new WebhookEvent { EventId = eventId, Provider = "paddle", EventType = type.Length > 64 ? type[..64] : type, OccurredUtc = occurred, ReceivedUtc = now, PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))) };
        _db.WebhookEvents.Add(evt);

        var data = At(root, "data") as JsonObject;
        string? detail = null;
        var outcome = Outcome.Ignored;
        if (data != null && type.StartsWith("subscription.", StringComparison.Ordinal)) (outcome, detail) = await ApplySubscriptionAsync(data, occurred, ct);
        else if (data != null && type == "transaction.completed") (outcome, detail) = await LinkCustomerAsync(data, ct);
        else detail = "event type not used";
        evt.Status = outcome == Outcome.Processed ? "processed" : "ignored";
        evt.ProcessedUtc = _time.GetUtcNow().UtcDateTime;
        try { await _db.SaveChangesAsync(ct); }   // atomic: event + subscription change
        catch (DbUpdateException)
        {
            if (!await IsDuplicateAsync(eventId)) throw;   // real failure → 500 → Paddle retries; nothing was committed
            _log.LogInformation("Paddle webhook {EventId} {EventType}: duplicate (concurrent delivery)", eventId, type);
            return Outcome.Duplicate;
        }
        _log.LogInformation("Paddle webhook {EventId} {EventType}: {Outcome} {Detail}", eventId, type, evt.Status, detail);
        return outcome;
    }

    private async Task<bool> IsDuplicateAsync(string eventId)
    {
        _db.ChangeTracker.Clear();
        return await _db.WebhookEvents.AsNoTracking().AnyAsync(e => e.EventId == eventId);
    }

    private async Task<Guid?> KnownUserAsync(JsonNode? customData, CancellationToken ct) =>
        Guid.TryParse(Str(At(customData, "user_id")), out var uid) && await _db.Users.AnyAsync(u => u.Id == uid && u.Status != "deleted", ct) ? uid : null;

    private async Task<(Outcome, string)> ApplySubscriptionAsync(JsonNode data, DateTime occurred, CancellationToken ct)
    {
        var subId = Str(At(data, "id"));
        var customerId = Str(At(data, "customer_id"));
        var sub = subId == null ? null : await _db.Subscriptions.FirstOrDefaultAsync(s => s.ProviderSubscriptionId == subId, ct);
        if (sub == null && await KnownUserAsync(At(data, "custom_data"), ct) is { } uid)
            sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == uid, ct) ?? AddSub(uid);
        if (sub == null && customerId != null) sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ProviderCustomerId == customerId, ct);
        if (sub == null) return (Outcome.Ignored, "no matching user (custom_data.user_id / customer)");
        if (sub.LastEventOccurredUtc != null && occurred < sub.LastEventOccurredUtc && subId == sub.ProviderSubscriptionId)
            return (Outcome.Ignored, "older than the applied state (out of order)");

        var status = Str(At(data, "status")) ?? sub.Status;
        var priceId = Str(At(data, "items", 0, "price", "id")) ?? Str(At(data, "items", 0, "price_id"));
        var plan = _plans.ByPaddlePrice(priceId);
        if (plan == null && priceId != null)
            _log.LogError("Paddle price {PriceId} is not mapped to any plan (PlanCatalog__Plans__N__PaddlePriceIds) — subscription {SubscriptionId} keeps plan '{PlanCode}'", priceId, subId, sub.PlanCode);
        sub.ProviderSubscriptionId = subId ?? sub.ProviderSubscriptionId;
        sub.ProviderCustomerId = customerId ?? sub.ProviderCustomerId;
        if (plan != null) sub.PlanCode = plan.Code;
        var prevStatus = sub.Status;
        sub.Status = status.Length > 16 ? status[..16] : status;
        sub.CurrentPeriodStartUtc = ParseDate(At(data, "current_billing_period", "starts_at")) ?? sub.CurrentPeriodStartUtc;
        sub.CurrentPeriodEndUtc = ParseDate(At(data, "current_billing_period", "ends_at")) ?? sub.CurrentPeriodEndUtc;
        sub.CancelAtPeriodEnd = Str(At(data, "scheduled_change", "action")) == "cancel";
        if (status == "past_due" && prevStatus != "past_due") sub.GraceUntilUtc = occurred.AddDays(_opt.PastDueGraceDays);
        if (status is "active" or "trialing") sub.GraceUntilUtc = null;
        sub.LastEventOccurredUtc = occurred;
        sub.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        return (Outcome.Processed, $"user {sub.UserId} {prevStatus}->{sub.Status} plan {sub.PlanCode}");
    }

    private async Task<(Outcome, string)> LinkCustomerAsync(JsonNode data, CancellationToken ct)
    {
        if (await KnownUserAsync(At(data, "custom_data"), ct) is not { } uid) return (Outcome.Ignored, "no valid custom_data.user_id");
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == uid, ct) ?? AddSub(uid);
        sub.ProviderCustomerId = Str(At(data, "customer_id")) ?? sub.ProviderCustomerId;
        sub.ProviderSubscriptionId = Str(At(data, "subscription_id")) ?? sub.ProviderSubscriptionId;
        sub.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        return (Outcome.Processed, $"linked customer for user {uid}");
    }

    private Subscription AddSub(Guid uid)
    {
        var s = new Subscription { Id = Guid.NewGuid(), UserId = uid, Provider = "paddle", Status = "none", PlanCode = "", UpdatedUtc = _time.GetUtcNow().UtcDateTime };
        _db.Subscriptions.Add(s);
        return s;
    }

    private static string? Str(JsonNode? n) => PaddleBillingProvider.Str(n);
    private static JsonNode? At(JsonNode? n, params object[] path) => PaddleBillingProvider.At(n, path);

    private static DateTime? ParseDate(JsonNode? n) =>
        Str(n) is { } t && DateTime.TryParse(t, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
