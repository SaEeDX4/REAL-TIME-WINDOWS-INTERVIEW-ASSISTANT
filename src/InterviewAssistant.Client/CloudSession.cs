using InterviewAssistant.Contracts;

namespace InterviewAssistant.Client;

/// <summary>
/// Client side of a server lease: heartbeats, the live transcription relay endpoint and graceful end. Audio goes
/// through the service's relay (authenticated with the user's own access token); the client never holds a provider
/// credential, and the relay closes as soon as the lease ends.
/// </summary>
public sealed class CloudSession : IAsyncDisposable
{
    private readonly BackendClient _api;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _seq;
    private RealtimeCredential _cred;
    private int _consecutiveFailures;

    public Guid Id { get; }
    public int HeartbeatIntervalSeconds { get; }
    public int RemainingSeconds { get; private set; }
    /// <summary>Plan allowance left in the billing period (-1 if the server did not say).</summary>
    public int PeriodRemainingSeconds { get; private set; } = -1;
    /// <summary>Live time still available today under the daily safety cap (-1 if unknown).</summary>
    public int TodayRemainingSeconds { get; private set; } = -1;
    /// <summary>Interview language passed on every secret renewal so reconnects keep the same recognition settings.</summary>
    public string? Language { get; }
    public Guid? TargetId { get; }
    public bool Active { get; private set; } = true;
    public string? EndReason { get; private set; }
    public RealtimeCredential Credential => _cred;
    /// <summary>Absolute address of this lease's transcription relay (with the interview language).</summary>
    public Uri RelayEndpoint => _api.WebSocketUri(_cred.WebSocketUrl, Language);
    /// <summary>Handshake headers for the relay connection.</summary>
    public IReadOnlyDictionary<string, string> RelayHeaders => new Dictionary<string, string> { ["X-Client-Version"] = _api.ClientVersion };

    /// <summary>Raised once when the server ends the lease (allowance, max duration, revoked) or it is stopped.</summary>
    public event Action<string>? Ended;
    public event Action<int>? RemainingChanged;
    public event Action<string>? Warning;

    private CloudSession(BackendClient api, StartSessionResponse r, TimeProvider time, string? language, Guid? targetId)
    {
        _api = api; _time = time; Id = r.SessionId; _cred = r.Realtime; Language = language; TargetId = targetId;
        HeartbeatIntervalSeconds = Math.Clamp(r.HeartbeatIntervalSeconds, 5, 120);
        RemainingSeconds = r.RemainingSeconds;
        PeriodRemainingSeconds = r.PeriodRemainingSeconds;
        TodayRemainingSeconds = r.TodayRemainingSeconds;
    }

    public static async Task<CloudSession> StartAsync(BackendClient api, Guid deviceId, Guid? targetId, string? language, CancellationToken ct, TimeProvider? time = null, bool runLoop = true)
    {
        var key = Guid.NewGuid().ToString("n");   // idempotency: the same key is reused across retries of this start
        StartSessionResponse? r = null;
        for (int attempt = 0; r == null; attempt++)
        {
            try { r = await api.StartSessionAsync(new StartSessionRequest(deviceId, targetId, key, language, Relay: true), ct); }
            catch (BackendException ex) when (ex.Status is 0 or 502 or 503 && ex.Code != "maintenance" && attempt < 2) { await Task.Delay(500 * (attempt + 1), ct); }
        }
        var s = new CloudSession(api, r, time ?? TimeProvider.System, language, targetId);
        if (runLoop) s._loop = s.LoopAsync(s._cts.Token);
        return s;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (Active && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), _time, ct);
                await HeartbeatOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Bearer for the next relay connection: the user's access token while the lease is active, null once it ended
    /// (the transcriber then stops instead of reconnecting). The relay itself re-checks the lease on every connection.
    /// </summary>
    public async Task<string?> NextConnectionCredentialAsync(CancellationToken ct) => Active ? await _api.AccessTokenAsync(ct) : null;

    /// <summary>One heartbeat (metering + lease state). Public for deterministic tests.</summary>
    public async Task HeartbeatOnceAsync(CancellationToken ct)
    {
        if (!Active) return;
        try
        {
            var hb = await _api.HeartbeatAsync(Id, ++_seq, ct);
            _consecutiveFailures = 0;
            RemainingSeconds = hb.RemainingSeconds;
            if (hb.PeriodRemainingSeconds >= 0) PeriodRemainingSeconds = hb.PeriodRemainingSeconds;
            if (hb.TodayRemainingSeconds >= 0) TodayRemainingSeconds = hb.TodayRemainingSeconds;
            RemainingChanged?.Invoke(hb.RemainingSeconds);
            if (!hb.Active) { End(hb.EndReason ?? "ended"); return; }
            if (hb.RemainingSeconds is > 0 and <= 300) Warning?.Invoke(Core.Languages.Loc.F("svc_minutes_left_warning", hb.RemainingSeconds / 60 + 1));
        }
        catch (BackendException ex) when (ex.Status is 404 or 409 or 403) { End(ex.Code); }
        catch (BackendException)
        {
            // Network blips: the server bills at most 2 intervals per heartbeat and expires the lease after 90 s.
            if (++_consecutiveFailures >= 3) Warning?.Invoke(Core.Languages.Loc.T("svc_unstable"));
        }
    }

    private void End(string reason)
    {
        if (!Active) return;
        Active = false;
        EndReason = reason;
        Ended?.Invoke(reason);
    }

    public async Task StopAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (Active)
        {
            try { using var t = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await _api.StopSessionAsync(Id, t.Token); }
            catch (Exception ex) when (ex is BackendException or OperationCanceledException) { /* server sweeper expires it */ }
            End("stopped");
        }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _cts.Dispose(); }
}
