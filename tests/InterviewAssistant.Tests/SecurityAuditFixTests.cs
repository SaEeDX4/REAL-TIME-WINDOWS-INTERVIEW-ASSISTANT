using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Client;
using InterviewAssistant.Core.Persistence;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// Regression tests for the rc.5 account/auth/export security review: the password-reset wait never outlives the reset
/// screen nor swaps the signed-in account (H1), the reset cooldown (M2), older auth servers' per-address limit (L7) and
/// the reset form's focus (L8).
/// </summary>
public class SecurityAuditFixTests
{
    // ------------------------------------------------------------------ helpers

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", relative)).Replace("\r\n", "\n");

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (private|public|internal|protected) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((r, body));
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

    private static readonly CloudOptions Opt = new() { ApiBaseUrl = "https://api.example.test", SupabaseUrl = "https://proj.supabase.test", SupabaseAnonKey = "anon-public", OAuthProviders = new[] { "google" } };

    private static HttpResponseMessage Tokens(string access, string userId, string email) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = access, refresh_token = "rt-" + access, expires_in = 3600, user = new { id = userId, email } }) };

    /// <summary>Starts a reset, opens its link with <paramref name="code"/> and returns the outcome.</summary>
    private static async Task<Exception?> OpenResetLink(SupabaseAuth auth, Func<string?> redirect, string email, string code = "RESET")
    {
        var recover = auth.RecoverPasswordAsync(email, TimeSpan.FromSeconds(20), default);
        for (int i = 0; i < 200 && redirect() == null; i++) await Task.Delay(20);
        using (var http = new HttpClient()) (await http.GetAsync(redirect() + "&code=" + code)).EnsureSuccessStatusCode();
        try { await recover; return null; }
        catch (Exception ex) { return ex; }
    }

    private static Stub ResetServer(Action<string> onRedirect, Func<HttpRequestMessage, HttpResponseMessage>? token = null) => new((r, _) =>
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/recover"))
        {
            onRedirect(Uri.UnescapeDataString(r.RequestUri.Query.Split("redirect_to=")[1]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
        return token?.Invoke(r) ?? Tokens("at-b", "user-b", "b@x.test");
    });

    // ------------------------------------------------------------------ H1: the reset link never swaps the signed-in account

    [Fact]
    public async Task ResetLink_OpenedWhileAnotherAccountIsSignedIn_IsRefused_AndTheSessionStays()
    {
        string? redirect = null;
        var stub = ResetServer(u => redirect = u);
        var a = new AuthSession("at-a", "rt-a", DateTimeOffset.UtcNow.AddHours(1), "user-a", "a@x.test");
        var store = new MemoryStore { Saved = a };
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, store);

        var ex = await OpenResetLink(auth, () => redirect, "b@x.test");

        Assert.Equal("signed_in_other_account", Assert.IsType<AuthException>(ex).Code);
        Assert.Equal(a, auth.Session);            // A is still signed in
        Assert.Equal(a, store.Saved);
        Assert.DoesNotContain(stub.Calls, c => c.Req.RequestUri!.Query.Contains("grant_type=pkce"));   // the code was never exchanged
    }

    [Fact]
    public async Task ResetLink_ForTheSignedInAccount_StillWorks()
    {
        string? redirect = null;
        var stub = ResetServer(u => redirect = u);
        var store = new MemoryStore { Saved = new AuthSession("at-old", "rt-old", DateTimeOffset.UtcNow.AddHours(1), "user-b", "B@x.test") };
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, store);

        Assert.Null(await OpenResetLink(auth, () => redirect, "b@x.test"));
        Assert.Equal("at-b", auth.Session!.AccessToken);
        Assert.Equal("user-b", auth.Session.UserId);
    }

    [Fact]
    public async Task ResetLink_ExchangedWhileAnotherAccountSignsIn_DoesNotReplaceThatSession()
    {
        string? redirect = null;
        SupabaseAuth? auth = null;
        var stub = ResetServer(u => redirect = u, r =>
        {
            if (r.RequestUri!.Query.Contains("grant_type=password")) return Tokens("at-a", "user-a", "a@x.test");
            // While the recovery code is being exchanged, A signs in on this PC.
            Task.Run(() => auth!.SignInWithPasswordAsync("a@x.test", "pw", default)).GetAwaiter().GetResult();
            return Tokens("at-b", "user-b", "b@x.test");
        });
        var store = new MemoryStore();
        auth = new SupabaseAuth(new HttpClient(stub), Opt, store);

        var ex = await OpenResetLink(auth, () => redirect, "b@x.test");

        Assert.Equal("signed_in_other_account", Assert.IsType<AuthException>(ex).Code);
        Assert.Equal("user-a", auth.Session!.UserId);
        Assert.Equal("user-a", store.Saved!.UserId);
    }

    [Fact]
    public async Task ResetLink_WhenSignedOut_SignsInTheResetAccount()
    {
        string? redirect = null;
        var auth = new SupabaseAuth(new HttpClient(ResetServer(u => redirect = u)), Opt, new MemoryStore());
        Assert.Null(await OpenResetLink(auth, () => redirect, "b@x.test"));
        Assert.Equal("user-b", auth.Session!.UserId);
    }

    [Fact]
    public void PendingReset_LeavingOrReplacing_CancelsTheWait_AndOnlyTheCurrentTicketMayTouchTheForm()
    {
        var r = new PendingReset();
        var first = r.Begin("b@x.test");
        Assert.True(r.IsPending);
        Assert.True(r.IsPendingFor("B@x.test"));
        Assert.True(r.IsCurrent(first));

        // A newer request replaces the first: its wait is cancelled and it may no longer clear Busy or reset the form.
        var second = r.Begin("c@x.test");
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(r.IsCurrent(first));
        Assert.False(r.Release(first));
        Assert.True(r.IsCurrent(second));
        Assert.True(second.Generation > first.Generation);

        // Leaving the reset screen (Back to sign in, a sign-in, sign-out, switch, closing the window).
        Assert.True(r.Cancel());
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(r.IsPending);
        Assert.False(r.IsCurrent(second));
        Assert.False(r.Release(second));   // the stale task's finally leaves Busy alone
        Assert.False(r.Cancel());          // nothing left to cancel
        first.Dispose(); second.Dispose();
        second.Dispose();                  // idempotent

        // A finished request is released without being cancelled, exactly once.
        var third = r.Begin("b@x.test");
        Assert.True(r.Release(third));
        Assert.False(third.Token.IsCancellationRequested);
        Assert.False(r.Release(third));
        Assert.False(r.IsPending);
        third.Dispose();
    }

    [Fact]
    public void PendingReset_ANewRequestForTheSameAddress_WaitsOutTheServersPerAddressInterval()
    {
        var r = new PendingReset();
        var t0 = DateTimeOffset.Parse("2026-10-19T10:00:00Z");
        var interval = AuthFormState.ResendCooldown;
        Assert.Equal(TimeSpan.Zero, r.WaitBeforeSending("b@x.test", t0, interval));
        r.MarkRequested("b@x.test", t0);
        Assert.Equal(TimeSpan.FromSeconds(50), r.WaitBeforeSending("B@x.test", t0.AddSeconds(10), interval));
        Assert.Equal(TimeSpan.Zero, r.WaitBeforeSending("c@x.test", t0.AddSeconds(10), interval));   // another address
        Assert.Equal(TimeSpan.Zero, r.WaitBeforeSending("b@x.test", t0 + interval, interval));
    }

    [Fact]
    public void AuthForm_InResetFlow_OnlyOnTheResetScreens()
    {
        var s = new AuthFormState();
        Assert.False(s.InResetFlow);
        Assert.True(s.ToForgotPassword());
        Assert.True(s.InResetFlow);
        Assert.True(s.ResetRequested(DateTimeOffset.UtcNow));
        Assert.True(s.InResetFlow);
        Assert.True(s.ToSignIn());
        Assert.False(s.InResetFlow);
        s.ToForgotPassword();
        s.StartSwitch("a@x.test");
        Assert.False(s.InResetFlow);
        s.ToForgotPassword();
        s.Reset();
        Assert.False(s.InResetFlow);
    }

    [Fact]
    public void HomeWindow_CancelsThePendingReset_WhenTheResetScreenIsLeft_OnSignInSignOutSwitchAndClose()
    {
        var src = App("HomeWindow.Account.cs");
        Assert.DoesNotContain("_resetCts", src);
        var init = Method(src, "private void InitAuthForm(");
        Assert.Contains("if (!_auth.InResetFlow) CancelPendingReset(", init);
        Assert.Contains("Closed += (_, _) => CancelPendingReset(", init);
        Assert.Contains("CancelPendingReset(\"signed in\")", Method(src, "private async Task RunAuth("));
        Assert.Contains("CancelPendingReset(", Method(src, "private async void SignOut_Click("));
        Assert.Contains("CancelPendingReset(", Method(src, "private async void SwitchAccount_Click("));

        // A stale reset task never clears Busy or resets the form.
        var reset = Method(src, "private async Task RequestResetAsync(");
        Assert.Contains("if (!_reset.IsCurrent(ticket)) return;", reset);
        Assert.Contains("if (_reset.Release(ticket)) _auth.SetBusy(false);", reset);
        Assert.Contains("if (_reset.IsCurrent(ticket)) Requested();", reset);
        Assert.True(reset.IndexOf("if (!_reset.IsCurrent(ticket)) return;", StringComparison.Ordinal) < reset.IndexOf("_auth.Reset();", StringComparison.Ordinal));
    }

    [Fact]
    public void CloudService_RefusesAResetWhileAnotherAccountIsSignedIn()
    {
        var m = Method(App("Services/CloudService.cs"), "public async Task<(bool Ok, string? Error)> ResetPasswordAsync(");
        Assert.True(m.IndexOf("Auth?.Session is { } current", StringComparison.Ordinal) < m.IndexOf("RecoverPasswordAsync(", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ L7: older auth servers' per-address limit

    [Theory]
    [InlineData(429, "http_429", "For security purposes, you can only request this after 42 seconds.", true)]
    [InlineData(429, "http_429", "for security purposes, you can only request this once every 60 seconds", true)]
    [InlineData(429, "over_request_rate_limit", "For security purposes, slow down", false)]   // the general limit is still reported
    [InlineData(429, "http_429", "Too many requests", false)]
    [InlineData(400, "http_400", "For security purposes, you can only request this after 42 seconds.", false)]
    public void PerAddressLimitOfOlderAuthServers_IsRecognised(int status, string code, string message, bool expected) =>
        Assert.Equal(expected, SupabaseAuth.IsPerAddressRateLimit(status, code, message));

    [Fact]
    public async Task PasswordReset_OlderServersPerAddressLimit_IsTreatedAsAccepted_NotShownRaw()
    {
        var stub = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = JsonContent.Create(new { code = 429, msg = "For security purposes, you can only request this after 42 seconds." }) });
        var auth = new SupabaseAuth(new HttpClient(stub), Opt, new MemoryStore());
        var accepted = false;
        // Accepted like any request; the listener then waits for a link (which this test never opens).
        await Assert.ThrowsAsync<TimeoutException>(() => auth.RecoverPasswordAsync("a@b.test", TimeSpan.FromMilliseconds(200), default, () => accepted = true));
        Assert.True(accepted);
    }

    // ------------------------------------------------------------------ L8: focus after the reset request and after an error

    [Fact]
    public void ResetForm_MovesTheFocus_AfterTheRequestAndAfterAnError()
    {
        var src = App("HomeWindow.Account.cs");
        var reset = Method(src, "private async Task RequestResetAsync(");
        var requested = reset[reset.IndexOf("void Requested()", StringComparison.Ordinal)..reset.IndexOf("if (!force", StringComparison.Ordinal)];
        Assert.True(requested.IndexOf("_auth.ResetRequested(", StringComparison.Ordinal) < requested.IndexOf("FocusAuthForm();", StringComparison.Ordinal));
        var error = Method(src, "private void ShowResetError(");
        Assert.True(error.IndexOf("_auth.ResetFailed();", StringComparison.Ordinal) < error.IndexOf("FocusAuthForm();", StringComparison.Ordinal));
    }
}
