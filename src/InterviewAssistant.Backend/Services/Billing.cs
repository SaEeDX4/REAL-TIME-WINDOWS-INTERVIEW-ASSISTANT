using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>Only what the product uses. One production implementation (Paddle, Merchant of Record).</summary>
public interface IBillingProvider
{
    string Name { get; }
    Task<string> CreateCheckoutUrlAsync(UserAccount user, PlanDefinition plan, string? existingCustomerId, CancellationToken ct);
    Task<string> CreatePortalUrlAsync(string customerId, string? subscriptionId, CancellationToken ct);
}

public sealed class PaddleBillingProvider : IBillingProvider
{
    private readonly HttpClient _http;
    private readonly PaddleOptions _opt;
    public string Name => "paddle";
    public PaddleBillingProvider(HttpClient http, IOptions<PaddleOptions> opt) { _http = http; _opt = opt.Value; }

    private HttpRequestMessage Req(HttpMethod m, string path, JsonNode? body)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) throw new ApiException(503, "billing_not_configured", "Billing is not configured on the server.");
        var r = new HttpRequestMessage(m, _opt.ApiBaseUrl.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        if (body != null) r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return r;
    }

    /// <summary>Creates a Paddle transaction; Paddle returns a hosted checkout URL (card data never touches us).</summary>
    public async Task<string> CreateCheckoutUrlAsync(UserAccount user, PlanDefinition plan, string? existingCustomerId, CancellationToken ct)
    {
        var price = plan.PaddlePriceIds.FirstOrDefault() ?? throw ApiException.BadRequest("plan_not_purchasable", "This plan cannot be purchased.");
        var body = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["price_id"] = price, ["quantity"] = 1 }),
            ["custom_data"] = new JsonObject { ["user_id"] = user.Id.ToString() },
        };
        if (existingCustomerId != null) body["customer_id"] = existingCustomerId;
        using var resp = await _http.SendAsync(Req(HttpMethod.Post, "/transactions", body), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!resp.IsSuccessStatusCode) throw new ApiException(502, "checkout_failed", "Checkout could not be created.");
        return json?["data"]?["checkout"]?["url"]?.GetValue<string>() ?? throw new ApiException(502, "checkout_failed", "Checkout URL missing (configure a default payment link in Paddle).");
    }

    public async Task<string> CreatePortalUrlAsync(string customerId, string? subscriptionId, CancellationToken ct)
    {
        var body = new JsonObject();
        if (subscriptionId != null) body["subscription_ids"] = new JsonArray(subscriptionId);
        using var resp = await _http.SendAsync(Req(HttpMethod.Post, $"/customers/{Uri.EscapeDataString(customerId)}/portal-sessions", body), ct);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!resp.IsSuccessStatusCode) throw new ApiException(502, "portal_failed", "Customer portal could not be opened.");
        return json?["data"]?["urls"]?["general"]?["overview"]?.GetValue<string>() ?? throw new ApiException(502, "portal_failed", "Portal URL missing.");
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

