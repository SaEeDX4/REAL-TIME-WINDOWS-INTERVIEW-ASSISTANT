namespace InterviewAssistant.Client;

/// <summary>
/// When the desktop app may check for, download and apply updates. Rule: nothing update-related happens while a live
/// session is running or paused (no bandwidth, CPU or restart risk mid-interview); applying happens on exit or when the
/// user explicitly restarts while idle.
/// </summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    public static bool ShouldCheck(bool sessionActive, DateTimeOffset? lastCheck, DateTimeOffset now, DateTimeOffset appStarted) =>
        !sessionActive && now - appStarted >= StartupDelay && (lastCheck == null || now - lastCheck.Value >= CheckInterval);

    public static bool CanApplyNow(bool sessionActive, bool updateDownloaded) => updateDownloaded && !sessionActive;

    /// <summary>"stable" or "beta"; anything else is treated as stable.</summary>
    public static string NormalizeChannel(string? channel) => string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";
}
