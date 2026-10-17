namespace InterviewAssistant.App.UiState;

/// <summary>The four rows of the in-app health check (Help &amp; about → Diagnostics → Run self-test).</summary>
public enum HealthCheckId { AudioDevice, Service, Settings, Disk }

public enum HealthOutcome { Pass, Warn }

/// <summary>What the answer service looked like when the check ran.</summary>
public enum ServiceReach { Reachable, Unreachable, DeveloperMode, NotConfigured }

/// <summary>Measured facts (gathered by the Help page; no WPF types here so the evaluation is unit-tested).</summary>
public sealed record HealthFacts(int PlaybackDevices, ServiceReach Service, bool SettingsReadable, bool DiskWritable);

/// <summary>One row: outcome plus the localization keys of its label and plain-language detail.</summary>
public sealed record HealthRow(HealthCheckId Id, HealthOutcome Outcome, string LabelKey, string DetailKey);

/// <summary>
/// A quick, friendly health check — not the CI self-test (--selftest). Every row is Pass or Warn (never a scary
/// "fail"): the app keeps working with defaults, typed questions or after reconnecting, and the detail says what to do.
/// </summary>
public static class HealthCheck
{
    public static IReadOnlyList<HealthRow> Evaluate(HealthFacts f) => new[]
    {
        f.PlaybackDevices > 0
            ? new HealthRow(HealthCheckId.AudioDevice, HealthOutcome.Pass, "help_diag_audio", "hc_audio_ok")
            : new HealthRow(HealthCheckId.AudioDevice, HealthOutcome.Warn, "help_diag_audio", "hc_audio_warn"),
        f.Service switch
        {
            ServiceReach.Reachable => new HealthRow(HealthCheckId.Service, HealthOutcome.Pass, "hc_service", "hc_service_ok"),
            ServiceReach.DeveloperMode => new HealthRow(HealthCheckId.Service, HealthOutcome.Pass, "hc_service", "hc_service_dev"),
            ServiceReach.NotConfigured => new HealthRow(HealthCheckId.Service, HealthOutcome.Warn, "hc_service", "acct_not_configured_long"),
            _ => new HealthRow(HealthCheckId.Service, HealthOutcome.Warn, "hc_service", "hc_service_warn"),
        },
        f.SettingsReadable
            ? new HealthRow(HealthCheckId.Settings, HealthOutcome.Pass, "settings", "hc_settings_ok")
            : new HealthRow(HealthCheckId.Settings, HealthOutcome.Warn, "settings", "hc_settings_warn"),
        f.DiskWritable
            ? new HealthRow(HealthCheckId.Disk, HealthOutcome.Pass, "hc_disk", "hc_disk_ok")
            : new HealthRow(HealthCheckId.Disk, HealthOutcome.Warn, "hc_disk", "hc_disk_warn"),
    };

    public static int Passed(IEnumerable<HealthRow> rows) => rows.Count(r => r.Outcome == HealthOutcome.Pass);

    /// <summary>Friendly "last answer time": seconds from the end of speech (or of a typed question) to the first bullet; null without a sample.</summary>
    public static double? AnswerSeconds(long speechEndToFirstBulletMs, long finalizedToFirstBulletMs)
    {
        var ms = speechEndToFirstBulletMs > 0 ? speechEndToFirstBulletMs : finalizedToFirstBulletMs;
        return ms > 0 ? Math.Round(ms / 1000.0, 1) : null;
    }
}