/// <summary>Processes verified Paddle events idempotently and syncs subscription state → entitlements.</summary>
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
        JsonNode root;
        try { root = JsonNode.Parse(rawBody) ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.BadRequest("invalid_payload", "Invalid JSON."); }
        var eventId = root["event_id"]?.GetValue<string>() ?? throw ApiException.BadRequest("invalid_payload", "event_id missing");
        var type = root["event_type"]?.GetValue<string>() ?? "";
        var occurred = DateTime.TryParse(root["occurred_at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var o) ? o : _time.GetUtcNow().UtcDateTime;
        var now = _time.GetUtcNow().UtcDateTime;

        if (await _db.WebhookEvents.AnyAsync(e => e.EventId == eventId, ct)) return Outcome.Duplicate;
        var evt = new WebhookEvent { EventId = eventId, Provider = "paddle", EventType = type, OccurredUtc = occurred, ReceivedUtc = now, PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))) };
        _db.WebhookEvents.Add(evt);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return Outcome.Duplicate; } // concurrent delivery of the same event

        var data = root["data"];
        var outcome = Outcome.Ignored;
        if (type.StartsWith("subscription.") && data != null) outcome = await ApplySubscriptionAsync(data, occurred, ct) ? Outcome.Processed : Outcome.Ignored;
        else if (type == "transaction.completed" && data != null) outcome = await LinkCustomerAsync(data, ct) ? Outcome.Processed : Outcome.Ignored;
        evt.Status = outcome == Outcome.Processed ? "processed" : "ignored";
        evt.ProcessedUtc = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return outcome;
    }

    private async Task<bool> ApplySubscriptionAsync(JsonNode data, DateTime occurred, CancellationToken ct)
    {
        var subId = data["id"]?.GetValue<string>();
        var customerId = data["customer_id"]?.GetValue<string>();
        var userIdText = data["custom_data"]?["user_id"]?.GetValue<string>();
        var sub = subId == null ? null : await _db.Subscriptions.FirstOrDefaultAsync(s => s.ProviderSubscriptionId == subId, ct);
        if (sub == null && Guid.TryParse(userIdText, out var uid) && await _db.Users.AnyAsync(u => u.Id == uid, ct))
            sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == uid, ct) ?? AddSub(uid);
        if (sub == null && customerId != null) sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ProviderCustomerId == customerId, ct);
        if (sub == null) { _log.LogWarning("Subscription event for unknown user (subscription {Sub})", subId); return false; }
        if (sub.LastEventOccurredUtc != null && occurred < sub.LastEventOccurredUtc) return false; // out-of-order: older than applied state

        var status = data["status"]?.GetValue<string>() ?? sub.Status;
        var priceId = data["items"]?[0]?["price"]?["id"]?.GetValue<string>();
        var plan = _plans.ByPaddlePrice(priceId);
        sub.ProviderSubscriptionId = subId ?? sub.ProviderSubscriptionId;
        sub.ProviderCustomerId = customerId ?? sub.ProviderCustomerId;
        if (plan != null) sub.PlanCode = plan.Code;
        var prevStatus = sub.Status;
        sub.Status = status;
        sub.CurrentPeriodStartUtc = ParseDate(data["current_billing_period"]?["starts_at"]) ?? sub.CurrentPeriodStartUtc;
        sub.CurrentPeriodEndUtc = ParseDate(data["current_billing_period"]?["ends_at"]) ?? sub.CurrentPeriodEndUtc;
        sub.CancelAtPeriodEnd = data["scheduled_change"]?["action"]?.GetValue<string>() == "cancel";
        if (status == "past_due" && prevStatus != "past_due") sub.GraceUntilUtc = occurred.AddDays(_opt.PastDueGraceDays);
        if (status is "active" or "trialing") sub.GraceUntilUtc = null;
        sub.LastEventOccurredUtc = occurred;
        sub.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        return true;
    }

    private async Task<bool> LinkCustomerAsync(JsonNode data, CancellationToken ct)
    {
        if (!Guid.TryParse(data["custom_data"]?["user_id"]?.GetValue<string>(), out var uid) || !await _db.Users.AnyAsync(u => u.Id == uid, ct)) return false;
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == uid, ct) ?? AddSub(uid);
        sub.ProviderCustomerId = data["customer_id"]?.GetValue<string>() ?? sub.ProviderCustomerId;
        sub.ProviderSubscriptionId = data["subscription_id"]?.GetValue<string>() ?? sub.ProviderSubscriptionId;
        sub.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        return true;
    }

    private Subscription AddSub(Guid uid)
    {
        var s = new Subscription { Id = Guid.NewGuid(), UserId = uid, Provider = "paddle", UpdatedUtc = _time.GetUtcNow().UtcDateTime };
        _db.Subscriptions.Add(s);
        return s;
    }

    private static DateTime? ParseDate(JsonNode? n) =>
        n != null && DateTime.TryParse(n.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
