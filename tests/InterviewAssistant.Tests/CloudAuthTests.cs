using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InterviewAssistant.Client;
using InterviewAssistant.Core.Persistence;

namespace InterviewAssistant.Tests;

public class CloudAuthTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r, body));
            return respond(r, body);
        }
    }

    private sealed class MemoryStore : ITokenStore
    {
        public AuthSession? Saved;
        public AuthSession? Load() => Saved;
        public void Save(AuthSession s) => Saved = s;
        public void Clear() => Saved = null;
    }

    private static readonly CloudOptions Opt = new() { ApiBaseUrl = "https://api.example.test", SupabaseUrl = "https://proj.supabase.test", SupabaseAnonKey = "anon-public", OAuthProviders = new[] { "google", "azure" } };

    private static HttpResponseMessage Tokens(string access, string refresh, int expiresIn = 3600) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = access, refresh_token = refresh, expires_in = expiresIn, user = new { id = "u1", email = "a@b.test" } }) };

    [Fact]
    public void PkceMatchesRfc7636()
    {
        // RFC 7636 Appendix B test vector.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var v = Pkce.CreateVerifier();
        Assert.True(Pkce.IsValidVerifier(v));
        Assert.NotEqual(v, Pkce.CreateVerifier());
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }

    [Fact]
    public void AuthorizeUrlUsesS256AndOnlyEnabledProviders()
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => new(HttpStatusCode.OK))), Opt, new MemoryStore());
        var url = auth.BuildAuthorizeUrl("google", "CHAL", "http://127.0.0.1:5555/callback?s=x");
        Assert.StartsWith("https://proj.supabase.test/auth/v1/authorize?provider=google", url);
        Assert.Contains("code_challenge=CHAL", url);
        Assert.Contains("code_challenge_method=s256", url);
        Assert.Contains("redirect_to=http%3A%2F%2F127.0.0.1%3A5555%2Fcallback%3Fs%3Dx", url);
        Assert.Throws<AuthException>(() => auth.BuildAuthorizeUrl("evil", "c", "r"));
    }

    [Fact]
    public async Task CodeExchangeStoresTokensAndRefreshRotatesThem()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-07T09:00:00Z"));
        var stub = new Stub((r, body) => r.RequestUri!.Query.Contains("grant_type=pkce") ? Tokens("at1", "rt1") : Tokens("at2", "rt2"));
        var store = new MemoryStore();
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, store, time);
        await auth.ExchangeCodeAsync("CODE", "VERIFIER", default);
        var (req, body) = stub.Calls[0];
        Assert.Equal("anon-public", req.Headers.GetValues("apikey").Single());
        Assert.Contains("\"auth_code\":\"CODE\"", body);
        Assert.Contains("\"code_verifier\":\"VERIFIER\"", body);
        Assert.Equal("rt1", store.Saved!.RefreshToken);
        Assert.Equal("at1", await auth.GetAccessTokenAsync(default));          // still fresh → no network
        Assert.Single(stub.Calls);
        time.Now = time.Now.AddMinutes(59.5);                                   // within 60 s of expiry → refresh
        Assert.Equal("at2", await auth.GetAccessTokenAsync(default));
        Assert.Contains("\"refresh_token\":\"rt1\"", stub.Calls[1].Body);
        Assert.Equal("rt2", store.Saved!.RefreshToken);
    }

    [Fact]
    public async Task RevokedRefreshTokenSignsOutLocally()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-07T09:00:00Z"));
        var store = new MemoryStore { Saved = new AuthSession("old", "revoked", time.Now.AddSeconds(-1), "u", "e") };
        var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error_code = "refresh_token_not_found", msg = "Invalid Refresh Token" }) });
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, store, time);
        AuthSession? changed = new("x", "x", default, "", "");
        auth.SessionChanged += s => changed = s;
        Assert.Null(await auth.GetAccessTokenAsync(default));
        Assert.False(auth.IsSignedIn);
        Assert.Null(store.Saved);
        Assert.Null(changed);
    }

    [Fact]
    public async Task LoopbackAcceptsOnlyMatchingStateOnCallbackPath()
    {
        using var loop = new LoopbackRedirect(Pkce.CreateState());
        Assert.StartsWith("http://127.0.0.1:", loop.RedirectUri);
        var wait = loop.WaitForCodeAsync(TimeSpan.FromSeconds(20), default);
        using var http = new HttpClient();
        var wrong = await http.GetAsync($"http://127.0.0.1:{loop.Port}/callback?s=forged&code=EVIL");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.False(wait.IsCompleted);
        var ok = await http.GetAsync(loop.RedirectUri + "&code=GOOD");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("no-store", ok.Headers.CacheControl!.ToString());
        Assert.Equal("GOOD", await wait);
    }

    [Fact]
    public async Task LoopbackRejectsMalformedAndOversizedRequestsAndKeepsWaiting()
    {
        using var loop = new LoopbackRedirect(Pkce.CreateState());
        var wait = loop.WaitForCodeAsync(TimeSpan.FromSeconds(20), default);
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, loop.Port);
            var s = tcp.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("POST /callback HTTP/1.1\r\nHost: x\r\n\r\n"));
            var resp = await new StreamReader(s).ReadToEndAsync();
            Assert.StartsWith("HTTP/1.1 400", resp);
        }
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, loop.Port);
            await tcp.GetStream().WriteAsync(new byte[20_000]);   // junk, no request line
        }
        Assert.False(wait.IsCompleted);
        using var http = new HttpClient();
        var err = await http.GetAsync(loop.RedirectUri + "&error=access_denied&error_description=User%20cancelled");
        Assert.Equal(HttpStatusCode.BadRequest, err.StatusCode);
        var ex = await Assert.ThrowsAsync<AuthException>(() => wait);
        Assert.Equal("User cancelled", ex.Message);
    }

    [Fact]
    public async Task LoopbackTimesOutCleanly()
    {
        using var loop = new LoopbackRedirect(Pkce.CreateState());
        await Assert.ThrowsAsync<TimeoutException>(() => loop.WaitForCodeAsync(TimeSpan.FromMilliseconds(200), default));
    }

    [Fact]
    public async Task InteractiveMagicLinkFlowEndToEnd()
    {
        string? redirect = null;
        var stub = new Stub((r, body) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/otp"))
            {
                redirect = Uri.UnescapeDataString(r.RequestUri.Query.Split("redirect_to=")[1]);
                Assert.Contains("\"code_challenge_method\":\"s256\"", body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return Tokens("at", "rt");
        });
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, new MemoryStore());
        var signIn = auth.SignInInteractiveAsync(_ => Task.CompletedTask, null, "a@b.test", TimeSpan.FromSeconds(20), default);
        for (int i = 0; i < 100 && redirect == null; i++) await Task.Delay(20);
        using (var http = new HttpClient()) (await http.GetAsync(redirect + "&code=MAGIC")).EnsureSuccessStatusCode();   // the user clicking the e-mail link
        var session = await signIn;
        Assert.Equal("at", session.AccessToken);
        Assert.Contains("\"auth_code\":\"MAGIC\"", stub.Calls.Last().Body);
    }

    [Fact]
    public void TokenStoreEncryptsAndSurvivesCorruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ia-tok-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, "session.bin");
        var store = new ProtectedFileTokenStore(path, new XorProtector());
        store.Save(new AuthSession("ACCESS-SECRET", "REFRESH-SECRET", DateTimeOffset.UtcNow, "u", "e"));
        Assert.DoesNotContain("REFRESH-SECRET", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("REFRESH-SECRET", store.Load()!.RefreshToken);
        File.WriteAllText(path, "garbage");
        Assert.Null(store.Load());
        store.Clear();
        Assert.False(File.Exists(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CloudOptionsRequireHttpsAndAnonKey()
    {
        Assert.True(Opt.IsConfigured);
        Assert.False(new CloudOptions { ApiBaseUrl = "http://api.example.test", SupabaseUrl = Opt.SupabaseUrl, SupabaseAnonKey = "k" }.IsConfigured);
        Assert.True(new CloudOptions { ApiBaseUrl = "http://localhost:5149", SupabaseUrl = "http://127.0.0.1:54321", SupabaseAnonKey = "k" }.IsConfigured);
        Assert.False(new CloudOptions().IsConfigured);
    }

    [Fact]
    public void InstallationIdIsRandomAndNotHardwareDerived()
    {
        var a = Path.Combine(Path.GetTempPath(), "ia-i-" + Guid.NewGuid().ToString("n"), "id");
        var b = Path.Combine(Path.GetTempPath(), "ia-i-" + Guid.NewGuid().ToString("n"), "id");
        Assert.NotEqual(InstallationIdentity.GetOrCreate(a), InstallationIdentity.GetOrCreate(b));
        Assert.DoesNotContain(Environment.MachineName.ToLowerInvariant(), InstallationIdentity.GetOrCreate(a));
    }

    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] c) { var r = Protect(c); JsonDocument.Parse(r).Dispose(); return r; }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
