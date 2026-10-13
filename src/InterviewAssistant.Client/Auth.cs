using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewAssistant.Core.Persistence;

namespace InterviewAssistant.Client;

public sealed class AuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record AuthSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId, string Email);

public interface ITokenStore
{
    AuthSession? Load();
    void Save(AuthSession session);
    void Clear();
}

/// <summary>Refresh/access tokens encrypted with the supplied protector (DPAPI on Windows). Never logged.</summary>
public sealed class ProtectedFileTokenStore(string path, IDataProtector protector) : ITokenStore
{
    public AuthSession? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AuthSession>(protector.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException) { return null; }
    }

    public void Save(AuthSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(session)));
        File.Move(tmp, path, overwrite: true);
    }

    public void Clear() { try { File.Delete(path); } catch (IOException) { } }
}

/// <summary>
/// Supabase Auth with PKCE through the system browser (OAuth providers or e-mail magic link). The desktop never sees a
/// password. Tokens are refreshed transparently and stored only through <see cref="ITokenStore"/>.
/// </summary>
public sealed class SupabaseAuth
{
    private readonly HttpClient _http;
    private readonly CloudOptions _opt;
    private readonly ITokenStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AuthSession? _session;

    public event Action<AuthSession?>? SessionChanged;
    /// <summary>Why the last sign-out happened: "expired" (refresh token rejected), "disabled", or "user".</summary>
    public string? SignedOutReason { get; private set; }
    public AuthSession? Session => _session;
    public bool IsSignedIn => _session != null;

    public SupabaseAuth(HttpClient http, CloudOptions opt, ITokenStore store, TimeProvider? time = null)
    {
        _http = http; _opt = opt; _store = store; _time = time ?? TimeProvider.System;
        _session = store.Load();
    }

    private string AuthBase => _opt.SupabaseUrl.TrimEnd('/') + "/auth/v1";

    /// <summary>URL to open in the system browser for an OAuth provider.</summary>
    public string BuildAuthorizeUrl(string provider, string codeChallenge, string redirectUri)
    {
        if (!_opt.OAuthProviders.Contains(provider, StringComparer.OrdinalIgnoreCase)) throw new AuthException("provider_not_enabled", "This sign-in provider is not enabled.");
        return $"{AuthBase}/authorize?provider={Uri.EscapeDataString(provider)}&redirect_to={Uri.EscapeDataString(redirectUri)}" +
               $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=s256";
    }

