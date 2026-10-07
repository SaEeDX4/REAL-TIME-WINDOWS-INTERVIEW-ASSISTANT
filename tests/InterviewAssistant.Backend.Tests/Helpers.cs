using System.Net.Http.Json;
using System.Text.Json;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Backend.Tests;

public static class Helpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> Read<T>(this HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"HTTP {(int)r.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    public static async Task<ApiError> Error(this HttpResponseMessage r) => JsonSerializer.Deserialize<ApiError>(await r.Content.ReadAsStringAsync(), Json)!;

    public static async Task<DeviceDto> RegisterDevice(this HttpClient c, string? installation = null) =>
        await (await c.PostAsJsonAsync("/api/v1/devices", new RegisterDeviceRequest(installation ?? "inst-" + Guid.NewGuid().ToString("n"), "Test PC", "windows-x64", "2.0.0"))).Read<DeviceDto>();

    public static Task<HttpResponseMessage> StartSession(this HttpClient c, Guid device, string? key = null) =>
        c.PostAsJsonAsync("/api/v1/sessions", new StartSessionRequest(device, null, key ?? Guid.NewGuid().ToString("n"), "en"));

    public static JsonElement Doc(object o) => JsonSerializer.SerializeToElement(o, Json);

    /// <summary>Simulates a verified Paddle webhook delivery.</summary>
    public static async Task<HttpResponseMessage> PaddleEvent(this ApiFactory f, string eventId, string type, object data, DateTimeOffset? occurred = null, string? secret = null, long? ts = null)
    {
        var body = JsonSerializer.Serialize(new { event_id = eventId, event_type = type, occurred_at = (occurred ?? f.Time.GetUtcNow()).ToString("o"), data });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/paddle") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("Paddle-Signature", PaddleSignature.Sign(body, secret ?? ApiFactory.WebhookSecret, ts ?? f.Time.GetUtcNow().ToUnixTimeSeconds()));
        return await f.CreateClient().SendAsync(req);
    }

    public static object SubscriptionData(Guid userId, string status, string priceId = "pri_test_pro", DateTimeOffset? start = null, DateTimeOffset? end = null, string sub = "sub_1", string? scheduled = null) => new
    {
        id = sub, status, customer_id = "ctm_" + userId.ToString("n")[..8], custom_data = new { user_id = userId.ToString() },
        items = new[] { new { price = new { id = priceId } } },
        current_billing_period = new { starts_at = (start ?? DateTimeOffset.Parse("2026-10-01T00:00:00Z")).ToString("o"), ends_at = (end ?? DateTimeOffset.Parse("2026-11-01T00:00:00Z")).ToString("o") },
        scheduled_change = scheduled == null ? null : new { action = scheduled },
    };
}
