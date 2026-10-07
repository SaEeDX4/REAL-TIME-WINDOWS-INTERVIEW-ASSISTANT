using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Backend.Tests;

public class AccountDeviceAdminTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AccountDeviceAdminTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task DeviceLimitReinstallAndRevocation()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        var a = await c.RegisterDevice("installation-AAAAAAAAAAAA");
        var again = await c.RegisterDevice("installation-AAAAAAAAAAAA");   // reinstall/upgrade: same device
        Assert.Equal(a.Id, again.Id);
        var b = await c.PostAsJsonAsync("/api/v1/devices", new RegisterDeviceRequest("installation-BBBBBBBBBBBB", "Laptop", "windows-x64", "2.0.0"));
        Assert.Equal(HttpStatusCode.Forbidden, b.StatusCode);
        Assert.Equal("device_limit", (await b.Error()).Code);
        var s = await (await c.StartSession(a.Id)).Read<StartSessionResponse>();
        (await c.DeleteAsync($"/api/v1/devices/{a.Id}")).EnsureSuccessStatusCode();
        var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(1))).Read<HeartbeatResponse>();
        Assert.False(hb.Active);                                            // revocation ends leases
        Assert.Equal(HttpStatusCode.Forbidden, (await c.StartSession(a.Id)).StatusCode);
        (await c.PostAsJsonAsync("/api/v1/devices", new RegisterDeviceRequest("installation-BBBBBBBBBBBB", "Laptop", "windows-x64", "2.0.0"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ProfileTargetReportCrudCascadeAndVersioning()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        Guid p = Guid.NewGuid(), t = Guid.NewGuid(), r = Guid.NewGuid();
        var created = await (await c.PutAsJsonAsync($"/api/v1/profiles/{p}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Jane" }), null))).Read<SyncDocument>();
        await (await c.PutAsJsonAsync($"/api/v1/targets/{t}", new UpsertDocumentRequest(p, Helpers.Doc(new { jobTitle = "PM", company = "Contoso" }), null))).Read<SyncDocument>();
        var report = await (await c.PutAsJsonAsync($"/api/v1/reports/{r}", new UpsertDocumentRequest(t, Helpers.Doc(new { questions = 3 }), null))).Read<SyncDocument>();
        Assert.Single(await (await c.GetAsync($"/api/v1/targets?parentId={p}")).Read<List<SyncDocument>>());
        var stale = await c.PutAsJsonAsync($"/api/v1/profiles/{p}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "x" }), created.Version + 5));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        (await c.PutAsJsonAsync($"/api/v1/profiles/{p}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Jane D." }), created.Version))).EnsureSuccessStatusCode();
        (await c.DeleteAsync($"/api/v1/profiles/{p}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/targets/{t}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/reports/{r}")).StatusCode);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task ReportRetentionFollowsPlan()
    {
        var id = Guid.NewGuid();
        var c = _f.ClientFor(id);
        Guid p = Guid.NewGuid(), t = Guid.NewGuid(), r = Guid.NewGuid();
        await c.PutAsJsonAsync($"/api/v1/profiles/{p}", new UpsertDocumentRequest(null, Helpers.Doc(new { }), null));
        await c.PutAsJsonAsync($"/api/v1/targets/{t}", new UpsertDocumentRequest(p, Helpers.Doc(new { }), null));
        (await c.PutAsJsonAsync($"/api/v1/reports/{r}", new UpsertDocumentRequest(t, Helpers.Doc(new { }), null))).EnsureSuccessStatusCode();
        Assert.Single(await (await c.GetAsync("/api/v1/reports")).Read<List<SyncDocument>>());
        _f.Time.Advance(TimeSpan.FromDays(15)); // trial retention = 14 days
        c = _f.ClientFor(id);
        Assert.Empty(await (await c.GetAsync("/api/v1/reports")).Read<List<SyncDocument>>());
        using (var scope = _f.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<InterviewAssistant.Backend.Services.DocumentService>().PurgeExpiredReportsAsync(default) >= 1);
    }

    [Fact]
    public async Task ExportContainsDataAndDeleteRemovesIt()
    {
        var id = Guid.NewGuid();
        var c = _f.ClientFor(id);
        await c.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Export Me" }), null));
        await c.RegisterDevice();
        var export = await (await c.GetAsync("/api/v1/me/export")).Content.ReadAsStringAsync();
        Assert.Contains("Export Me", export);
        Assert.Contains("interview-assistant-export/v1", export);
        (await c.DeleteAsync("/api/v1/me")).EnsureSuccessStatusCode();
        var after = await c.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("account_deleted", (await after.Error()).Code);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewAssistant.Backend.Data.AppDbContext>();
        Assert.False(db.Documents.Any(d => d.UserId == id));
        Assert.False(db.Devices.Any(d => d.UserId == id));
    }

    [Fact]
    public async Task AdminIsRestrictedAuditedAndNeverSeesProfileContent()
    {
        var user = Guid.NewGuid();
        var uc = _f.ClientFor(user);
        await uc.PutAsJsonAsync($"/api/v1/profiles/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { resume = "SECRET-CV-TEXT" }), null));
        Assert.Equal(HttpStatusCode.Forbidden, (await uc.GetAsync("/api/v1/admin/users")).StatusCode);
        var admin = _f.ClientFor(ApiFactory.AdminId);
        var detail = await (await admin.GetAsync($"/api/v1/admin/users/{user}")).Content.ReadAsStringAsync();
        Assert.Contains("documentCounts", detail);
        Assert.DoesNotContain("SECRET-CV-TEXT", detail);
        (await admin.PostAsync($"/api/v1/admin/users/{user}/disable", null)).EnsureSuccessStatusCode();
        Assert.Equal("account_disabled", (await (await uc.GetAsync("/api/v1/me")).Error()).Code);
        (await admin.PostAsync($"/api/v1/admin/users/{user}/enable", null)).EnsureSuccessStatusCode();
        (await uc.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();
        using var scope = _f.Services.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<InterviewAssistant.Backend.Data.AppDbContext>().Audit.Count(a => a.ActorUserId == ApiFactory.AdminId) >= 3);
    }

    [Fact]
    public async Task RemoteConfigKillSwitchSwitchesToFallbackModel()
    {
        var admin = _f.ClientFor(ApiFactory.AdminId);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/config/openai_api_key", "x")).StatusCode); // secrets never via config
        (await admin.PutAsJsonAsync("/api/v1/admin/config/disabled_models", new[] { "gpt-5.4-mini", "gpt-live-transcribe" })).EnsureSuccessStatusCode();
        try
        {
            var cfg = await (await _f.CreateClient().GetAsync("/api/v1/config")).Read<RuntimeConfig>();
            Assert.Equal("gpt-4.1-mini", cfg.AnswerModel);
            Assert.Equal("gpt-4o-transcribe", cfg.TranscriptionModel);
        }
        finally { (await admin.PutAsJsonAsync("/api/v1/admin/config/disabled_models", Array.Empty<string>())).EnsureSuccessStatusCode(); }
    }

    [Fact]
    public async Task PlanCatalogIsDataDrivenAndPublic()
    {
        var plans = await (await _f.CreateClient().GetAsync("/api/v1/billing/plans")).Read<List<PlanDto>>();
        Assert.Contains(plans, p => p.Code == "trial" && !p.Purchasable);
        Assert.Contains(plans, p => p.Code == "pro-dev" && p.Purchasable && p.Entitlements.CoachMode);
    }
}