    /// <summary>Sends a magic link whose redirect returns to the loopback listener with a PKCE code.</summary>
    public async Task SendMagicLinkAsync(string email, string codeChallenge, string redirectUri, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, $"{AuthBase}/otp?redirect_to={Uri.EscapeDataString(redirectUri)}",
            new { email, create_user = true, code_challenge = codeChallenge, code_challenge_method = "s256" });
        using var resp = await Send(req, ct);
        if (!resp.IsSuccessStatusCode) throw await Fail(resp, "Could not send the sign-in e-mail.");
    }

    public async Task<AuthSession> ExchangeCodeAsync(string authCode, string verifier, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, $"{AuthBase}/token?grant_type=pkce", new { auth_code = authCode, code_verifier = verifier });
        return await TokenRequestAsync(req, ct);
    }

    /// <summary>Result of a sign-up: either signed in immediately, or the user must confirm the e-mail first.</summary>
    public sealed record SignUpResult(bool SignedIn, bool ConfirmationRequired);

    /// <summary>Creates an account with e-mail + password. Supabase may require e-mail confirmation before sign-in.</summary>
    public async Task<SignUpResult> SignUpAsync(string email, string password, CancellationToken ct)
    {
        RequireReady();
        var redirect = _opt.EmailConfirmedRedirectUrl is { Length: > 0 } r ? "?redirect_to=" + Uri.EscapeDataString(r) : "";
        using var req = Request(HttpMethod.Post, $"{AuthBase}/signup{redirect}", new { email = email.Trim(), password });
        using var resp = await Send(req, ct);
        if (!resp.IsSuccessStatusCode) throw await Fail(resp, "The account could not be created.");
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Contains("\"access_token\"", StringComparison.Ordinal))
        {
            await StoreTokensAsync(body, ct);
            return new SignUpResult(true, false);
        }
        return new SignUpResult(false, true);
    }

    /// <summary>E-mail + password sign-in (no browser needed).</summary>
    public async Task<AuthSession> SignInWithPasswordAsync(string email, string password, CancellationToken ct)
    {
        RequireReady();
        using var req = Request(HttpMethod.Post, $"{AuthBase}/token?grant_type=password", new { email = email.Trim(), password });
        return await TokenRequestAsync(req, ct);
    }

    /// <summary>
    /// Password reset with PKCE: Supabase e-mails a link that returns to this PC's loopback listener with a one-time code;
    /// the code becomes a short session in which <see cref="UpdatePasswordAsync"/> sets the new password.
    /// </summary>
    public async Task<AuthSession> RecoverPasswordAsync(string email, TimeSpan timeout, CancellationToken ct)
    {
        RequireReady();
        var verifier = Pkce.CreateVerifier();
        using var loop = new LoopbackRedirect(Pkce.CreateState());
        using (var req = Request(HttpMethod.Post, $"{AuthBase}/recover?redirect_to={Uri.EscapeDataString(loop.RedirectUri)}",
                   new { email = email.Trim(), code_challenge = Pkce.Challenge(verifier), code_challenge_method = "s256" }))
        using (var resp = await Send(req, ct))
            if (!resp.IsSuccessStatusCode) throw await Fail(resp, "The reset e-mail could not be sent.");
        var code = await loop.WaitForCodeAsync(timeout, ct);
        return await ExchangeCodeAsync(code, verifier, ct);
    }

    /// <summary>Sets a new password for the signed-in user (used right after recovery, or from Account).</summary>
    public async Task UpdatePasswordAsync(string newPassword, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct) ?? throw new AuthException("signed_out", "Please sign in again.");
        using var req = Request(HttpMethod.Put, $"{AuthBase}/user", new { password = newPassword });
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await Send(req, ct);
        if (!resp.IsSuccessStatusCode) throw await Fail(resp, "The password could not be changed.");
    }

    private void RequireReady()
    {
        if (!_opt.AuthReady) throw new AuthException("auth_unavailable", "Sign-in is temporarily unavailable. Please try again in a moment.");
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new AuthException("network", "Cannot reach the sign-in service. Check your internet connection."); }
    }

    /// <summary>Full interactive flow: listener → browser → code → tokens.</summary>
    public async Task<AuthSession> SignInInteractiveAsync(Func<string, Task> openBrowser, string? provider, string? email, TimeSpan timeout, CancellationToken ct)
    {
        var verifier = Pkce.CreateVerifier();
        using var loop = new LoopbackRedirect(Pkce.CreateState());
        var challenge = Pkce.Challenge(verifier);
        if (!string.IsNullOrWhiteSpace(email)) await SendMagicLinkAsync(email, challenge, loop.RedirectUri, ct);
        else await openBrowser(BuildAuthorizeUrl(provider ?? _opt.OAuthProviders.First(), challenge, loop.RedirectUri));
        var code = await loop.WaitForCodeAsync(timeout, ct);
        return await ExchangeCodeAsync(code, verifier, ct);
    }

    /// <summary>Valid access token, refreshing when it expires within 60 s. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        var s = _session;
        if (s == null) return null;
        if (!forceRefresh && s.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromSeconds(60)) return s.AccessToken;
        await _refreshLock.WaitAsync(ct);
        try
        {
            s = _session;
            if (s == null) return null;
            if (!forceRefresh && s.ExpiresAt - _time.GetUtcNow() > TimeSpan.FromSeconds(60)) return s.AccessToken;
            if (!_opt.AuthReady) return s.AccessToken;   // service config not loaded yet: the API decides whether it is still valid
            using var req = Request(HttpMethod.Post, $"{AuthBase}/token?grant_type=refresh_token", new { refresh_token = s.RefreshToken });
            try { return (await TokenRequestAsync(req, ct)).AccessToken; }
            catch (AuthException ex) when (ex.Code is "invalid_grant" or "refresh_token_not_found" or "refresh_token_already_used" or "session_expired" or "session_not_found" or "user_banned" or "http_400" or "http_401")
            {
                SignedOutReason = ex.Code == "user_banned" ? "disabled" : "expired";
                SignOutLocal(keepReason: true);   // refresh token revoked/expired → user must sign in again
                return null;
            }
        }
        finally { _refreshLock.Release(); }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var s = _session;
        if (s != null)
        {
            try
            {
                using var req = Request(HttpMethod.Post, $"{AuthBase}/logout", new { });
                req.Headers.Authorization = new("Bearer", s.AccessToken);
                using var _ = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) { /* offline sign-out still clears local tokens */ }
        }
        SignOutLocal();
    }

    public void SignOutLocal(bool keepReason = false)
    {
        if (!keepReason) SignedOutReason = "user";
        _session = null;
        _store.Clear();
        SessionChanged?.Invoke(null);
    }

    private HttpRequestMessage Request(HttpMethod m, string url, object body)
    {
        var r = new HttpRequestMessage(m, url) { Content = JsonContent.Create(body) };
        r.Headers.Add("apikey", _opt.SupabaseAnonKey);
        return r;
    }

    private async Task<AuthSession> TokenRequestAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Send(req, ct);
        if (!resp.IsSuccessStatusCode) throw await Fail(resp, "Sign-in failed.");
        return await StoreTokensAsync(await resp.Content.ReadAsStringAsync(ct), ct);
    }

    private Task<AuthSession> StoreTokensAsync(string json, CancellationToken ct)
    {
        _ = ct;
        TokenResponse? t;
        try { t = JsonSerializer.Deserialize<TokenResponse>(json); }
        catch (JsonException) { throw new AuthException("bad_response", "Unexpected response from the sign-in service."); }
        if (t == null) throw new AuthException("bad_response", "Empty token response.");
        if (string.IsNullOrEmpty(t.AccessToken) || string.IsNullOrEmpty(t.RefreshToken)) throw new AuthException("bad_response", "Incomplete token response.");
        var session = new AuthSession(t.AccessToken, t.RefreshToken, _time.GetUtcNow().AddSeconds(Math.Max(60, t.ExpiresIn)), t.User?.Id ?? "", t.User?.Email ?? "");
        _session = session;
        SignedOutReason = null;
        _store.Save(session);
        SessionChanged?.Invoke(session);
        return Task.FromResult(session);
    }

    private static async Task<AuthException> Fail(HttpResponseMessage resp, string fallback)
    {
        string code = "http_" + (int)resp.StatusCode, msg = fallback;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString()!;
            else if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) code = e.GetString()!;
            if (doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String) msg = m.GetString()!;
            else if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) msg = d.GetString()!;
        }
        catch (JsonException) { }
        return new AuthException(code, Friendly(code, msg));
    }

    /// <summary>Plain-language messages for the codes users actually hit (raw provider text otherwise).</summary>
    public static string Friendly(string code, string fallback) => code switch
    {
        "invalid_credentials" or "invalid_grant" => "The e-mail or password is not correct.",
        "email_not_confirmed" => "Please confirm your e-mail address first — check your inbox for the confirmation link.",
        "user_already_exists" or "email_exists" => "An account with this e-mail already exists. Sign in instead.",
        "weak_password" => "Choose a stronger password (at least 8 characters, mixing letters and numbers).",
        "over_email_send_rate_limit" or "over_request_rate_limit" => "Too many attempts. Please wait a minute and try again.",
        "user_banned" => "This account has been disabled. Contact support.",
        "signup_disabled" => "New sign-ups are currently closed.",
        "validation_failed" => "Please enter a valid e-mail address.",
        _ => fallback,
    };

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("user")] public TokenUser? User { get; set; }
    }
    private sealed class TokenUser
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("email")] public string? Email { get; set; }
    }
}
