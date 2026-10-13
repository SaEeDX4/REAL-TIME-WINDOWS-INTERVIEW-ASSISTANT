using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.App.Services;

public enum CloudState { NotConfigured, Connecting, ServiceUnavailable, UpdateRequired, SignedOut, SessionExpired, AccountDisabled, SignedIn }

/// <summary>
/// Desktop side of the commercial service: account state, e-mail/password (and optional OAuth) sign-in through Supabase,
/// entitlements, device registration, upgrade and billing links. Only the API base URL ships with the app (cloud.json);
/// the publishable sign-in configuration comes from the service and is cached for offline start. Tokens are DPAPI-encrypted.
/// </summary>
public sealed class CloudService
{
    public CloudOptions Options { get; }
    public SupabaseAuth? Auth { get; }
    public BackendClient? Api { get; }
    public AccountDto? Account { get; private set; }
    public RuntimeConfig? Config { get; private set; }
    public List<PlanDto> Plans { get; private set; } = new();
    public CloudState State { get; private set; }
    /// <summary>Plain-language explanation of the current state (never a raw exception).</summary>
    public string? Message { get; private set; }
    public bool IsConfigured => Options.IsConfigured;
    public bool IsSignedIn => Auth?.IsSignedIn == true;
    public bool UpgradePending { get; private set; }
    public event Action? Changed;

    /// <summary>Self-test: point the client at an unreachable endpoint to exercise the offline UI without touching production.</summary>
    public static string? ForcedApiBaseUrl { get; set; }

    private readonly AppSettings _settings;
    private static readonly HttpClient AuthHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string PublicConfigCache => Path.Combine(AppPaths.Roaming, "service-config.json");
    private CancellationTokenSource? _upgradeCts;

    public CloudService(AppSettings settings)
    {
        _settings = settings;
        Options = LoadOptions();
        Options.ClientVersion = typeof(CloudService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        if (!Options.IsConfigured) { State = CloudState.NotConfigured; Message = "This build is not connected to the Interview Assistant service."; return; }
        LoadCachedPublicConfig();
        Auth = new SupabaseAuth(AuthHttp, Options, new ProtectedFileTokenStore(Path.Combine(AppPaths.Roaming, "session.dpapi"), new DpapiProtector()));
        Auth.SessionChanged += _ => { if (!Auth.IsSignedIn) { Account = null; SetState(StateWhenSignedOut(), SignedOutMessage()); } };
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(8) })
        {
            BaseAddress = new Uri(Options.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,   // streaming answers; per-call cancellation instead
        };
        Api = BackendClient.ForAuth(http, Auth, Options.ClientVersion);
        State = IsSignedIn ? CloudState.Connecting : CloudState.SignedOut;
    }

