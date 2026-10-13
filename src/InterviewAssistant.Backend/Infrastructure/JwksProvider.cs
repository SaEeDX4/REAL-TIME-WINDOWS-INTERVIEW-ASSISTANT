using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InterviewAssistant.Backend.Infrastructure;

/// <summary>
/// The identity provider's JSON Web Key Set, refreshed asynchronously (never on the request thread): loaded at startup,
/// refreshed hourly, and refreshed in the background when a token carries an unknown key id (key rotation; at most once
/// per minute). Validation fails closed while no keys are known.
/// </summary>
public sealed class JwksProvider : BackgroundService
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ILogger<JwksProvider> _log;
    private readonly TimeProvider _time;
    private volatile IReadOnlyList<SecurityKey> _keys = Array.Empty<SecurityKey>();
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public JwksProvider(IHttpClientFactory http, IOptions<AuthOptions> auth, ILogger<JwksProvider> log, TimeProvider time)
    {
        _http = http.CreateClient("jwks");
        _url = auth.Value.JwksUrl;
        _log = log;
        _time = time;
    }

    public bool Enabled => _url.Length > 0;
    public int KeyCount => _keys.Count;
    public DateTimeOffset? LastRefreshed { get; private set; }

    public IEnumerable<SecurityKey> Resolve(string? kid)
    {
        var keys = _keys;
        var match = kid == null ? keys : keys.Where(k => k.KeyId == kid).ToList();
        if (kid != null && !match.Any() && _time.GetUtcNow() - _lastAttempt > TimeSpan.FromMinutes(1) && _wake.CurrentCount == 0)
            try { _wake.Release(); } catch (SemaphoreFullException) { } // unknown kid: refresh in background
        return match;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        await _refreshGate.WaitAsync(ct);
        try
        {
            _lastAttempt = _time.GetUtcNow();
            var json = await _http.GetStringAsync(_url, ct);
            var keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            if (keys.Count == 0) { _log.LogError("JWKS at {Url} contains no signing keys", _url); return; }
            _keys = keys;
            LastRefreshed = _time.GetUtcNow();
            _log.LogInformation("JWKS refreshed: {Count} signing key(s)", keys.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("JWKS refresh failed ({Error}); keeping {Count} previous key(s)", ex.GetType().Name, _keys.Count);
        }
        finally { _refreshGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(_keys.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { return; }
            await RefreshAsync(ct);
        }
    }
}
