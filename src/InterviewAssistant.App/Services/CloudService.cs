using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Desktop side of the commercial service: sign-in (Supabase PKCE via the system browser), account/entitlements,
/// device registration, billing links. Endpoints come from cloud.json (public values only). Tokens are DPAPI-encrypted.
/// </summary>
public sealed class CloudService
{
    public CloudOptions Options { get; }
    public SupabaseAuth? Auth { get; }
    public BackendClient? Api { get; }
    public AccountDto? Account { get; private set; }
    public RuntimeConfig? Config { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConfigured => Options.IsConfigured;
    public bool IsSignedIn => Auth?.IsSignedIn == true;
    public event Action? Changed;

    private readonly AppSettings _settings;
    private static readonly HttpClient AuthHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public CloudService(AppSettings settings)
    {
        _settings = settings;
        Options = LoadOptions();
        Options.ClientVersion = typeof(CloudService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        if (!Options.IsConfigured) return;
        Auth = new SupabaseAuth(AuthHttp, Options, new ProtectedFileTokenStore(Path.Combine(AppPaths.Roaming, "session.dpapi"), new DpapiProtector()));
        Auth.SessionChanged += _ => { if (!Auth.IsSignedIn) Account = null; Changed?.Invoke(); };
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(8) })
        {
            BaseAddress = new Uri(Options.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,   // streaming answers; per-call cancellation instead
        };
        Api = BackendClient.ForAuth(http, Auth, Options.ClientVersion);
    }

    /// <summary>cloud.json next to the executable (shipped per channel), overridable by %APPDATA%\InterviewAssistant\cloud.json for staging.</summary>
    private static CloudOptions LoadOptions()
    {
        foreach (var path in new[] { Path.Combine(AppPaths.Roaming, "cloud.json"), Path.Combine(AppContext.BaseDirectory, "cloud.json") })
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<CloudOptions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException) { AppLog.Warn("cloud.json unreadable: " + ex.Message); }
        }
        return new CloudOptions();
    }

    public async Task<bool> SignInAsync(string? provider, string? email, CancellationToken ct)
    {
        if (Auth == null) return false;
        try
        {
            await Auth.SignInInteractiveAsync(url => { OpenExternal(url); return Task.CompletedTask; }, provider, email, TimeSpan.FromMinutes(5), ct);
            AppLog.Info("Signed in");
            await RefreshAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is AuthException or TimeoutException or HttpRequestException or BackendException)
        {
            LastError = ex.Message;
            AppLog.Warn("Sign-in failed: " + ex.GetType().Name + " " + (ex as AuthException)?.Code);
            return false;
        }
    }

    public async Task SignOutAsync()
    {
        if (Auth == null) return;
        await Auth.SignOutAsync(CancellationToken.None);
        Account = null;
        Changed?.Invoke();
    }

    /// <summary>Refreshes runtime config, account and device registration. Never throws; sets LastError.</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (Api == null) return false;
        try
        {
            Config = await Api.ConfigAsync(ct);
            if (!IsSignedIn) { Changed?.Invoke(); return true; }
            Account = await Api.MeAsync(ct);
            await EnsureDeviceAsync(ct);
            LastError = null;
            Changed?.Invoke();
            return true;
        }
        catch (BackendException ex)
        {
            LastError = ex.IsUpdateRequired ? "This version is no longer supported. Please update the app." : ex.Message;
            AppLog.Warn($"Cloud refresh failed: {ex.Code} (ref {ex.CorrelationId})");
            Changed?.Invoke();
            return false;
        }
    }

    public async Task<Guid> EnsureDeviceAsync(CancellationToken ct)
    {
        var installation = InstallationIdentity.GetOrCreate(Path.Combine(AppPaths.Local, "installation.id"));
        var dev = await Api!.RegisterDeviceAsync(new RegisterDeviceRequest(installation, Environment.MachineName.Length is > 0 and <= 60 ? Environment.MachineName : "Windows PC", "windows-x64", Options.ClientVersion), ct);
        if (_settings.CloudDeviceId != dev.Id) { _settings.CloudDeviceId = dev.Id; _settings.Save(); }
        return dev.Id;
    }

    public async Task OpenCheckoutAsync(string planCode, CancellationToken ct) => OpenExternal((await Api!.CheckoutAsync(planCode, ct)).Url);
    public async Task OpenPortalAsync(CancellationToken ct) => OpenExternal((await Api!.PortalAsync(ct)).Url);

    /// <summary>Opens https URLs (and the loopback sign-in) in the default browser. Anything else is refused.</summary>
    public static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { AppLog.Warn("Refused to open non-https URL"); return; }
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
