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

    private static HttpResponseMessage Err(HttpStatusCode code, string errorCode, string msg) =>
        new(code) { Content = JsonContent.Create(new { code = (int)code, error_code = errorCode, msg }) };

    [Fact]
    public async Task SignUpReportsConfirmationRequiredWithoutStoringTokens()
    {
        var o = new CloudOptions { ApiBaseUrl = Opt.ApiBaseUrl, SupabaseUrl = Opt.SupabaseUrl, SupabaseAnonKey = "anon", EmailConfirmedRedirectUrl = "https://api.example.test/auth/confirmed" };
        var stub = new Stub((r, b) => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "u1", email = "a@b.test", confirmation_sent_at = "2026-10-13T00:00:00Z" }) });
        var store = new MemoryStore();
        var auth = new SupabaseAuth(new HttpClient(stub), o, store);
        var r = await auth.SignUpAsync(" a@b.test ", "Secret123!", default);
        Assert.True(r.ConfirmationRequired);
        Assert.False(r.SignedIn);
        Assert.Null(store.Saved);
        var (req, body) = stub.Calls.Single();
        Assert.Equal("/auth/v1/signup", req.RequestUri!.AbsolutePath);
        Assert.Contains("redirect_to=https%3A%2F%2Fapi.example.test%2Fauth%2Fconfirmed", req.RequestUri.Query);
        Assert.Contains("\"email\":\"a@b.test\"", body);                       // trimmed
    }

    [Fact]
    public async Task SignUpWithoutConfirmationSignsInImmediately()
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => Tokens("at", "rt"))), Opt, new MemoryStore());
        var r = await auth.SignUpAsync("a@b.test", "Secret123!", default);
        Assert.True(r.SignedIn);
        Assert.True(auth.IsSignedIn);
    }

    [Theory]
    [InlineData("invalid_credentials", "not correct")]
    [InlineData("email_not_confirmed", "confirm your e-mail")]
    [InlineData("user_already_exists", "already exists")]
    [InlineData("weak_password", "stronger password")]
    [InlineData("over_request_rate_limit", "wait a minute")]
    public async Task AuthErrorsBecomePlainLanguage(string code, string expected)
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => Err(HttpStatusCode.BadRequest, code, "raw provider text"))), Opt, new MemoryStore());
        var ex = await Assert.ThrowsAsync<AuthException>(() => auth.SignInWithPasswordAsync("a@b.test", "x", default));
        Assert.Equal(code, ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInWithoutServiceConfigIsAReadableError()
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => Tokens("a", "b"))), new CloudOptions { ApiBaseUrl = Opt.ApiBaseUrl }, new MemoryStore());
        var ex = await Assert.ThrowsAsync<AuthException>(() => auth.SignInWithPasswordAsync("a@b.test", "x", default));
        Assert.Equal("auth_unavailable", ex.Code);
    }

    [Fact]
    public async Task NetworkFailureIsAReadableError()
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => throw new HttpRequestException("dns"))), Opt, new MemoryStore());
        var ex = await Assert.ThrowsAsync<AuthException>(() => auth.SignInWithPasswordAsync("a@b.test", "x", default));
        Assert.Equal("network", ex.Code);
        Assert.Contains("internet", ex.Message);
    }

    [Fact]
    public async Task PasswordResetRunsThroughLoopbackAndUpdatesPassword()
    {
        string? redirect = null;
        var stub = new Stub((r, b) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/recover"))
            {
                redirect = Uri.UnescapeDataString(r.RequestUri.Query.Split("redirect_to=")[1]);
                Assert.Contains("code_challenge", b);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            if (r.Method == HttpMethod.Put && r.RequestUri.AbsolutePath.EndsWith("/user"))
            {
                Assert.Equal("Bearer at", r.Headers.Authorization!.ToString());
                Assert.Contains("NewSecret123", b);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return Tokens("at", "rt");
        });
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, new MemoryStore());
        var recover = auth.RecoverPasswordAsync("a@b.test", TimeSpan.FromSeconds(20), default);
        for (int i = 0; i < 100 && redirect == null; i++) await Task.Delay(20);
        using (var http = new HttpClient()) (await http.GetAsync(redirect + "&code=RESET")).EnsureSuccessStatusCode();
        await recover;
        await auth.UpdatePasswordAsync("NewSecret123", default);
        Assert.Contains(stub.Calls, c => c.Body.Contains("\"auth_code\":\"RESET\""));
    }

    /// <summary>
    /// Enumeration safety (rc.3 bug: an unknown address showed "We sent a reset link to …"): an existing and an unknown
    /// address produce the same displayed notice and the same outcome; the notice never names the address or claims
    /// delivery. Rate limits are still reported.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "user_not_found")]
    [InlineData(HttpStatusCode.BadRequest, "email_not_found")]
    [InlineData(HttpStatusCode.NotFound, null)]
    public async Task PasswordReset_ShowsTheSameNeutralNotice_ForKnownAndUnknownAddresses(HttpStatusCode unknownStatus, string? unknownCode)
    {
        var before = Core.Languages.Loc.Language;
        Core.Languages.Loc.Language = "en";
        try
        {
            async Task<(string? Shown, string Outcome)> Reset(string email)
            {
                var stub = new Stub((r, b) => b.Contains("known@example.test") && !b.Contains("unknown@")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
                    : unknownCode != null ? Err(unknownStatus, unknownCode, "User not found") : new HttpResponseMessage(unknownStatus) { Content = new StringContent("{}") });
                var auth = new SupabaseAuth(new HttpClient(stub), Opt, new MemoryStore());
                string? shown = null;
                // What the Account page shows when the request is accepted (HomeWindow.Forgot_Click).
                try { await auth.RecoverPasswordAsync(email, TimeSpan.FromMilliseconds(300), default, () => shown = App.UiState.PasswordResetCopy.Requested); }
                catch (Exception ex) { return (shown, ex.GetType().Name); }
                return (shown, "completed");
            }
            var known = await Reset("known@example.test");
            var unknown = await Reset("unknown@example.test");
            Assert.Equal("If an account exists for this e-mail, a password reset link has been sent.", known.Shown);
            Assert.Equal(known, unknown);   // same text, same outcome (no reset link opened within the wait)
            Assert.DoesNotContain("example.test", known.Shown);
        }
        finally { Core.Languages.Loc.Language = before; }
    }

    /// <summary>rc.5: the forgot-password state shows the same neutral notice for known and unknown addresses in every UI language.</summary>
    [Theory]
    [InlineData("en")] [InlineData("es")] [InlineData("fr")] [InlineData("de")] [InlineData("pt")]
    [InlineData("it")] [InlineData("ar")] [InlineData("fa")] [InlineData("zh")] [InlineData("hi")]
    public async Task PasswordReset_NeutralNotice_IsIdenticalForKnownAndUnknown_InEveryLanguage(string lang)
    {
        // The language is passed explicitly: switching the global UI language would race with tests running in parallel.
        async Task<(string? Shown, string Outcome)> Reset(string email)
        {
            var stub = new Stub((r, b) => b.Contains("known@example.test") && !b.Contains("unknown@")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
                : Err(HttpStatusCode.NotFound, "user_not_found", "User not found"));
            var auth = new SupabaseAuth(new HttpClient(stub), Opt, new MemoryStore());
            string? shown = null;
            try { await auth.RecoverPasswordAsync(email, TimeSpan.FromMilliseconds(200), default, () => shown = App.UiState.PasswordResetCopy.RequestedIn(lang)); }
            catch (Exception ex) { return (shown, ex.GetType().Name); }
            return (shown, "completed");
        }
        var known = await Reset("known@example.test");
        var unknown = await Reset("unknown@example.test");
        Assert.Equal(Core.Languages.Loc.TableFor(lang)["acct_reset_requested"], known.Shown);
        Assert.Equal(known, unknown);
        Assert.DoesNotContain("example.test", known.Shown);
        Assert.DoesNotContain("{0}", known.Shown);
        if (lang != "en") Assert.NotEqual(Core.Languages.Loc.TableFor("en")["acct_reset_requested"], known.Shown);
    }

    [Fact]
    public async Task PasswordReset_StillReportsTheGeneralRateLimit()
    {
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => Err(HttpStatusCode.TooManyRequests, "over_request_rate_limit", "x"))), Opt, new MemoryStore());
        var accepted = false;
        var ex = await Assert.ThrowsAsync<AuthException>(() => auth.RecoverPasswordAsync("a@b.test", TimeSpan.FromSeconds(1), default, () => accepted = true));
        Assert.Equal("over_request_rate_limit", ex.Code);
        Assert.False(accepted);
    }

    [Fact]
    public void PerAddressEmailRateLimit_IsTreatedAsAccepted_BecauseOnlyExistingAccountsHitIt()
    {
        // Supabase limits reset e-mails per address only for addresses that have an account: surfacing it would tell a
        // second, quick request whether the account exists. The first link of that minute was sent.
        Assert.True(SupabaseAuth.RevealsAccountExistence("over_email_send_rate_limit"));
        Assert.False(SupabaseAuth.RevealsAccountExistence("over_request_rate_limit"));
        Assert.False(SupabaseAuth.RevealsAccountExistence("validation_failed"));
    }

    [Fact]
    public async Task ExpiredRefreshMarksSessionExpired()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-07T09:00:00Z"));
        var store = new MemoryStore { Saved = new AuthSession("old", "gone", time.Now.AddSeconds(-1), "u", "e") };
        var auth = new SupabaseAuth(new HttpClient(new Stub((_, _) => Err(HttpStatusCode.BadRequest, "refresh_token_not_found", "x"))), Opt, store, time);
        Assert.Null(await auth.GetAccessTokenAsync(default));
        Assert.Equal("expired", auth.SignedOutReason);
        await auth.SignOutAsync(default);
        Assert.Equal("user", auth.SignedOutReason);
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
