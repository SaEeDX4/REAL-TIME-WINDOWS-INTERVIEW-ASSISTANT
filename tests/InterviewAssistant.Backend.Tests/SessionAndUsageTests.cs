using System.Net;
using System.Net.Http.Json;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Backend.Tests;

public class SessionAndUsageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public SessionAndUsageTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task LeaseLifecycleMetersSecondsAndNeverExposesServerKey()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var dev = await c.RegisterDevice();
        var start = await (await c.StartSession(dev.Id)).Read<StartSessionResponse>();
        Assert.StartsWith("ek_test_", start.Realtime.ClientSecret);
        Assert.DoesNotContain("sk-", start.Realtime.ClientSecret);
        Assert.Equal(30 * 60, start.RemainingSeconds);
        var secretReq = _f.OpenAi.Requests.Last(r => r.Request.RequestUri!.AbsolutePath.EndsWith("client_secrets"));
        Assert.Equal("Bearer " + ApiFactory.ServerOpenAiKey, secretReq.Request.Headers.Authorization!.ToString());
        Assert.Contains("\"type\":\"transcription\"", secretReq.Body);

        _f.Time.Advance(TimeSpan.FromSeconds(30));
        var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{start.SessionId}/heartbeat", new HeartbeatRequest(1))).Read<HeartbeatResponse>();
        Assert.True(hb.Active);
        Assert.Equal(30 * 60 - 30, hb.RemainingSeconds);
        // Long gap (client paused/offline): billed at most 2 intervals.
        _f.Time.Advance(TimeSpan.FromMinutes(10));
        hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{start.SessionId}/heartbeat", new HeartbeatRequest(2))).Read<HeartbeatResponse>();
        Assert.Equal(30 * 60 - 30 - 60, hb.RemainingSeconds);
        _f.Time.Advance(TimeSpan.FromSeconds(10));
        (await c.PostAsync($"/api/v1/sessions/{start.SessionId}/stop", null)).EnsureSuccessStatusCode();
        var me = await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();
        Assert.Equal(100, me.Usage.LiveSecondsUsed);
    }

    [Fact]
    public async Task ReplayedHeartbeatsAreRejected()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(5))).EnsureSuccessStatusCode();
        var replay = await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(5));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("replayed_heartbeat", (await replay.Error()).Code);
    }

    [Fact]
    public async Task ConcurrentSessionsAreRejectedButIdempotentRetryIsAllowed()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var dev = await c.RegisterDevice();
        var first = await (await c.StartSession(dev.Id, "key-1")).Read<StartSessionResponse>();
        var retry = await (await c.StartSession(dev.Id, "key-1")).Read<StartSessionResponse>();
        Assert.Equal(first.SessionId, retry.SessionId);                 // network retry → same lease
        var second = await c.StartSession(dev.Id, "key-2");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrent_limit", (await second.Error()).Code);
        (await c.PostAsync($"/api/v1/sessions/{first.SessionId}/stop", null)).EnsureSuccessStatusCode();
        (await c.StartSession(dev.Id, "key-3")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AllowanceExhaustionEndsTheLeaseAndBlocksNewSessions()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var dev = await c.RegisterDevice();
        var s = await (await c.StartSession(dev.Id)).Read<StartSessionResponse>();
        HeartbeatResponse hb = null!;
        for (int i = 1; i <= 40; i++) { _f.Time.Advance(TimeSpan.FromSeconds(60)); hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(i))).Read<HeartbeatResponse>(); if (!hb.Active) break; }
        Assert.False(hb.Active);
        Assert.Equal("allowance_exhausted", hb.EndReason);
        var again = await c.StartSession(dev.Id);
        Assert.Equal(HttpStatusCode.PaymentRequired, again.StatusCode);
        Assert.Equal(30 * 60, (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Usage.LiveSecondsUsed); // never over-billed
    }

    [Fact]
    public async Task StaleLeasesExpireAndRenewalRequiresActiveLease()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        (await c.PostAsync($"/api/v1/sessions/{s.SessionId}/realtime-secret", null)).EnsureSuccessStatusCode();
        _f.Time.Advance(TimeSpan.FromMinutes(5));
        using (var scope = _f.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<InterviewAssistant.Backend.Services.SessionService>().ExpireStaleAsync(default) >= 1);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/sessions/{s.SessionId}/realtime-secret", null)).StatusCode);
    }

    [Fact]
    public async Task AnswersRequireAnActiveLeaseAndStreamWithServerModel()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var msg = new[] { new AnswerMessage("system", "s"), new AnswerMessage("user", "How would you prioritise?") };
        var noLease = await c.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(Guid.NewGuid(), null, msg, 200, "live"));
        Assert.Equal(HttpStatusCode.Forbidden, noLease.StatusCode);
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        var r = await c.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(s.SessionId, null, msg, 200, "live"));
        r.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType!.MediaType);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("start with the goal", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
        var upstream = _f.OpenAi.Requests.Last(x => x.Request.RequestUri!.AbsolutePath.EndsWith("chat/completions"));
        Assert.Contains("\"model\":\"gpt-5.4-mini\"", upstream.Body);                     // server-chosen model
        Assert.Equal("Bearer " + ApiFactory.ServerOpenAiKey, upstream.Request.Headers.Authorization!.ToString());
        Assert.Equal(1, (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Usage.AnswerRequestsToday);
    }

    [Fact]
    public async Task AnswerRateLimitAndPromptBoundsAreEnforced()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
        var msg = new[] { new AnswerMessage("user", "q") };
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 7; i++) codes.Add((await c.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(s.SessionId, null, msg, 50, "live"))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);   // 5/min in tests
        var c2 = _f.ClientFor(Guid.NewGuid());
        var s2 = await (await c2.StartSession((await c2.RegisterDevice()).Id)).Read<StartSessionResponse>();
        var huge = new[] { new AnswerMessage("user", new string('x', 30_000)) };
        Assert.Equal(HttpStatusCode.BadRequest, (await c2.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(s2.SessionId, null, huge, 50, "live"))).StatusCode);
        var badRole = new[] { new AnswerMessage("tool", "x") };
        Assert.Equal(HttpStatusCode.BadRequest, (await c2.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(s2.SessionId, null, badRole, 50, "live"))).StatusCode);
    }

    [Fact]
    public async Task PreparationJobsAreQuotaLimited()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        (await c.PostAsync("/api/v1/preparation/jobs", null)).EnsureSuccessStatusCode();
        var job = await (await c.PostAsync("/api/v1/preparation/jobs", null)).Read<PrepJobResponse>();
        Assert.Equal(0, job.PrepJobsRemaining);
        var third = await c.PostAsync("/api/v1/preparation/jobs", null);
        Assert.Equal(HttpStatusCode.PaymentRequired, third.StatusCode);
        var r = await c.PostAsJsonAsync("/api/v1/answers/stream", new AnswerStreamRequest(null, job.JobId, new[] { new AnswerMessage("user", "prep") }, 100, "prep"));
        r.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MaintenanceModeBlocksNewSessionsButNotAccount()
    {
        var admin = _f.ClientFor(ApiFactory.AdminId);
        (await admin.PutAsJsonAsync("/api/v1/admin/config/maintenance", true)).EnsureSuccessStatusCode();
        try
        {
            var c = _f.ClientFor(Guid.NewGuid());
            var dev = await c.RegisterDevice();
            var r = await c.StartSession(dev.Id);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
            Assert.Equal("maintenance", (await r.Error()).Code);
            (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();
        }
        finally { (await admin.PutAsJsonAsync("/api/v1/admin/config/maintenance", false)).EnsureSuccessStatusCode(); }
    }
}
