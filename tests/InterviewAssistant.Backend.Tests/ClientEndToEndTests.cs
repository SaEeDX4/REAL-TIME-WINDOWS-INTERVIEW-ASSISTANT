using InterviewAssistant.Client;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.Backend.Tests;

/// <summary>The real desktop cloud client against the real API + PostgreSQL (OpenAI/Paddle stubbed at the HTTP boundary).</summary>
public class ClientEndToEndTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ClientEndToEndTests(ApiFactory f) => _f = f;

    private (BackendClient Api, List<bool> Refreshes) Client(Guid user, string version = "2.0.0")
    {
        var refreshes = new List<bool>();
        var api = new BackendClient(_f.CreateClient(), (force, _) => { refreshes.Add(force); return Task.FromResult<string?>(_f.Token(user)); }, version);
        return (api, refreshes);
    }

    [Fact]
    public async Task FullLiveSessionThroughDesktopClient()
    {
        var (api, _) = Client(Guid.NewGuid());
        var cfg = await api.ConfigAsync(default);
        Assert.Equal("gpt-5.4-mini", cfg.AnswerModel);
        var me = await api.MeAsync(default);
        Assert.Equal("trial", me.Entitlements.PlanCode);

        var dir = Path.Combine(Path.GetTempPath(), "ia-inst-" + Guid.NewGuid().ToString("n"));
        var inst = InstallationIdentity.GetOrCreate(Path.Combine(dir, "installation.id"));
        Assert.Equal(inst, InstallationIdentity.GetOrCreate(Path.Combine(dir, "installation.id")));   // stable across restarts
        var device = await api.RegisterDeviceAsync(new RegisterDeviceRequest(inst, "Test PC", "windows-x64", "2.0.0"), default);

        await using var session = await CloudSession.StartAsync(api, device.Id, null, "en", default, _f.Time, runLoop: false);
        Assert.StartsWith("ek_test_", session.CurrentSecret);
        string? ended = null;
        session.Ended += r => ended = r;

        _f.Time.Advance(TimeSpan.FromSeconds(30));
        await session.HeartbeatOnceAsync(default);
        Assert.Equal(30 * 60 - 30, session.RemainingSeconds);

        // Credential renewal happens automatically when the ephemeral secret is about to expire.
        var before = session.CurrentSecret;
        _f.Time.Advance(TimeSpan.FromMinutes(9));
        await session.HeartbeatOnceAsync(default);
        Assert.NotEqual(before, session.CurrentSecret);

        var provider = new CloudAnswerProvider(api, () => session.Id);
        var text = string.Concat(await provider.StreamAsync(new[] { new ChatMessage("system", "s"), new ChatMessage("user", "q") }, 200, default).ToListAsync());
        Assert.Contains("start with the goal", text);

        await session.StopAsync();
        Assert.Equal("stopped", ended);
        Assert.False(session.Active);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ServerEndingTheLeaseIsSurfacedToTheClient()
    {
        var (api, _) = Client(Guid.NewGuid());
        var device = await api.RegisterDeviceAsync(new RegisterDeviceRequest("inst-" + Guid.NewGuid().ToString("n"), "PC", "windows-x64", "2.0.0"), default);
        await using var session = await CloudSession.StartAsync(api, device.Id, null, null, default, _f.Time, runLoop: false);
        string? ended = null;
        session.Ended += r => ended = r;
        await api.RevokeDeviceAsync(device.Id, default);
        await session.HeartbeatOnceAsync(default);
        Assert.False(session.Active);
        Assert.NotNull(ended);
    }

    [Fact]
    public async Task ErrorsAreTypedWithCodesAndCorrelationIds()
    {
        var (api, _) = Client(Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<BackendException>(() => api.StartSessionAsync(new StartSessionRequest(Guid.NewGuid(), null, "k", null), default));
        Assert.Equal(403, ex.Status);
        Assert.False(string.IsNullOrEmpty(ex.CorrelationId));

        var (old, _) = Client(Guid.NewGuid(), version: "1.0.0");
        var upd = await Assert.ThrowsAsync<BackendException>(() => old.MeAsync(default));
        Assert.True(upd.IsUpdateRequired);

        var provider = new CloudAnswerProvider(api, () => Guid.NewGuid());
        var pe = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in provider.StreamAsync(new[] { new ChatMessage("user", "q") }, 50, default)) { } });
        Assert.Equal(ProviderErrorKind.Unknown, pe.Kind);   // 403 no_active_session
    }

    [Fact]
    public async Task ExpiredTokenTriggersOneRefreshAndRetry()
    {
        var user = Guid.NewGuid();
        var calls = new List<bool>();
        var api = new BackendClient(_f.CreateClient(), (force, _) =>
        {
            calls.Add(force);
            return Task.FromResult<string?>(force ? _f.Token(user) : _f.Token(user, expires: _f.Time.GetUtcNow().UtcDateTime.AddMinutes(-5)));
        }, "2.0.0");
        var me = await api.MeAsync(default);
        Assert.Equal("trial", me.Entitlements.PlanCode);
        Assert.Equal(new[] { false, true }, calls);
    }

    [Fact]
    public async Task SyncedDocumentsRoundTrip()
    {
        var (api, _) = Client(Guid.NewGuid());
        var id = Guid.NewGuid();
        var doc = await api.PutAsync("profiles", id, new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Jane" }), null), default);
        Assert.Equal(1, doc.Version);
        var list = await api.ListAsync("profiles", null, default);
        Assert.Equal("Jane", Assert.Single(list).Data.GetProperty("name").GetString());
        var conflict = await Assert.ThrowsAsync<BackendException>(() => api.PutAsync("profiles", id, new UpsertDocumentRequest(null, Helpers.Doc(new { }), 99), default));
        Assert.Equal(409, conflict.Status);
        await api.DeleteDocumentAsync("profiles", id, default);
        Assert.Empty(await api.ListAsync("profiles", null, default));
    }
}
