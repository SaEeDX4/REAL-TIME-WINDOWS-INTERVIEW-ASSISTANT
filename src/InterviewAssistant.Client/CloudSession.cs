using InterviewAssistant.Contracts;

namespace InterviewAssistant.Client;

/// <summary>
/// Client side of a server lease: heartbeats, ephemeral transcription credential renewal and graceful end.
/// The transcriber reads <see cref="CurrentSecret"/> on every (re)connect.
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
    private bool _startSecretUsed;
    private readonly SemaphoreSlim _secretGate = new(1, 1);

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
    public string CurrentSecret => _cred.ClientSecret;
    public RealtimeCredential Credential => _cred;

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
            try { r = await api.StartSessionAsync(new StartSessionRequest(deviceId, targetId, key, language), ct); }
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
    /// Credential for the next transcription connection: the secret from session start once, then a freshly minted one
    /// for every reconnect (ephemeral secrets are single-purpose and short-lived). Null when the lease has ended.
    /// </summary>
    public async Task<string?> NextConnectionSecretAsync(CancellationToken ct)
    {
        if (!Active) return null;
        await _secretGate.WaitAsync(ct);
        try
        {
            if (!_startSecretUsed && _cred.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime > TimeSpan.FromSeconds(30)) { _startSecretUsed = true; return _cred.ClientSecret; }
            _startSecretUsed = true;
            try { _cred = await _api.RenewRealtimeSecretAsync(Id, ct, Language); return _cred.ClientSecret; }
            catch (BackendException ex) when (ex.Status is 404 or 409 or 402 or 403) { End(ex.Code); return null; }
        }
        finally { _secretGate.Release(); }
    }

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
