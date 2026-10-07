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

    public Guid Id { get; }
    public int HeartbeatIntervalSeconds { get; }
    public int RemainingSeconds { get; private set; }
    public bool Active { get; private set; } = true;
    public string? EndReason { get; private set; }
    public string CurrentSecret => _cred.ClientSecret;
    public RealtimeCredential Credential => _cred;

    /// <summary>Raised once when the server ends the lease (allowance, max duration, revoked) or it is stopped.</summary>
    public event Action<string>? Ended;
    public event Action<int>? RemainingChanged;
    public event Action<string>? Warning;

    private CloudSession(BackendClient api, StartSessionResponse r, TimeProvider time)
    {
        _api = api; _time = time; Id = r.SessionId; _cred = r.Realtime;
        HeartbeatIntervalSeconds = Math.Clamp(r.HeartbeatIntervalSeconds, 5, 120);
        RemainingSeconds = r.RemainingSeconds;
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
        var s = new CloudSession(api, r, time ?? TimeProvider.System);
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

    /// <summary>One heartbeat + credential renewal if it expires within 2 minutes. Public for deterministic tests.</summary>
    public async Task HeartbeatOnceAsync(CancellationToken ct)
    {
        if (!Active) return;
        try
        {
            var hb = await _api.HeartbeatAsync(Id, ++_seq, ct);
            _consecutiveFailures = 0;
            RemainingSeconds = hb.RemainingSeconds;
            RemainingChanged?.Invoke(hb.RemainingSeconds);
            if (!hb.Active) { End(hb.EndReason ?? "ended"); return; }
            if (hb.RemainingSeconds is > 0 and <= 300) Warning?.Invoke($"{hb.RemainingSeconds / 60 + 1} live minutes left.");
            if (_cred.ExpiresAtUtc - _time.GetUtcNow().UtcDateTime < TimeSpan.FromMinutes(2))
                _cred = await _api.RenewRealtimeSecretAsync(Id, ct);
        }
        catch (BackendException ex) when (ex.Status is 404 or 409 or 403) { End(ex.Code); }
        catch (BackendException ex)
        {
            // Network blips: the server bills at most 2 intervals per heartbeat and expires the lease after 90 s.
            if (++_consecutiveFailures >= 3) Warning?.Invoke("Connection to the service is unstable (" + ex.Code + ").");
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
