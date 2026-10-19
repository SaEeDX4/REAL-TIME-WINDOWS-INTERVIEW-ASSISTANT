namespace InterviewAssistant.App.UiState;

/// <summary>
/// The one password reset whose e-mailed link this PC is waiting for (up to 15 minutes). Each request gets a
/// <see cref="Ticket"/>; a newer request, leaving the reset screen, a sign-in, a sign-out, a switch or closing the window
/// cancels the current one. Only the CURRENT ticket's task may change the form (clear Busy, reset the state, show its
/// result), so a stale reset can never interfere with a later sign-in. Plain class (no WPF) so it is unit-tested.
/// </summary>
public sealed class PendingReset
{
    /// <summary>One reset request: its cancellation and the generation it belongs to.</summary>
    public sealed class Ticket : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        internal Ticket(int generation, string email)
        {
            Generation = generation;
            Email = email;
            Token = _cts.Token;
        }

        public int Generation { get; }
        public string Email { get; }
        /// <summary>Cancelled when the ticket is replaced or the reset screen is left.</summary>
        public CancellationToken Token { get; }

        internal void Cancel()
        {
            if (!_disposed) _cts.Cancel();
        }

        /// <summary>Called by the task that owns the ticket once it has finished.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Dispose();
        }
    }

    private Ticket? _current;
    private int _generation;
    private readonly Dictionary<string, DateTimeOffset> _requestedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A reset link is being awaited.</summary>
    public bool IsPending => _current != null;

    /// <summary>The generation of the latest request (increases with every request and every cancellation).</summary>
    public int Generation => _generation;

    /// <summary>A link for this address is being awaited (asking again sends nothing new).</summary>
    public bool IsPendingFor(string email) => _current is { } t && string.Equals(t.Email, email, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts a new request; any earlier one is cancelled.</summary>
    public Ticket Begin(string email)
    {
        Cancel();
        _current = new Ticket(++_generation, email);
        return _current;
    }

    /// <summary>True while <paramref name="ticket"/> is the current, uncancelled request: only then may its task touch the form.</summary>
    public bool IsCurrent(Ticket ticket) =>
        ReferenceEquals(_current, ticket) && ticket.Generation == _generation && !ticket.Token.IsCancellationRequested;

    /// <summary>
    /// The ticket's task has finished: it stops being current WITHOUT being cancelled. Returns true when it was still
    /// current (the task may then clear Busy); false for a stale ticket, which must leave the form alone.
    /// </summary>
    public bool Release(Ticket ticket)
    {
        if (!IsCurrent(ticket)) return false;
        _current = null;
        return true;
    }

    /// <summary>Cancels the awaited reset (if any). Returns true when one was cancelled.</summary>
    public bool Cancel()
    {
        var t = _current;
        if (t == null) return false;
        _current = null;
        _generation++;
        t.Cancel();
        return true;
    }

    /// <summary>Records that the auth server accepted a request for this address (its per-address interval starts).</summary>
    public void MarkRequested(string email, DateTimeOffset now) => _requestedAt[email.Trim()] = now;

    /// <summary>
    /// How long a new request for <paramref name="email"/> must wait so the auth server sends a new e-mail rather than
    /// silently rate-limiting it (which is reported as accepted): the earlier link stops working once its wait is
    /// cancelled, so sending again too early would leave no working link.
    /// </summary>
    public TimeSpan WaitBeforeSending(string email, DateTimeOffset now, TimeSpan interval) =>
        _requestedAt.TryGetValue(email.Trim(), out var at) && now - at < interval ? interval - (now - at) : TimeSpan.Zero;
}
