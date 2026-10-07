using System.Net;
using System.Net.Http.Json;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Backend.Tests;

public class BillingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public BillingTests(ApiFactory f) => _f = f;

    private async Task<(Guid Id, HttpClient C)> NewUser() { var id = Guid.NewGuid(); var c = _f.ClientFor(id); (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode(); return (id, c); }

    [Fact]
    public async Task CheckoutReturnsHostedPaddleUrlWithUserLink()
    {
        var (id, c) = await NewUser();
        var url = await (await c.PostAsJsonAsync("/api/v1/billing/checkout", new CheckoutRequest("pro-dev"))).Read<UrlResponse>();
        Assert.StartsWith("https://sandbox-checkout.paddle.test/", url.Url);
        var sent = _f.Paddle.Requests.Last(r => r.Request.RequestUri!.AbsolutePath == "/transactions");
        Assert.Contains("pri_test_pro", sent.Body);
        Assert.Contains(id.ToString(), sent.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/billing/checkout", new CheckoutRequest("trial"))).StatusCode);
    }

    [Fact]
    public async Task SignedWebhookActivatesPaidEntitlementsAndPortalWorks()
    {
        var (id, c) = await NewUser();
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.created", Helpers.SubscriptionData(id, "active", sub: "sub_" + id.ToString("n")))).EnsureSuccessStatusCode();
        var me = await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();
        Assert.Equal("pro-dev", me.Entitlements.PlanCode);
        Assert.Equal(3, me.Entitlements.MaxProfiles);
        Assert.Equal(600 * 60, me.Usage.LiveSecondsRemaining);
        var portal = await (await c.PostAsync("/api/v1/billing/portal", null)).Read<UrlResponse>();
        Assert.Contains("customer-portal", portal.Url);
    }

    [Theory]
    [InlineData("bad-signature")]
    [InlineData("old-timestamp")]
    [InlineData("missing")]
    public async Task ForgedOrReplayedWebhooksAreRejected(string kind)
    {
        var (id, c) = await NewUser();
        HttpResponseMessage r;
        if (kind == "missing")
            r = await _f.CreateClient().PostAsync("/api/v1/webhooks/paddle", new StringContent("{\"event_id\":\"x\"}"));
        else
            r = await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.created", Helpers.SubscriptionData(id, "active"),
                secret: kind == "bad-signature" ? "wrong" : null, ts: kind == "old-timestamp" ? _f.Time.GetUtcNow().AddMinutes(-30).ToUnixTimeSeconds() : null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("trial", (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Entitlements.PlanCode);
    }

    [Fact]
    public async Task DuplicateEventsAreProcessedOnce()
    {
        var (id, _) = await NewUser();
        var evt = "evt_dup_" + Guid.NewGuid();
        var first = await (await _f.PaddleEvent(evt, "subscription.created", Helpers.SubscriptionData(id, "active", sub: "sub_d" + id.ToString("n")))).Content.ReadAsStringAsync();
        var second = await (await _f.PaddleEvent(evt, "subscription.created", Helpers.SubscriptionData(id, "active", sub: "sub_d" + id.ToString("n")))).Content.ReadAsStringAsync();
        Assert.Contains("processed", first);
        Assert.Contains("duplicate", second);
    }

    [Fact]
    public async Task PastDueKeepsAccessDuringGraceThenFallsBack()
    {
        var (id, c) = await NewUser();
        var sub = "sub_pd" + id.ToString("n");
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.activated", Helpers.SubscriptionData(id, "active", sub: sub))).EnsureSuccessStatusCode();
        _f.Time.Advance(TimeSpan.FromMinutes(1));
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.past_due", Helpers.SubscriptionData(id, "past_due", sub: sub))).EnsureSuccessStatusCode();
        var me = await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();
        Assert.Equal("pro-dev", me.Entitlements.PlanCode);
        Assert.NotNull(me.Subscription!.GraceUntilUtc);
        _f.Time.Advance(TimeSpan.FromDays(8));
        c = _f.ClientFor(id); // the old access token has expired by now
        Assert.Equal("trial", (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Entitlements.PlanCode);
    }

    [Fact]
    public async Task CancellationAtPeriodEndKeepsAccessUntilPeriodEnd()
    {
        var (id, c) = await NewUser();
        var sub = "sub_c" + id.ToString("n");
        var now = _f.Time.GetUtcNow();
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.updated", Helpers.SubscriptionData(id, "active", sub: sub, start: now.AddDays(-5), end: now.AddDays(25), scheduled: "cancel"))).EnsureSuccessStatusCode();
        var me = await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();
        Assert.True(me.Subscription!.CancelAtPeriodEnd);
        _f.Time.Advance(TimeSpan.FromMinutes(1));
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.canceled", Helpers.SubscriptionData(id, "canceled", sub: sub, start: now.AddDays(-5), end: now.AddDays(25), scheduled: "cancel"))).EnsureSuccessStatusCode();
        Assert.Equal("pro-dev", (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Entitlements.PlanCode);
        _f.Time.Advance(TimeSpan.FromDays(26));
        c = _f.ClientFor(id);
        Assert.Equal("trial", (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Entitlements.PlanCode);
    }

    [Fact]
    public async Task OutOfOrderEventsDoNotRegressState()
    {
        var (id, c) = await NewUser();
        var sub = "sub_o" + id.ToString("n");
        var t = _f.Time.GetUtcNow();
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.updated", Helpers.SubscriptionData(id, "active", sub: sub), occurred: t)).EnsureSuccessStatusCode();
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.paused", Helpers.SubscriptionData(id, "paused", sub: sub), occurred: t.AddMinutes(-5))).EnsureSuccessStatusCode();
        Assert.Equal("active", (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Subscription!.Status);
    }

    [Fact]
    public async Task AccountDeletionRequiresCancellingActiveSubscriptionFirst()
    {
        var (id, c) = await NewUser();
        (await _f.PaddleEvent("evt_" + Guid.NewGuid(), "subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_del" + id.ToString("n")))).EnsureSuccessStatusCode();
        var r = await c.DeleteAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("subscription_active", (await r.Error()).Code);
    }

    [Fact]
    public async Task ClientCannotGrantItselfPaidFeatures()
    {
        var (_, c) = await NewUser();
        // A modified client might claim a plan in its own documents — entitlements ignore client data entirely.
        (await c.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { plan = "pro-dev", maxProfiles = 99 }), null))).EnsureSuccessStatusCode();
        var second = await c.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "2nd" }), null));
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Equal("profile_limit", (await second.Error()).Code);
    }
}
