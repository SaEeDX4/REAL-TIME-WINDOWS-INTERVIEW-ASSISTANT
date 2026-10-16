using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewAssistant.Backend.Tests;

/// <summary>
/// Server-authoritative live transcription: the desktop streams through the service's relay, never holds a provider
/// credential, and the relay closes the moment the billable lease ends — whichever way it ends.
/// </summary>
public class RealtimeRelayTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public RealtimeRelayTests(ApiFactory f) => _f = f;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private const string Append = "{\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\"}";

    private async Task<(Guid User, HttpClient C, DeviceDto Device, StartSessionResponse Lease)> StartLease()
    {
        var user = Guid.NewGuid();
        var c = _f.ClientFor(user, "2.1.0");
        var device = await c.RegisterDevice();
        var lease = await (await c.StartSession(device.Id)).Read<StartSessionResponse>();
        return (user, c, device, lease);
    }

    private int Minted() => _f.OpenAi.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.EndsWith("client_secrets"));

    private static async Task AssertClosedForLeaseEnd(WebSocket ws)
    {
        // Upstream traffic may still be in flight; the relay's close must follow within the wait.
        for (int i = 0; i < 20; i++)
        {
            var f = await ws.Receive(Wait);
            Assert.NotNull(f);
            if (f!.Type != WebSocketMessageType.Close) continue;
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, f.CloseStatus);
            Assert.Equal("lease_ended", f.CloseDescription);
            return;
        }
        Assert.Fail("relay did not close");
    }

    [Fact]
    public async Task LeaseCarriesNoProviderSecret_AudioAndTranscriptsFlowThroughTheRelay()
    {
        var before = Minted();
        var (user, c, _, lease) = await StartLease();
        Assert.True(lease.Realtime.Relay);
        Assert.Equal("", lease.Realtime.ClientSecret);
        Assert.Equal($"api/v1/sessions/{lease.SessionId}/realtime", lease.Realtime.WebSocketUrl);
        Assert.Equal(before, Minted());                                   // starting a lease mints nothing
        var renewed = await (await c.PostAsync($"/api/v1/sessions/{lease.SessionId}/realtime-secret", null)).Read<RealtimeCredential>();
        Assert.Equal("", renewed.ClientSecret);                           // neither does the legacy renewal route
        Assert.Equal(before, Minted());

        using var ws = await _f.OpenRelay(user, lease.SessionId);
        Assert.Equal(before + 1, Minted());                               // minted server-side for this connection only
        var upstream = _f.Upstream.Last;
        Assert.StartsWith("ek_test_", upstream.Credential.ClientSecret);

        // Only audio-buffer messages reach the provider: a session.update (model/prompt change) is dropped.
        await ws.SendText("{\"type\":\"session.update\",\"session\":{\"model\":\"expensive\"}}");
        await ws.SendText("not json");
        await ws.SendText(Append);
        var got = await upstream.Socket.Receive(Wait);
        Assert.Equal(Append, got!.Text);

        const string evt = "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"Tell me about yourself.\"}";
        await upstream.Socket.SendText(evt);
        Assert.Equal(evt, (await ws.Receive(Wait))!.Text);

        (await c.PostAsync($"/api/v1/sessions/{lease.SessionId}/stop", null)).EnsureSuccessStatusCode();
        await AssertClosedForLeaseEnd(ws);
    }

    [Fact]
    public async Task OldClientsThatWouldHoldAProviderSecret_MustUpdate()
    {
        var c = _f.ClientFor(Guid.NewGuid(), "2.1.0");
        var device = await c.RegisterDevice();
        var r = await c.PostAsJsonAsync("/api/v1/sessions", new StartSessionRequest(device.Id, null, Guid.NewGuid().ToString("n"), "en"));
        Assert.Equal((HttpStatusCode)426, r.StatusCode);
        Assert.Equal("client_update_required", (await r.Error()).Code);
    }

    [Fact]
    public async Task RelayIsRefused_ForAnotherUsersLease_AnEndedLease_AndPlainHttp()
    {
        var (user, c, _, lease) = await StartLease();
        var before = Minted();
        await Assert.ThrowsAnyAsync<Exception>(() => _f.OpenRelay(Guid.NewGuid(), lease.SessionId));   // someone else's lease: 404
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/sessions/{lease.SessionId}/realtime")).StatusCode);
        (await c.PostAsync($"/api/v1/sessions/{lease.SessionId}/stop", null)).EnsureSuccessStatusCode();
        await Assert.ThrowsAnyAsync<Exception>(() => _f.OpenRelay(user, lease.SessionId));            // ended lease: 409
        Assert.Equal(before, Minted());                                                               // nothing minted for refused connections
    }

    [Fact]
    public async Task RelayClosesAtTheHeartbeatTimeout_InsideTheWindowTheSweeperBills()
    {
        var (user, _, _, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        // The client stops heartbeating (modified client, crash, network loss) but keeps the socket open.
        _f.Time.Advance(TimeSpan.FromSeconds(90 - SessionService.RelayCheckMarginSeconds));
        await AssertClosedForLeaseEnd(ws);
        // The sweeper later bills the full timeout: the relay never ran outside billed time.
        _f.Time.Advance(TimeSpan.FromSeconds(SessionService.RelayCheckMarginSeconds + 1));
        using (var scope = _f.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SessionService>().ExpireStaleAsync(default);
        using (var scope = _f.Services.CreateScope())
        {
            var s = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking().FirstAsync(x => x.Id == lease.SessionId);
            Assert.Equal("expired", s.Status);
            Assert.Equal(90, s.MeteredSeconds);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => _f.OpenRelay(user, lease.SessionId));
    }

    [Fact]
    public async Task RelayClosesWhenTheAllowanceRunsOut_BetweenHeartbeats()
    {
        var (user, c, _, lease) = await StartLease();          // trial: 30 minutes
        for (int i = 1; i <= 29; i++)
        {
            _f.Time.Advance(TimeSpan.FromSeconds(60));
            var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{lease.SessionId}/heartbeat", new HeartbeatRequest(i))).Read<HeartbeatResponse>();
            Assert.True(hb.Active);
        }
        using var ws = await _f.OpenRelay(user, lease.SessionId);   // 60 s left
        _f.Time.Advance(TimeSpan.FromSeconds(61));                   // no heartbeat yet: the relay must not run past the allowance
        await AssertClosedForLeaseEnd(ws);
        var last = await (await c.PostAsJsonAsync($"/api/v1/sessions/{lease.SessionId}/heartbeat", new HeartbeatRequest(30))).Read<HeartbeatResponse>();
        Assert.False(last.Active);
        Assert.Equal("allowance_exhausted", last.EndReason);
        Assert.Equal(30 * 60, (await (await c.GetAsync("/api/v1/me")).Read<AccountDto>()).Usage.LiveSecondsUsed);
    }

    [Fact]
    public async Task RevokingTheDevice_ClosesItsRelayImmediately()
    {
        var (user, c, device, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        (await c.DeleteAsync($"/api/v1/devices/{device.Id}")).EnsureSuccessStatusCode();
        await AssertClosedForLeaseEnd(ws);
    }

    [Fact]
    public async Task OneRelayPerLease_AReconnectReplacesThePreviousConnection()
    {
        var (user, _, _, lease) = await StartLease();
        using var first = await _f.OpenRelay(user, lease.SessionId);
        using var second = await _f.OpenRelay(user, lease.SessionId);
        var f = await first.Receive(Wait);
        Assert.Equal(WebSocketMessageType.Close, f!.Type);
        await second.SendText(Append);
        Assert.Equal(Append, (await _f.Upstream.Last.Socket.Receive(Wait))!.Text);
    }

    [Fact]
    public async Task UpstreamUnavailable_IsAReadableErrorAndNoSocketIsAccepted()
    {
        var (user, _, _, lease) = await StartLease();
        _f.Upstream.Fail = true;
        try { await Assert.ThrowsAnyAsync<Exception>(() => _f.OpenRelay(user, lease.SessionId)); }
        finally { _f.Upstream.Fail = false; }
    }

    private async Task<int> LiveSecondsUsed(Guid user)
    {
        using var scope = _f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Usage.Where(u => u.UserId == user && u.Kind == UsageService.LiveSeconds).SumAsync(u => (int?)u.Quantity) ?? 0;
    }

    [Fact]
    public async Task RevokingYourOwnDevice_EndsTheLeaseBilledLikeAStop()
    {
        // A modified client that never heartbeats and then removes its own device must still pay for the time used.
        var (user, c, device, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        _f.Time.Advance(TimeSpan.FromSeconds(60));
        (await c.DeleteAsync($"/api/v1/devices/{device.Id}")).EnsureSuccessStatusCode();
        await AssertClosedForLeaseEnd(ws);
        Assert.Equal(60, await LiveSecondsUsed(user));
    }

    [Fact]
    public async Task AdminSuspension_EndsTheLeaseBilledLikeAStop()
    {
        var (user, _, _, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        _f.Time.Advance(TimeSpan.FromSeconds(45));
        var admin = _f.ClientFor(ApiFactory.AdminId);
        (await admin.PostAsync($"/api/v1/admin/users/{user}/disable", null)).EnsureSuccessStatusCode();
        await AssertClosedForLeaseEnd(ws);
        Assert.Equal(45, await LiveSecondsUsed(user));
    }

    [Fact]
    public async Task ANewerRelayConnectionOnAnotherReplica_StopsTheOlderOne()
    {
        var (user, _, _, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        // Another replica accepted a newer connection for the same lease: it is recorded on the lease row.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var s = await db.Sessions.FirstAsync(x => x.Id == lease.SessionId);
            s.RelayConnectionId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        await AssertClosedForLeaseEnd(ws);
    }

    [Fact]
    public async Task OnlyARebuiltAudioMessageReachesTheProvider_NeverTheClientsBytes()
    {
        var (user, _, _, lease) = await StartLease();
        using var ws = await _f.OpenRelay(user, lease.SessionId);
        var upstream = _f.Upstream.Last;
        await ws.SendText("{\"type\":\"session.update\",\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\"}");   // duplicate key
        await ws.SendText(Append + " {\"type\":\"session.update\"}");                                                    // trailing content
        await ws.SendText("{\"type\":\"input_audio_buffer.append\",\"audio\":\"not base64!\"}");
        await ws.SendText("{\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\",\"session\":{\"model\":\"x\"}}");  // extra field
        var got = await upstream.Socket.Receive(Wait);
        Assert.Equal(Append, got!.Text);   // the first three were dropped; the fourth arrives rebuilt without "session"
    }

    [Fact]
    public void OnlyAudioBufferMessagesAreForwarded()
    {
        static bool Ok(string s) => RealtimeRelay.IsAllowedClientMessage(System.Text.Encoding.UTF8.GetBytes(s));
        Assert.True(Ok(Append));
        Assert.True(Ok("{\"type\":\"input_audio_buffer.commit\"}"));
        Assert.False(Ok("{\"type\":\"session.update\"}"));
        Assert.False(Ok("{\"type\":\"transcription_session.update\"}"));
        Assert.False(Ok("{\"type\":\"response.create\"}"));
        Assert.False(Ok("[]"));
        Assert.False(Ok("{\"type\":1}"));
        Assert.False(Ok("{"));
        Assert.False(Ok("{\"type\":\"session.update\",\"type\":\"input_audio_buffer.commit\"}"));
        Assert.False(Ok("{\"type\":\"input_audio_buffer.commit\"} {}"));
        Assert.False(Ok("{\"type\":\"input_audio_buffer.append\"}"));
    }
}