    /// <summary>cloud.json next to the executable (ships per release channel). Developer builds may override it from %APPDATA%.</summary>
    private static CloudOptions LoadOptions()
    {
        var paths = BuildFlavor.Developer
            ? new[] { Path.Combine(AppPaths.Roaming, "cloud.json"), Path.Combine(AppContext.BaseDirectory, "cloud.json") }
            : new[] { Path.Combine(AppContext.BaseDirectory, "cloud.json") };
        CloudOptions o = new();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) { o = JsonSerializer.Deserialize<CloudOptions>(File.ReadAllText(path), Json) ?? new(); break; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { AppLog.Warn("cloud.json unreadable: " + ex.GetType().Name); }
        }
        if (ForcedApiBaseUrl != null) o.ApiBaseUrl = ForcedApiBaseUrl;
        return o;
    }

    private void ApplyPublicConfig(RuntimeConfig cfg)
    {
        Config = cfg;
        if (cfg.Auth is { } a)
        {
            Options.SupabaseUrl = a.Available ? a.SupabaseUrl : "";
            Options.SupabaseAnonKey = a.Available ? a.PublishableKey : "";
            Options.OAuthProviders = a.OAuthProviders.ToArray();
            Options.EmailPassword = a.EmailPassword;
            Options.EmailConfirmedRedirectUrl = a.EmailConfirmedRedirectUrl;
        }
    }

    private void LoadCachedPublicConfig()
    {
        try
        {
            if (File.Exists(PublicConfigCache) && JsonSerializer.Deserialize<RuntimeConfig>(File.ReadAllText(PublicConfigCache), Json) is { } cfg) ApplyPublicConfig(cfg);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { AppLog.Warn("Cached service config ignored: " + ex.GetType().Name); }
    }

    private void CachePublicConfig(RuntimeConfig cfg)
    {
        try { File.WriteAllText(PublicConfigCache, JsonSerializer.Serialize(cfg, Json)); }   // public values only
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Could not cache service config: " + ex.GetType().Name); }
    }

    private CloudState StateWhenSignedOut() => Auth?.SignedOutReason switch { "expired" => CloudState.SessionExpired, "disabled" => CloudState.AccountDisabled, _ => CloudState.SignedOut };
    private string SignedOutMessage() => Auth?.SignedOutReason switch
    {
        "expired" => "Your session expired. Please sign in again.",
        "disabled" => "This account has been disabled. Contact support.",
        _ => "Sign in to use live interview help.",
    };

    private void SetState(CloudState s, string? message)
    {
        State = s; Message = message;
        Changed?.Invoke();
    }

    /// <summary>Refreshes service config, account and device registration. Never throws; updates <see cref="State"/>.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (Api == null) return false;
        if (State is CloudState.ServiceUnavailable or CloudState.SignedOut or CloudState.SessionExpired && IsSignedIn) SetState(CloudState.Connecting, "Connecting…");
        try
        {
            var cfg = await Api.ConfigAsync(ct);
            ApplyPublicConfig(cfg);
            CachePublicConfig(cfg);
            if (Plans.Count == 0) { try { Plans = await Api.PlansAsync(ct); } catch (BackendException) { } }
            if (!IsSignedIn)
            {
                SetState(StateWhenSignedOut(), Options.AuthReady ? SignedOutMessage() : "Sign-in is temporarily unavailable. Please try again later.");
                return true;
            }
            Account = await Api.MeAsync(ct);
            await EnsureDeviceAsync(ct);
            SetState(CloudState.SignedIn, null);
            return true;
        }
        catch (BackendException ex)
        {
            AppLog.Warn($"Cloud refresh failed: {ex.Code} (ref {ex.CorrelationId})");
            var (state, message) = Classify(ex);
            if (state == CloudState.SignedOut && Auth != null) { Auth.SignOutLocal(); Account = null; }
            SetState(state, message);
            return false;
        }
    }

    private (CloudState, string) Classify(BackendException ex) => ex switch
    {
        { IsUpdateRequired: true } => (CloudState.UpdateRequired, "This version is no longer supported. Please install the latest version."),
        { Code: "account_disabled" } => (CloudState.AccountDisabled, "This account has been disabled. Contact support."),
        { Code: "account_deleted" } => (CloudState.SignedOut, "This account was deleted."),
        { Status: 401 } => (IsSignedIn ? CloudState.SessionExpired : CloudState.SignedOut, "Your session expired. Please sign in again."),
        _ => (CloudState.ServiceUnavailable, Describe(ex)),
    };

    /// <summary>User-facing text for any service error (status class + server message, plus a short reference id).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        BackendException { Status: 0 } => "Cannot reach the Interview Assistant service. Check your internet connection.",
        BackendException { Status: 429 } b => b.Message.Length > 0 ? b.Message : "Too many requests — please wait a moment.",
        BackendException { Status: >= 500 } b => (b.Code == "maintenance" ? b.Message : "The service is temporarily unavailable. Please try again shortly.") + Ref(b),
        BackendException b => b.Message + Ref(b),
        AuthException a => a.Message,
        TimeoutException => "That took too long. Please try again.",
        _ => "Something went wrong. Please try again.",
    };

    private static string Ref(BackendException b) => b.CorrelationId is { Length: >= 8 } c ? $" (ref {c[..8]})" : "";

    // ---------------- authentication ----------------

    private async Task<(bool Ok, string? Error)> AuthAction(Func<Task> action, string log)
    {
        if (Auth == null) return (false, Message);
        try
        {
            if (!Options.AuthReady) await RefreshAsync(CancellationToken.None);   // fetch sign-in config on first use
            await action();
            AppLog.Info(log);
            return (true, null);
        }
        catch (Exception ex) when (ex is AuthException or TimeoutException or BackendException)
        {
            AppLog.Warn($"{log} failed: {(ex as AuthException)?.Code ?? (ex as BackendException)?.Code ?? ex.GetType().Name}");
            return (false, Describe(ex));
        }
    }

    public async Task<(bool Ok, string? Error)> SignInAsync(string email, string password)
    {
        var r = await AuthAction(() => Auth!.SignInWithPasswordAsync(email, password, CancellationToken.None), "Signed in (e-mail)");
        if (r.Ok) await RefreshAsync(CancellationToken.None);
        return r;
    }

    /// <summary>Returns ConfirmationRequired=true when Supabase sent a confirmation e-mail.</summary>
    public async Task<(bool Ok, bool ConfirmationRequired, string? Error)> SignUpAsync(string email, string password)
    {
        SupabaseAuth.SignUpResult? result = null;
        var r = await AuthAction(async () => result = await Auth!.SignUpAsync(email, password, CancellationToken.None), "Account created");
        if (r.Ok && result?.SignedIn == true) await RefreshAsync(CancellationToken.None);
        return (r.Ok, result?.ConfirmationRequired == true, r.Error);
    }

    public async Task<(bool Ok, string? Error)> SignInWithProviderAsync(string provider)
    {
        var r = await AuthAction(() => Auth!.SignInInteractiveAsync(url => { OpenExternal(url); return Task.CompletedTask; }, provider, null, TimeSpan.FromMinutes(5), CancellationToken.None), "Signed in (" + provider + ")");
        if (r.Ok) await RefreshAsync(CancellationToken.None);
        return r;
    }

    /// <summary>Password reset: the e-mailed link returns to this PC; then <paramref name="askNewPassword"/> collects the new password.</summary>
    public async Task<(bool Ok, string? Error)> ResetPasswordAsync(string email, Func<Task<string?>> askNewPassword)
    {
        var r = await AuthAction(async () =>
        {
            await Auth!.RecoverPasswordAsync(email, TimeSpan.FromMinutes(15), CancellationToken.None);
            var pw = await askNewPassword() ?? throw new AuthException("cancelled", "Password not changed.");
            await Auth.UpdatePasswordAsync(pw, CancellationToken.None);
        }, "Password reset");
        if (r.Ok) await RefreshAsync(CancellationToken.None);
        return r;
    }

    public Task<(bool Ok, string? Error)> ChangePasswordAsync(string newPassword) =>
        AuthAction(() => Auth!.UpdatePasswordAsync(newPassword, CancellationToken.None), "Password changed");

    public async Task SignOutAsync()
    {
        if (Auth == null) return;
        _upgradeCts?.Cancel();
        await Auth.SignOutAsync(CancellationToken.None);
        Account = null;
        SetState(CloudState.SignedOut, "Signed out.");
    }

    public async Task<Guid> EnsureDeviceAsync(CancellationToken ct)
    {
        var installation = InstallationIdentity.GetOrCreate(Path.Combine(AppPaths.Local, "installation.id"));
        var name = Environment.MachineName is { Length: > 0 and <= 60 } m ? m : "Windows PC";
        var dev = await Api!.RegisterDeviceAsync(new RegisterDeviceRequest(installation, name, "windows-x64", Options.ClientVersion), ct);
        if (_settings.CloudDeviceId != dev.Id) { _settings.CloudDeviceId = dev.Id; _settings.Save(); }
        return dev.Id;
    }

    // ---------------- billing ----------------

    public static bool IsPaidState(string? s) => s is BillingStates.Active or BillingStates.Canceling or BillingStates.PastDue;

    /// <summary>
    /// Upgrade: server creates a Paddle checkout for the signed-in user → browser → Paddle webhook updates the account.
    /// The app then polls the account (every 5 s, up to 15 min) until the paid plan is active.
    /// </summary>
    public async Task<(bool Ok, string? Error)> StartUpgradeAsync(string planCode)
    {
        if (Api == null || !IsSignedIn) return (false, "Sign in first.");
        try
        {
            var url = (await Api.CheckoutAsync(planCode, CancellationToken.None)).Url;
            if (!OpenExternal(url)) return (false, "The checkout link was not valid. Please try again.");
            AppLog.Info("Checkout opened");
            _upgradeCts?.Cancel();
            _upgradeCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            UpgradePending = true; Changed?.Invoke();
            _ = WaitForUpgradeAsync(_upgradeCts.Token);
            return (true, null);
        }
        catch (BackendException ex) { return (false, Describe(ex)); }
    }

    private async Task WaitForUpgradeAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await RefreshAsync(ct);
                if (IsPaidState(Account?.BillingState)) { AppLog.Info("Upgrade confirmed by the service"); break; }
            }
        }
        catch (OperationCanceledException) { }
        finally { UpgradePending = false; Changed?.Invoke(); }
    }

    public async Task<(bool Ok, string? Error)> OpenPortalAsync()
    {
        if (Api == null || !IsSignedIn) return (false, "Sign in first.");
        try { return OpenExternal((await Api.PortalAsync(CancellationToken.None)).Url) ? (true, null) : (false, "The billing link was not valid."); }
        catch (BackendException ex) { return (false, Describe(ex)); }
    }

    /// <summary>Opens https URLs in the default browser (checkout, portal, sign-in, legal pages). Anything else is refused.</summary>
    public static bool OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.HostNameType != UriHostNameType.Dns)
        {
            AppLog.Warn("Refused to open a non-https or IP-literal link");
            return false;
        }
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
