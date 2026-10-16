using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace InterviewAssistant.Backend.Tests;

/// <summary>
/// Regression tests for the RC2 security, billing and data-integrity audit findings: metering cannot be bypassed by
/// skipping heartbeats, stale subscription rows stop granting access, unmapped prices are retried, late events for an
/// older subscription cannot clobber a newer one, sensitive account actions need a recent sign-in, deletion is atomic
/// and blocked by a resumable subscription, and anonymous sign-ins cannot create accounts.
/// </summary>
public class Rc2BackendHardeningTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public Rc2BackendHardeningTests(ApiFactory f) => _f = f;

    private async Task<(Guid Id, HttpClient C)> NewUser() { var id = Guid.NewGuid(); var c = _f.ClientFor(id); (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode(); return (id, c); }
    private static async Task<AccountDto> Me(HttpClient c) => await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();
    private Task<HttpResponseMessage> Event(string type, object data, DateTimeOffset? at = null) => _f.PaddleEvent("evt_" + Guid.NewGuid().ToString("n"), type, data, at);
    private async Task<int> LiveSeconds(Guid user)
    {
        using var scope = _f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Usage.Where(u => u.UserId == user && u.Kind == UsageService.LiveSeconds).SumAsync(u => u.Quantity);
    }
    private async Task<int> Expire()
    {
        using var scope = _f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SessionService>().ExpireStaleAsync(default);
    }

    // ---------------- metering ----------------

    [Fact]
    public async Task LeaseThatNeverHeartbeats_IsBilledWhenItExpires()
    {
        var (id, c) = await NewUser();
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        _f.Time.Advance(TimeSpan.FromSeconds(120));             // client used the credential and never sent a heartbeat
        Assert.True(await Expire() >= 1);
        Assert.Equal(90, await LiveSeconds(id));                 // bounded by the 90 s heartbeat timeout — not zero
        var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(1))).Read<HeartbeatResponse>();
        Assert.False(hb.Active);
    }

    [Fact]
    public async Task StretchedHeartbeats_AreBilledForTheRealElapsedTime()
    {
        var (id, c) = await NewUser();
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        for (int i = 1; i <= 3; i++)
        {
            _f.Time.Advance(TimeSpan.FromSeconds(85));             // a modified client heartbeats just inside the timeout
            (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(i))).EnsureSuccessStatusCode();
        }
        Assert.Equal(3 * 85, await LiveSeconds(id));             // RC1 billed at most 60 s per heartbeat here
    }

    [Fact]
    public async Task HeartbeatJustAfterTheSweepCutoff_WinsAgainstTheSweeper()
    {
        var (_, c) = await NewUser();
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        _f.Time.Advance(TimeSpan.FromSeconds(30));
        (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(1))).EnsureSuccessStatusCode();
        await Expire();                                          // the lease is fresh: never expired by the sweep
        var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(2))).Read<HeartbeatResponse>();
        Assert.True(hb.Active);
    }

    [Fact]
    public async Task FrequentPauseResume_NeverHitsTheStartLimit_ButAbandonedLeasesDo()
    {
        var (_, c) = await NewUser();
        var dev = (await c.RegisterDevice()).Id;
        for (int i = 0; i < 25; i++)                               // a candidate pausing during each of 25 answers (RC2 draft capped at 20)
        {
            var s = await (await c.StartSession(dev)).Read<StartSessionResponse>();
            _f.Time.Advance(TimeSpan.FromSeconds(20));
            (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(1))).EnsureSuccessStatusCode();
            (await c.PostAsync($"/api/v1/sessions/{s.SessionId}/stop", null)).EnsureSuccessStatusCode();
        }

        var (_, a) = await NewUser();
        var device = (await a.RegisterDevice()).Id;
        HttpResponseMessage? refused = null;
        for (int i = 0; i < 12 && refused == null; i++)          // a client that mints credentials and never heartbeats
        {
            var r = await a.StartSession(device);
            if (r.StatusCode == HttpStatusCode.TooManyRequests) { refused = r; break; }
            r.EnsureSuccessStatusCode();
            _f.Time.Advance(TimeSpan.FromSeconds(100));
            await Expire();
        }
        Assert.NotNull(refused);
        Assert.Equal("session_start_rate", (await refused!.Error()).Code);
    }

    // ---------------- entitlements / billing ----------------

    [Fact]
    public async Task ActiveSubscriptionWhosePeriodEndedLongAgo_NoLongerGrantsPro()
    {
        var (id, c) = await NewUser();
        var now = _f.Time.GetUtcNow();
        (await Event("subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_old" + id.ToString("n"), start: now.AddDays(-40), end: now.AddDays(-10)))).EnsureSuccessStatusCode();
        var me = await Me(c);
        Assert.Equal("trial", me.Entitlements.PlanCode);        // no renewal webhook for 10 days: not paid access
        Assert.Equal(BillingStates.Expired, me.BillingState);

        (await Event("subscription.updated", Helpers.SubscriptionData(id, "active", sub: "sub_old" + id.ToString("n"), start: now.AddDays(-1), end: now.AddDays(29)), now.AddMinutes(1))).EnsureSuccessStatusCode();
        Assert.Equal("pro", (await Me(c)).Entitlements.PlanCode);   // renewal arrives → Pro again
    }

    [Fact]
    public async Task RenewalWebhookWithinTheGrace_KeepsPro()
    {
        var (id, c) = await NewUser();
        var now = _f.Time.GetUtcNow();
        (await Event("subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_g" + id.ToString("n"), start: now.AddDays(-31), end: now.AddDays(-1)))).EnsureSuccessStatusCode();
        Assert.Equal("pro", (await Me(c)).Entitlements.PlanCode);   // one day late: still within the renewal grace
    }

    [Fact]
    public async Task UnmappedPrice_IsNotRecordedSoPaddleRetriesItAfterTheMappingIsFixed()
    {
        var (id, c) = await NewUser();
        var eventId = "evt_" + Guid.NewGuid().ToString("n");
        var now = _f.Time.GetUtcNow();
        var r = await _f.PaddleEvent(eventId, "subscription.activated", Helpers.SubscriptionData(id, "active", priceId: "pri_not_configured", sub: "sub_u" + id.ToString("n"), start: now.AddDays(-1), end: now.AddDays(29)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        using var scope = _f.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebhookEvents.AnyAsync(e => e.EventId == eventId));
        Assert.Equal("trial", (await Me(c)).Entitlements.PlanCode);
    }

    [Fact]
    public async Task LateEventFromAnOlderSubscription_DoesNotCancelTheNewOne()
    {
        var (id, c) = await NewUser();
        var now = _f.Time.GetUtcNow();
        (await Event("subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_new" + id.ToString("n"), start: now.AddDays(-1), end: now.AddDays(29)), now)).EnsureSuccessStatusCode();
        var late = await Event("subscription.canceled", Helpers.SubscriptionData(id, "canceled", sub: "sub_prev" + id.ToString("n"), start: now.AddDays(-60), end: now.AddDays(-30)), now.AddMinutes(5));
        Assert.Contains("ignored", await late.Content.ReadAsStringAsync());
        var me = await Me(c);
        Assert.Equal("pro", me.Entitlements.PlanCode);
        Assert.Equal(BillingStates.Active, me.BillingState);
    }

    [Fact]
    public async Task WebhookWithoutOccurredAt_IsRejected()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { event_id = "evt_" + Guid.NewGuid().ToString("n"), event_type = "subscription.updated", data = new { id = "sub_x" } });
        var r = await _f.PaddleRaw(body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---------------- sensitive account actions ----------------

    private HttpClient ClientWithAuthAt(Guid id, DateTimeOffset authenticatedAt)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Token(id, authenticatedAt: authenticatedAt));
        c.DefaultRequestHeaders.Add("X-Client-Version", "2.1.0-rc.2");
        return c;
    }

    [Fact]
    public async Task ExportAndDelete_RequireARecentSignIn_ServerSide()
    {
        var (id, _) = await NewUser();
        var stale = ClientWithAuthAt(id, _f.Time.GetUtcNow().AddHours(-2));   // e.g. a leaked, refreshed token
        var export = await stale.GetAsync("/api/v1/me/export");
        Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
        Assert.Equal("reauth_required", (await export.Error()).Code);
        var delete = await stale.DeleteAsync("/api/v1/me");
        Assert.Equal("reauth_required", (await delete.Error()).Code);
        Assert.Equal("active", (await Me(_f.ClientFor(id))).Status);           // nothing was deleted

        var fresh = ClientWithAuthAt(id, _f.Time.GetUtcNow().AddMinutes(-1)); // just re-entered the password
        (await fresh.GetAsync("/api/v1/me/export")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await fresh.DeleteAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task TokenWithoutAuthenticationTime_CannotExport()
    {
        var id = Guid.NewGuid();
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Token(id, extraClaims: new Dictionary<string, object> { ["amr"] = Array.Empty<object>() }));
        (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();
        Assert.Equal("reauth_required", (await (await c.GetAsync("/api/v1/me/export")).Error()).Code);
    }

    [Fact]
    public async Task PausedSubscription_BlocksAccountDeletion_AndNothingIsDeleted()
    {
        var (id, c) = await NewUser();
        await c.RegisterDevice();
        (await Event("subscription.paused", Helpers.SubscriptionData(id, "paused", sub: "sub_p" + id.ToString("n")))).EnsureSuccessStatusCode();
        var r = await c.DeleteAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("subscription_active", (await r.Error()).Code);
        Assert.Single(await (await c.GetAsync("/api/v1/devices")).Read<List<DeviceDto>>());   // transaction rolled back
    }

    [Fact]
    public async Task AnonymousSignIns_CannotCreateAccounts()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.Token(Guid.NewGuid(), extraClaims: new Dictionary<string, object> { ["is_anonymous"] = true }));
        var r = await c.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("anonymous_not_allowed", (await r.Error()).Code);
    }
}
