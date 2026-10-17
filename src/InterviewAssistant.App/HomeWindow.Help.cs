using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Views;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App;

/// <summary>
/// Help &amp; about → Diagnostics (rc.5; the Live window no longer has a diagnostics panel): a friendly summary
/// (connection, audio device, last answer time, questions answered — never raw errors, tokens or interview text),
/// Copy diagnostics (support line + the technical lines, English on purpose), Open log folder, and Run self-test: a
/// quick in-app health check shown as pass / warn rows (not the CI --selftest).
/// </summary>
public partial class HomeWindow
{
    private DispatcherTimer? _diagTimer;
    private IReadOnlyList<HealthRow>? _healthRows;
    private bool _healthRunning;

    private void PageHelp_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PageHelp.IsVisible)
        {
            RefreshHelpDiagnostics();
            _diagTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshHelpDiagnostics(), Dispatcher);
            _diagTimer.Start();
        }
        else _diagTimer?.Stop();
    }

    /// <summary>Re-renders the summary and the last health-check rows (also follows a UI language switch).</summary>
    internal void RefreshHelpDiagnostics()
    {
        HelpDiagConnection.Text = ConnectionText();
        var device = _vm.CurrentDeviceName is { Length: > 0 } d ? d : T("audio_system_default");
        HelpDiagAudio.Text = $"{device} · {_vm.AudioStatus}";
        var metrics = _vm.Engine?.Metrics;
        var last = metrics?.Samples.LastOrDefault();
        var seconds = last == null ? null : HealthCheck.AnswerSeconds(last.SpeechEndToFirstBulletMs, last.FinalizedToFirstBulletMs);
        HelpDiagLatency.Text = seconds is double s ? F("help_diag_latency_value", s.ToString("0.0", Loc.Culture)) : T("help_diag_none");
        HelpDiagQuestions.Text = (metrics?.Questions ?? 0).ToString(Loc.Culture);
        RenderHealthRows();
    }

    /// <summary>Plain connection state (the account page has the details; nothing technical here).</summary>
    private string ConnectionText()
    {
        if (!_vm.UsingCloud) return T("hc_service_dev");
        return _vm.Cloud.State switch
        {
            CloudState.SignedIn => T("help_diag_conn_ok"),
            CloudState.Connecting => T("acct_connecting"),
            CloudState.ServiceUnavailable => T("acct_unavailable"),
            CloudState.NotConfigured => T("acct_not_configured"),
            CloudState.UpdateRequired => T("acct_update_required"),
            CloudState.SessionExpired => T("acct_session_expired"),
            CloudState.AccountDisabled => T("acct_disabled"),
            _ => T("acct_signed_out"),
        };
    }

    /// <summary>One line for support: product, version, channel, build, install type, signing, OS (English, no personal data).</summary>
    private static string SupportLine()
    {
        // Support text stays in English on purpose (read by the support team, not shown in the UI).
        var signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;
        return $"{Branding.ProductName} {BuildInfo.Version} ({BuildInfo.Channel}) build {BuildInfo.Commit} · {(BuildInfo.IsInstalled ? "installed" : "portable")} · {(signer != null ? "signed" : "unsigned")} · {Environment.OSVersion}";
    }

    /// <summary>The text Copy diagnostics puts on the clipboard (self-test checks it carries no credentials).</summary>
    internal string DiagnosticsText()
    {
        _vm.RefreshDiagnostics();
        var sb = new StringBuilder();
        sb.AppendLine(SupportLine());
        sb.AppendLine($"Service: {(_vm.UsingCloud ? _vm.Cloud.State.ToString() : "developer mode")} · audio {_vm.Capture.Status}");
        if (_healthRows != null) sb.AppendLine("Health check: " + string.Join(", ", _healthRows.Select(r => $"{r.Id} {r.Outcome}")));
        if (_vm.Diagnostics.Length > 0) sb.AppendLine(_vm.Diagnostics);
        return sb.ToString().TrimEnd();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText());
            Notify(T("toast_copied"), ToastKind.Success);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            AppLog.Warn($"Clipboard busy: {ex.GetType().Name}");
            Notify(T("err_diag_copy"), ToastKind.Error);
        }
    }

    private async void RunHealthCheck_Click(object sender, RoutedEventArgs e) => await RunHealthCheckAsync();

    /// <summary>Quick in-app health check: audio device present, service reachable, settings readable, disk writable.</summary>
    internal async Task<IReadOnlyList<HealthRow>> RunHealthCheckAsync()
    {
        if (_healthRunning && _healthRows != null) return _healthRows;
        _healthRunning = true;
        HelpDiagRunBtn.IsEnabled = false;
        HelpDiagRunStatus.Text = T("help_diag_running");
        HelpDiagRunStatus.Visibility = Visibility.Visible;
        try
        {
            var devices = await Task.Run(() => Math.Max(0, AudioCaptureService.ListDevices().Count - 1));   // first entry = "system default"
            var facts = new HealthFacts(devices, await ServiceReachAsync(), await Task.Run(SettingsReadable), await Task.Run(DiskWritable));
            _healthRows = HealthCheck.Evaluate(facts);
            AppLog.Info("Health check: " + string.Join(", ", _healthRows.Select(r => $"{r.Id} {r.Outcome}")));
            RenderHealthRows();
            return _healthRows;
        }
        finally
        {
            _healthRunning = false;
            HelpDiagRunBtn.IsEnabled = true;
        }
    }

    private async Task<ServiceReach> ServiceReachAsync()
    {
        if (!_vm.UsingCloud) return ServiceReach.DeveloperMode;
        if (!_vm.Cloud.IsConfigured) return ServiceReach.NotConfigured;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ok = await _vm.Cloud.RefreshAsync(cts.Token);
        return ok || _vm.Cloud.State is not (CloudState.ServiceUnavailable or CloudState.Connecting) ? ServiceReach.Reachable : ServiceReach.Unreachable;
    }

    private static bool SettingsReadable()
    {
        try
        {
            var path = AppSettings.CurrentPath;
            if (!File.Exists(path)) return true;   // nothing saved yet: defaults are the settings
            using var _ = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Health check: settings unreadable ({ex.GetType().Name})");
            return false;
        }
    }

    private static bool DiskWritable()
    {
        var probe = Path.Combine(AppPaths.Local, $"health-{Guid.NewGuid():n}.tmp");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Health check: app data folder not writable ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>Pass / warn rows: icon, label, plain detail (the outcome is also spoken by screen readers).</summary>
    private void RenderHealthRows()
    {
        HelpDiagRows.Children.Clear();
        if (_healthRows == null) return;
        if (!_healthRunning) HelpDiagRunStatus.Text = F("help_diag_result", HealthCheck.Passed(_healthRows), _healthRows.Count);
        foreach (var r in _healthRows)
        {
            var pass = r.Outcome == HealthOutcome.Pass;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var icon = new TextBlock { Text = pass ? "" : "", FontSize = 13, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.SetResourceReference(StyleProperty, "T.Icon");
            icon.SetResourceReference(TextBlock.ForegroundProperty, pass ? "B.Listening" : "B.Thinking");
            DockPanel.SetDock(icon, Dock.Left);
            var text = new StackPanel();
            var label = new TextBlock { Text = T(r.LabelKey), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
            var detail = new TextBlock { Text = T(r.DetailKey) };
            detail.SetResourceReference(StyleProperty, "T.Body");
            text.Children.Add(label); text.Children.Add(detail);
            row.Children.Add(icon); row.Children.Add(text);
            AutomationProperties.SetName(row, $"{T(r.LabelKey)}: {T(pass ? "hc_pass" : "hc_warn")} — {T(r.DetailKey)}");
            HelpDiagRows.Children.Add(row);
        }
    }

    // ----- self-test hooks -----
    internal IEnumerable<string> HelpDiagnosticsTexts =>
        new[] { HelpDiagConnection.Text, HelpDiagAudio.Text, HelpDiagLatency.Text, HelpDiagQuestions.Text }
            .Concat(HelpDiagRows.Children.OfType<FrameworkElement>().Select(AutomationProperties.GetName));
}
