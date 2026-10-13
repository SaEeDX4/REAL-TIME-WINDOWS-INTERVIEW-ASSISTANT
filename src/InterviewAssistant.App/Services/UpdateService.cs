using System.Net.Http;
using InterviewAssistant.Client;
using Velopack;
using Velopack.Sources;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Velopack updates (stable/beta channel). Enabled only when the running exe carries a valid Authenticode signature
/// and an https feed is configured; every downloaded package must be signed by the same publisher before it is
/// scheduled, otherwise it is discarded. Unsigned builds never self-update. Never checks, downloads or applies during a live session
/// (<see cref="UpdatePolicy"/>). A downloaded update installs when the app exits, or on "Restart to update" while idle.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _mgr;
    private readonly AppSettings _settings;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private DateTimeOffset? _lastCheck;
    private UpdateInfo? _pending;

    public string Status { get; private set; } = "";
    public bool IsInstalled => _mgr?.IsInstalled == true;
    public bool UpdateReady => _pending != null;
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();
    public string Channel => UpdatePolicy.NormalizeChannel(_settings.UpdateChannel);
    public event Action? Changed;

    private readonly string? _signer;

    public UpdateService(AppSettings settings, string feedUrl)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(feedUrl)) { Status = "Automatic updates are not enabled for this build."; return; }
        if (!feedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { Status = "Automatic updates are disabled: the update feed must use https."; return; }
        _signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        if (_signer == null)
        {
            Status = "Automatic updates are disabled: this build is not code-signed.";
            AppLog.Info("Updates disabled: running executable has no valid Authenticode signature");
            return;
        }
        _mgr = Create(feedUrl, Channel);
        if (!_mgr.IsInstalled) Status = "Updates apply to installed copies only (this copy runs from a folder).";
    }

    /// <summary>The downloaded package must be signed by the same publisher as the running app.</summary>
    private bool PackageTrusted(UpdateInfo info)
    {
        var installRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (installRoot == null || _signer == null) return false;
        var pkg = Path.Combine(installRoot, "packages", info.TargetFullRelease.FileName);
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "InterviewAssistant.exe");
        var signer = Authenticode.TrustedSignerInPackage(pkg, exeName);
        if (signer == _signer) return true;
        AppLog.Warn("Update package rejected: " + (signer == null ? "missing or invalid signature" : "different publisher"));
        try { File.Delete(pkg); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return false;
    }

    private static UpdateManager Create(string feed, string channel)
    {
        var options = new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = false };
        IUpdateSource source = feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed, null, prerelease: channel == "beta")
            : feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? new SimpleWebSource(feed)
#if UPDATE_TEST
            : new SimpleFileSource(new DirectoryInfo(feed));   // local folder: CI update-test builds only
#else
            : throw new InvalidOperationException("Update feed must use https.");
#endif
        return new UpdateManager(source, options);
    }

    /// <summary>Background tick (call periodically). Respects the live-session rule.</summary>
    public async Task TickAsync(bool sessionActive)
    {
        if (_mgr is not { IsInstalled: true } || _pending != null) return;
        if (!UpdatePolicy.ShouldCheck(sessionActive, _lastCheck, DateTimeOffset.UtcNow, _started)) return;
        _lastCheck = DateTimeOffset.UtcNow;
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info == null) { Status = $"Up to date ({_mgr.CurrentVersion}, {Channel})."; Changed?.Invoke(); return; }
            await _mgr.DownloadUpdatesAsync(info);
            if (!PackageTrusted(info)) { Status = "An update was found but its signature could not be verified; it was not installed."; Changed?.Invoke(); return; }
            _pending = info;
            Status = $"Version {PendingVersion} is ready — it installs when you close the app.";
            AppLog.Info("Update downloaded: " + PendingVersion);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Status = "Update check failed; will retry later.";
            AppLog.Warn("Update check failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Called during shutdown: hands the downloaded update to the updater, which installs after this process exits.</summary>
    public void ApplyOnExit()
    {
        if (_mgr == null || _pending == null) return;
        try { _mgr.WaitExitThenApplyUpdates(_pending, silent: true, restart: false); AppLog.Info("Update will install after exit"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { AppLog.Warn("Could not schedule update: " + ex.Message); }
    }

    /// <summary>User-initiated "Restart to update" — refused while a session is active.</summary>
    public bool TryApplyAndRestart(bool sessionActive)
    {
        if (_mgr == null || !UpdatePolicy.CanApplyNow(sessionActive, _pending != null)) return false;
        _mgr.ApplyUpdatesAndRestart(_pending!);
        return true;
    }

#if UPDATE_TEST
    /// <summary>
    /// CI N→N+1 test (compiled only into update-test builds, which are unsigned and never shipped): `InterviewAssistant.exe --update-test &lt;feedDir&gt; &lt;result.json&gt;` on an installed copy.
    /// Checks the local feed, downloads, schedules the install for after exit, and records what happened.
    /// </summary>
    public static async Task<int> RunUpdateTestAsync(string feedDir, string resultPath)
    {
        string result;
        int code;
        try
        {
            var mgr = Create(feedDir, "stable");
            if (!mgr.IsInstalled) { result = "not-installed"; code = 2; }
            else
            {
                var info = await mgr.CheckForUpdatesAsync();
                if (info == null) { result = $"no-update (current {mgr.CurrentVersion})"; code = 3; }
                else
                {
                    await mgr.DownloadUpdatesAsync(info);
                    mgr.WaitExitThenApplyUpdates(info, silent: true, restart: false);
                    result = $"scheduled {mgr.CurrentVersion} -> {info.TargetFullRelease.Version}";
                    code = 0;
                }
            }
        }
        catch (Exception ex) { result = "error: " + ex.GetType().Name + ": " + ex.Message; code = 1; }
        await File.WriteAllTextAsync(resultPath, result);
        AppLog.Info("UPDATE TEST " + result);
        return code;
    }
#endif
}
