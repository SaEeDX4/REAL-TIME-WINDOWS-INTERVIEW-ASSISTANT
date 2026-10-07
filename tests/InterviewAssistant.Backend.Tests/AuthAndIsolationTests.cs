using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.Backend.Tests;

public class AuthAndIsolationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuthAndIsolationTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task UnauthenticatedRequestsAreRejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("wrong-secret")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    [InlineData("alg-none")]
    public async Task InvalidTokensAreRejected(string kind)
    {
        var id = Guid.NewGuid();
        var token = kind switch
        {
            "tampered" => Tamper(_f.Token(id)),
            "wrong-secret" => _f.Token(id, secret: "another-secret-that-is-long-enough-for-hs256-xyz"), // fake-credential: test fixture
            "wrong-issuer" => _f.Token(id, issuer: "https://evil.example/auth/v1"),
            "expired" => _f.Token(id, expires: _f.Time.GetUtcNow().UtcDateTime.AddMinutes(-10)),
            "wrong-audience" => _f.Token(id, audience: "anon"),
            _ => UnsignedToken(id),
        };
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/me")).StatusCode);
    }

    private static string Tamper(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1])).Replace("authenticated\"", "service_role\"");
        parts[1] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return string.Join('.', parts);
    }
    private static byte[] Base64UrlDecode(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
    private static string UnsignedToken(Guid id)
    {
        string B(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B($"{{\"sub\":\"{id}\",\"iss\":\"{ApiFactory.Issuer}\",\"aud\":\"authenticated\",\"exp\":4102444800}}") + ".";
    }

    [Fact]
    public async Task FirstValidRequestProvisionsAccountWithTrialEntitlements()
    {
        var me = await (await _f.ClientFor(Guid.NewGuid()).GetAsync("/api/v1/me")).Read<AccountDto>();
        Assert.Equal("trial", me.Entitlements.PlanCode);
        Assert.Equal(30 * 60, me.Usage.LiveSecondsRemaining);
        Assert.Equal(1, me.Entitlements.MaxProfiles);
    }

    [Fact]
    public async Task UsersCannotSeeOrModifyEachOthersDocuments()
    {
        var alice = _f.ClientFor(Guid.NewGuid());
        var bob = _f.ClientFor(Guid.NewGuid());
        var pid = Guid.NewGuid();
        (await alice.PutAsJsonAsync($"/api/v1/profiles/{pid}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Alice" }), null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/profiles/{pid}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/profiles/{pid}", new UpsertDocumentRequest(null, Helpers.Doc(new { name = "Hijack" }), null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/profiles/{pid}")).StatusCode);
        Assert.Empty(await (await bob.GetAsync("/api/v1/profiles")).Read<List<SyncDocument>>());
        // Bob cannot attach a target to Alice's profile either.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/targets/{Guid.NewGuid()}", new UpsertDocumentRequest(pid, Helpers.Doc(new { jobTitle = "x" }), null))).StatusCode);
        var still = await (await alice.GetAsync($"/api/v1/profiles/{pid}")).Read<SyncDocument>();
        Assert.Equal("Alice", still.Data.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SecurityHeadersAndCorrelationIdArePresent()
    {
        var r = await _f.ClientFor(Guid.NewGuid()).GetAsync("/api/v1/me");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(r.Headers.Contains("X-Correlation-Id"));
        var err = await _f.CreateClient().GetAsync("/api/v1/profiles/not-a-guid");
        Assert.True(err.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReadinessReportsMigratedDatabase()
    {
        var r = await _f.CreateClient().GetStringAsync("/health/ready");
        Assert.Contains("ready", r);
    }

    [Fact]
    public async Task OutdatedClientsMustUpdate()
    {
        var r = await _f.ClientFor(Guid.NewGuid(), version: "1.0.0").GetAsync("/api/v1/me");
        Assert.Equal((HttpStatusCode)426, r.StatusCode);
        Assert.Equal("client_update_required", (await r.Error()).Code);
    }
}
