namespace InterviewAssistant.App.UiState;

public enum ConnectionToast { None, Lost, Restored }

/// <summary>
/// Decides when the "Connection lost — reconnecting…" / "Connection restored" notifications appear, so that network
/// blips never turn into notification noise:
/// <list type="bullet">
/// <item>"lost" only after the connection has stayed down for <see cref="Grace"/> (a blip under ~3 s shows nothing);</item>
/// <item>"restored" only after a "lost" was actually shown (never a lone "restored");</item>
/// <item>at most one lost/restored pair per <see cref="MinInterval"/>: a new outage soon after the previous "lost"
/// waits until that interval has passed, and shows nothing if it is over by then.</item>
/// </list>
/// The owner reports every state change with <see cref="Report"/> and calls <see cref="Poll"/> periodically while
/// <see cref="NeedsPolling"/> is true (the delayed "lost" is decided there). Time comes from an injected clock.
/// </summary>
public sealed class ConnectionToastGate
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _downSince;
    private DateTimeOffset? _lastLostShown;
    private bool _lostShown;

    public ConnectionToastGate(Func<DateTimeOffset> now, TimeSpan? grace = null, TimeSpan? minInterval = null)
    {
        _now = now;
        Grace = grace ?? DefaultGrace;
        MinInterval = minInterval ?? DefaultMinInterval;
    }

    public TimeSpan Grace { get; }
    public TimeSpan MinInterval { get; }
    public bool IsConnected { get; private set; } = true;
    /// <summary>A "lost" notification is pending (down, not shown yet): call <see cref="Poll"/> again soon.</summary>
    public bool NeedsPolling => !IsConnected && !_lostShown;

    /// <summary>
    /// The connection state changed (or was re-confirmed). <paramref name="silent"/>: the connection no longer matters
    /// (interview stopped, signed out) — forget the outage without announcing a recovery that did not happen.
    /// </summary>
    public ConnectionToast Report(bool connected, bool silent = false)
    {
        if (connected == IsConnected) return Poll();
        IsConnected = connected;
        if (!connected) { _downSince = _now(); return Poll(); }
        _downSince = null;
        var wasShown = _lostShown;
        _lostShown = false;
        return wasShown && !silent ? ConnectionToast.Restored : ConnectionToast.None;
    }

    /// <summary>Shows "lost" once the outage has lasted the grace period (and the rate limit allows it).</summary>
    public ConnectionToast Poll()
    {
        if (IsConnected || _lostShown || _downSince is not { } since) return ConnectionToast.None;
        var now = _now();
        if (now - since < Grace) return ConnectionToast.None;
        if (_lastLostShown is { } last && now - last < MinInterval) return ConnectionToast.None;
        _lostShown = true;
        _lastLostShown = now;
        return ConnectionToast.Lost;
    }
}

/// <summary>Maps the live relay's own connection state onto the toast gate (not the status line, which answers overwrite).</summary>
public static class RelayConnection
{
    /// <summary>
    /// New up/down state for a transcriber event, or null when nothing changes. Reconnecting = down; Connected = up
    /// (may show "restored"); Failed/Disconnected while down = the session ended: up again, silently (never "restored").
    /// </summary>
    public static (bool Up, bool Silent)? Transition(bool currentlyUp, Core.Providers.ConnectionStatus status) => status switch
    {
        Core.Providers.ConnectionStatus.Reconnecting when currentlyUp => (false, false),
        Core.Providers.ConnectionStatus.Connected when !currentlyUp => (true, false),
        Core.Providers.ConnectionStatus.Failed or Core.Providers.ConnectionStatus.Disconnected when !currentlyUp => (true, true),
        _ => null,
    };
}
