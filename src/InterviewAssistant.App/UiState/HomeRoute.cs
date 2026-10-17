namespace InterviewAssistant.App.UiState;

/// <summary>
/// A deep link into the Home window: a page and an optional section ("settings/audio", "preinterview", "account").
/// Resolved BEFORE the window shows or switches page, so the target appears directly — never another page first.
/// </summary>
public sealed record HomeRoute(string Page, string? Section = null)
{
    /// <summary>Pages of the Home window (their navigation tags).</summary>
    public static readonly IReadOnlyList<string> Pages = new[] { "start", "profiles", "interviews", "reports", "preinterview", "account", "settings", "help" };

    public static HomeRoute Start { get; } = new("start");

    /// <summary>"settings/audio" → (settings, audio). Unknown pages fall back to Get started; unknown sections are dropped by the page.</summary>
    public static HomeRoute Parse(string? route)
    {
        var parts = (route ?? "").Trim().Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return Start;
        var page = parts[0].ToLowerInvariant();
        if (!Pages.Contains(page)) return Start;
        return new HomeRoute(page, parts.Length > 1 && parts[1].Length > 0 ? parts[1].ToLowerInvariant() : null);
    }

    /// <summary>
    /// The page actually shown: signed out (account service configured), the user's data pages lead to Account.
    /// </summary>
    public HomeRoute Gate(bool signedOutGate) =>
        signedOutGate && Page is "profiles" or "interviews" or "reports" ? new HomeRoute("account") : this;

    public override string ToString() => Section == null ? Page : $"{Page}/{Section}";
}
