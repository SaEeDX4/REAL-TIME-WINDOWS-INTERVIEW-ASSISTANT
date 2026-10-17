using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.Views;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Preparation;
using InterviewAssistant.Core.Reports;
using Microsoft.Win32;

namespace InterviewAssistant.App;

/// <summary>Home → Account: sign-in, sign-up and password reset, plan and usage, devices, security, privacy and account deletion.</summary>
public partial class HomeWindow
{
    private bool _createMode;
    private bool _authBusy;
    /// <summary>State of the signed-out form (sign in, create account, forgot password, reset sent, switch account).</summary>
    private readonly AuthFormState _auth = new();

    private void RefreshAccount()
    {
        var c = _vm.Cloud;
        RefreshAccountMenu();
        NotConfiguredPanel.Visibility = c.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        SignedOutPanel.Visibility = c.IsConfigured && !c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = c.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        // Page level: only the account-wide state. A request that failed (refresh, devices, billing) is reported in its
        // own section, next to the data it concerns — never as a page-wide error over data that is fine.
        var email = c.Account?.Email ?? c.Auth?.Session?.Email;
        AccountStatus.Text = c.IsSignedIn && c.State is CloudState.SignedIn or CloudState.ServiceUnavailable or CloudState.Connecting && email is { Length: > 0 }
            ? F("acct_signed_in_email", email)
            : c.State == CloudState.Connecting ? T("acct_connecting_service") : Loc.Relocalize(c.Message) ?? "";
        AccountStatus.Foreground = (Brush)FindResource(c.State is CloudState.AccountDisabled or CloudState.UpdateRequired or CloudState.SessionExpired
            || c.State == CloudState.ServiceUnavailable && !c.IsSignedIn ? "B.Thinking" : "B.TextDim");
        SetCreateMode(_createMode);

        // Sign-in options come from the service (publishable configuration only).
        ForgotBtn.Visibility = c.Options.EmailPassword ? Visibility.Visible : Visibility.Collapsed;
        ProviderButtons.Children.Clear();
        foreach (var p in c.Options.OAuthProviders)
        {
            var b = new Button { Content = F("continue_with", ProviderName(p)), Style = (Style)FindResource("Btn.Form"), Margin = (Thickness)FindResource("S.ButtonGap"), Tag = p, IsEnabled = !_authBusy };
            b.Click += async (_, _) => await RunAuth(() => _vm.Cloud.SignInWithProviderAsync(p), T("acct_continue_in_browser"));
            ProviderButtons.Children.Add(b);
        }
        SignUpLegal.Text = LegalSentence();

        SecurityEmail.Text = c.IsSignedIn ? email ?? "" : "";
        if (c.Account is { } a && c.IsSignedIn)
        {
            // Plan name + state badge, then the metric cards (minutes, today, preparations, limits) with usage bars.
            PlanText.Text = a.Entitlements.PlanName;
            var label = MainViewModel.BillingLabel(a);
            PlanSubText.Text = label != a.Entitlements.PlanName ? label : "";
            ShowPlanBadge(a.BillingState);
            UsageMetrics.ItemsSource = PlanView.Metrics(a, d => d.ToLocalTime().ToString("d", Loc.Culture)).Select(MetricRow.From).ToList();
            ShowDevices(_devices);   // row texts follow the UI language
            BillingNotice.Text = a.BillingState switch
            {
                BillingStates.PastDue => F("bill_notice_past_due", a.Subscription?.GraceUntilUtc?.ToLocalTime().ToString("d", Loc.Culture)),
                BillingStates.Canceling => F("bill_notice_canceling", a.Subscription?.CurrentPeriodEndUtc?.ToLocalTime().ToString("d", Loc.Culture)),
                BillingStates.Paused => T("bill_notice_paused"),
                BillingStates.Expired => T("bill_notice_expired"),
                BillingStates.Revoked => T("bill_notice_revoked"),
                BillingStates.TrialExhausted => T("bill_notice_trial_used"),
                _ => "",
            };
            var pro = c.Plans.FirstOrDefault(x => x.Purchasable);
            var paid = PlanView.IsPaid(a.BillingState);   // Upgrade is the card's primary action only on a plan that is not paid
            UpgradeBtn.Visibility = pro != null && !paid ? Visibility.Visible : Visibility.Collapsed;
            UpgradeBtn.Content = pro?.PriceDisplay is { } price ? F("upgrade_to_price", pro.Name, price) : F("upgrade_to", pro?.Name);
            UpgradeBtn.IsEnabled = !c.UpgradePending && !_sections.Billing.IsLoading;
            ManageBillingBtn.Visibility = a.Subscription != null ? Visibility.Visible : Visibility.Collapsed;
            ManageBillingBtn.IsEnabled = !_sections.Billing.IsLoading;
            UpgradeStatus.Text = c.UpgradePending ? T("upgrade_pending") : "";
            BillingTerms.Text = pro == null ? "" : F("billing_terms", pro.Name, pro.PriceDisplay ?? "");
        }
        else
        {
            // No account data (signed out, or this account's data is not loaded yet): never leave the previous
            // account's plan, minutes or devices on screen.
            PlanText.Text = ""; PlanSubText.Text = ""; BillingNotice.Text = ""; UpgradeStatus.Text = ""; BillingTerms.Text = "";
            PlanBadge.Visibility = Visibility.Collapsed;
            UsageMetrics.ItemsSource = null;
            UpgradeBtn.Visibility = ManageBillingBtn.Visibility = Visibility.Collapsed;
            if (!c.IsSignedIn) { _devices = new(); DeviceList.ItemsSource = null; DevicesEmpty.Visibility = Visibility.Collapsed; SecurityMessage.Clear(); PrivacyMessage.Clear(); DangerMessage.Clear(); }
        }
        RenderSections();
        RefreshLegal();
        RefreshUpdates();
    }

    /// <summary>Inline status of each Account section (loading, failed with retry, saved data from …).</summary>
    private void RenderSections()
    {
        string Time(DateTimeOffset t) => t.ToLocalTime().ToString("t", Loc.Culture);
        void Render(SectionState s, InlineMessage m, string failedKey, bool retry = true)
        {
            if (s.Status == SectionStatus.Failed)
            {
                var detail = string.Join(" ", new[] { Loc.Relocalize(s.Error), s.StaleNote(Time) }.Where(x => !string.IsNullOrEmpty(x)));
                m.Show(T(failedKey), s.IsStale ? MessageKind.Warning : MessageKind.Error, detail, retry ? T("dlg_retry") : null);
            }
            else if (s.IsLoading && !s.HasData) m.Show(T("acct_section_loading"), MessageKind.Info);
            else m.Clear();
        }
        Render(_sections.Summary, SummaryMessage, "acct_summary_failed");
        Render(_sections.Devices, DevicesMessage, "acct_devices_failed");
        Render(_sections.Billing, BillingMessage, "acct_billing_failed", retry: _lastBillingAction != null);
        RefreshAccountBtn.IsEnabled = !_sections.Summary.IsLoading && !_sections.Devices.IsLoading;
    }

    /// <summary>The plan's state as a small badge next to its name (Trial, Active, Payment problem …).</summary>
    private void ShowPlanBadge(string billingState)
    {
        var key = PlanView.BadgeTone(billingState) switch
        {
            PlanTone.Positive => "B.Listening",
            PlanTone.Warning => "B.Thinking",
            PlanTone.Problem => "B.Error",
            PlanTone.Accent => "B.Accent",
            _ => "B.TextDim",
        };
        var c = ((SolidColorBrush)FindResource(key)).Color;
        PlanBadge.Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));
        PlanBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        PlanBadgeText.Foreground = (Brush)FindResource("B.Text");
        PlanBadgeText.Text = T(PlanView.BadgeKey(billingState));
        System.Windows.Automation.AutomationProperties.SetName(PlanBadge, PlanBadgeText.Text);
        PlanBadge.Visibility = Visibility.Visible;
    }

    /// <summary>One metric card of "Plan and usage" (bound in XAML): the bar is two star columns, so it scales with the card.</summary>
    public sealed record MetricRow(string Label, string Value, string Detail, Visibility DetailVisibility, Visibility BarVisibility,
        GridLength Filled, GridLength Rest, string BarName, Brush BarBrush)
    {
        public static MetricRow From(UsageMetric m)
        {
            var p = m.Progress ?? 0;
            var brush = (Brush)Application.Current.Resources[p >= 0.9 ? "B.Thinking" : "B.Accent"];
            return new MetricRow(m.Label, m.Value, m.Detail, m.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                m.Progress != null ? Visibility.Visible : Visibility.Collapsed,
                new GridLength(p, GridUnitType.Star), new GridLength(1 - p, GridUnitType.Star), $"{m.Label}: {m.Value}", brush);
        }
    }

    /// <summary>One registered device (bound in XAML): name, "This PC" tag, version and last seen, its own Remove action.</summary>
    public sealed record DeviceRow(DeviceDto Device, string Name, string Meta, Visibility ThisPcVisibility, string RemoveName, bool CanRemove);

    /// <summary>Two columns (Plan and Devices | Security, Privacy, Danger zone) on wide content, one column below the breakpoint.</summary>
    private void AccountDashboard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var two = AccountLayout.DashboardColumns(e.NewSize.Width) == 2;
        AccountColGap.Width = two ? (GridLength)FindResource("S.PaneGap") : new GridLength(0);
        AccountColSide.Width = two ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(AccountSideColumn, two ? 0 : 1);
        Grid.SetColumn(AccountSideColumn, two ? 2 : 0);
        Grid.SetColumnSpan(AccountSideColumn, two ? 1 : 3);
    }

    private string LegalSentence()
    {
        var l = _vm.Cloud.Config?.Links;
        if (l?.Terms != null && l.PrivacyPolicy != null) return T("legal_agree_both");
        if (l?.Terms != null) return T("legal_agree_terms");
        if (l?.PrivacyPolicy != null) return T("legal_agree_privacy");
        return "";
    }

    private void RefreshLegal()
    {
        LegalLinks.Children.Clear();
        var l = _vm.Cloud.Config?.Links;
        foreach (var (label, url) in new[] { (T("legal_privacy"), l?.PrivacyPolicy), (T("legal_terms"), l?.Terms), (T("legal_billing_terms"), l?.BillingTerms), (T("legal_support_link"), l?.Support) })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var b = new Button { Content = label, Style = (Style)FindResource("Btn.Ghost"), Margin = new Thickness(0, 0, 8, 4), ToolTip = url };
            b.Click += (_, _) => CloudService.OpenExternal(url);
            LegalLinks.Children.Add(b);
        }
        SupportEmailText.Text = l?.SupportEmail is { Length: > 0 } mail ? F("legal_support_email", mail) : "";
        LegalHeader.Visibility = LegalLinks.Children.Count > 0 || SupportEmailText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ProviderName(string p) => p switch { "google" => "Google", "azure" => "Microsoft", "github" => "GitHub", "apple" => "Apple", _ => p };

    private void ModeSignIn_Click(object sender, RoutedEventArgs e) { SetCreateMode(false); AuthMessage.Clear(); EmailBox.Focus(); }
    private void ModeCreate_Click(object sender, RoutedEventArgs e) { SetCreateMode(true); AuthMessage.Clear(); EmailBox.Focus(); }

    private void SetCreateMode(bool create)
    {
        _createMode = create;
        if (create) _auth.ToCreateAccount(); else if (_auth.Mode == AuthMode.CreateAccount) _auth.ToSignIn();
        AuthHeading.Text = T(_auth.HeadingKey);
        AuthSubtitle.Text = _auth.SubtitleKey is { } sub ? F(sub, _auth.PreviousEmail) : "";
        ConfirmPanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        if (!_authBusy) AuthSubmitBtn.Content = T(create ? "create_account" : "sign_in");
        ModeSignInBtn.FontWeight = create ? FontWeights.Normal : FontWeights.SemiBold;
        ModeCreateBtn.FontWeight = create ? FontWeights.SemiBold : FontWeights.Normal;
        ModeSignInBtn.Opacity = create ? 0.65 : 1; ModeCreateBtn.Opacity = create ? 1 : 0.65;
        // The chosen mode is announced and part of the tab order before the fields.
        System.Windows.Automation.AutomationProperties.SetItemStatus(ModeSignInBtn, create ? "" : T("auth_mode_selected"));
        System.Windows.Automation.AutomationProperties.SetItemStatus(ModeCreateBtn, create ? T("auth_mode_selected") : "");
    }

    /// <summary>Busy state of the sign-in form: the submit button shows the progress text and every submit path is disabled (no double submit).</summary>
    private void SetAuthBusy(bool busy, string? busyText = null)
    {
        _authBusy = busy;
        AuthSubmitBtn.IsEnabled = ModeSignInBtn.IsEnabled = ModeCreateBtn.IsEnabled = !busy;
        ForgotBtn.IsEnabled = !busy;
        EmailBox.IsReadOnly = busy;
        foreach (var b in ProviderButtons.Children.OfType<Button>()) b.IsEnabled = !busy;
        AuthSubmitBtn.Content = busy && busyText != null ? busyText : T(_createMode ? "create_account" : "sign_in");
    }

    private async void Email_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (PasswordBox.SecurePassword.Length == 0) { PasswordBox.Focus(); return; }
        await SubmitAuthAsync();
    }

    private async void Password_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_createMode && sender == PasswordBox && ConfirmBox.SecurePassword.Length == 0) { ConfirmBox.Focus(); return; }
        await SubmitAuthAsync();
    }

    private async void AuthSubmit_Click(object sender, RoutedEventArgs e) => await SubmitAuthAsync();

    private static bool ValidEmail(string email) => System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private void FieldError(Control field, string message)
    {
        Field.SetHasError(field, true);
        AuthMessage.Show(message, MessageKind.Error);
        field.Focus();
    }

    private async Task SubmitAuthAsync()
    {
        if (_authBusy) return;
        var email = EmailBox.Text.Trim();
        var pw = PasswordBox.Password;
        AuthMessage.Clear();
        if (!ValidEmail(email)) { FieldError(EmailBox, T("err_email_invalid")); return; }
        if (pw.Length == 0) { FieldError(PasswordBox, T("err_password_empty")); return; }
        if (_createMode)
        {
            if (pw.Length < 8) { FieldError(PasswordBox, T("err_password_short")); return; }
            if (pw != ConfirmBox.Password) { FieldError(ConfirmBox, T("err_password_mismatch")); return; }
            await RunAuth(async () =>
            {
                var (ok, confirm, error) = await _vm.Cloud.SignUpAsync(email, pw);
                if (ok && confirm) Dispatcher.Invoke(() => { SetCreateMode(false); AuthMessage.Show(F("acct_confirm_email_sent", email), MessageKind.Success); });
                return (ok, error);
            }, T("acct_creating"));
        }
        else await RunAuth(() => _vm.Cloud.SignInAsync(email, pw), T("acct_signing_in"));
    }

    private async Task RunAuth(Func<Task<(bool Ok, string? Error)>> action, string busyText)
    {
        if (_authBusy) return;
        SetAuthBusy(true, busyText);
        try
        {
            var (ok, error) = await action();
            if (!ok) AuthMessage.Show(error ?? T("err_sign_in_failed"), MessageKind.Error);
            else
            {
                PasswordBox.Clear(); ConfirmBox.Clear();
                if (_vm.Cloud.IsSignedIn)
                {
                    _vm.OpenWorkspaceForCurrentAccount();   // this account's data only
                    if (!_vm.SessionActive) _vm.BuildEngine();
                    _auth.Reset();   // a later sign-out starts at the plain sign-in again
                    _sections.Summary.Loaded(DateTimeOffset.Now);
                    await RefreshAccountSectionsAsync(summary: false);
                    Notify(T("toast_signed_in"), ToastKind.Success, _vm.Cloud.Account?.Email);
                }
            }
        }
        finally { SetAuthBusy(false); RefreshAll(); }
    }

    // The pending reset waits up to 15 minutes for the link. A new request (e.g. a corrected address) replaces it.
    private CancellationTokenSource? _resetCts;
    private string? _resetEmail;

    /// <summary>
    /// Password reset. Enumeration-safe: the same neutral notice appears for every syntactically valid address, whether
    /// or not an account exists, and it never claims that an e-mail was delivered. Rate limits are still reported.
    /// </summary>
    private async void Forgot_Click(object sender, RoutedEventArgs e)
    {
        if (_authBusy) return;
        var email = EmailBox.Text.Trim();
        AuthMessage.Clear();
        if (!ValidEmail(email)) { FieldError(EmailBox, T("err_email_first")); return; }
        void Requested() => AuthMessage.Show(PasswordResetCopy.Requested, MessageKind.Info, T("acct_reset_requested_hint"));
        // Same address again while its link is awaited: nothing new to send. Another address: the old wait ends.
        if (_resetCts != null && string.Equals(_resetEmail, email, StringComparison.OrdinalIgnoreCase)) { Requested(); return; }
        _resetCts?.Cancel();
        var cts = _resetCts = new CancellationTokenSource();
        _resetEmail = email;
        try
        {
            var (ok, error) = await _vm.Cloud.ResetPasswordAsync(email, () => Prompt.NewPasswordAsync(this),
                requested: () => Dispatcher.Invoke(Requested), ct: cts.Token);
            if (cts.IsCancellationRequested) return;   // replaced by a newer request
            if (ok) { AuthMessage.Show(T("acct_password_reset_done"), MessageKind.Success); _vm.OpenWorkspaceForCurrentAccount(); }
            else AuthMessage.Show(error ?? T("err_reset_failed"), MessageKind.Error);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_resetCts, cts)) { _resetCts = null; _resetEmail = null; }
            cts.Dispose();
            RefreshAll();
        }
    }

    /// <summary>
    /// Sensitive actions ask for the current password again (or a fresh provider sign-in for OAuth accounts). A wrong
    /// password is reported inside the prompt, which stays open for another try.
    /// </summary>
    private async Task<bool> ReauthenticateAsync(string reasonKey, InlineMessage messages)
    {
        messages.Clear();
        // Accounts that sign in with Google/Microsoft/GitHub/Apple have no password: confirm with that provider instead.
        if (_vm.Cloud.SignInProvider is { Length: > 0 } provider && provider != "email")
        {
            if (!await AppDialog.ConfirmAsync(this, F("reauth_provider_confirm", ProviderLabel(provider)), T("dlg_continue"), title: T("reauth_title"))) return false;
            var (pok, perror) = await _vm.Cloud.ReauthenticateWithProviderAsync(provider);
            if (!pok) messages.Show(perror ?? T("err_reauth_failed"), MessageKind.Error);
            return pok;
        }
        return await Prompt.CurrentPasswordAsync(this, T(reasonKey), _vm.Cloud.Account?.Email ?? "", async password =>
        {
            var (ok, error) = await _vm.Cloud.ReauthenticateAsync(password);
            return ok ? null : error ?? T("err_reauth_failed");
        });
    }

    private static string ProviderLabel(string provider) => provider switch
    {
        "google" => "Google", "azure" => "Microsoft", "github" => "GitHub", "apple" => "Apple",
        _ => provider.Length > 0 ? char.ToUpperInvariant(provider[0]) + provider[1..] : provider,
    };

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!await ReauthenticateAsync("reauth_reason_password", SecurityMessage)) return;
        var pw = await Prompt.NewPasswordAsync(this);
        if (pw == null) return;
        var (ok, error) = await _vm.Cloud.ChangePasswordAsync(pw);
        if (ok) { SecurityMessage.Show(T("acct_password_changed"), MessageKind.Success); Notify(T("acct_password_changed"), ToastKind.Success); }
        else SecurityMessage.Show(error ?? T("err_generic"), MessageKind.Error);
    }

    /// <summary>Loads the Account sections independently: one failed or slow request never fails the others.</summary>
    private async Task RefreshAccountSectionsAsync(bool summary)
    {
        if (!_vm.Cloud.IsSignedIn) { _sections.ResetAll(); RenderSections(); return; }
        var work = new List<Task> { LoadDevicesAsync() };
        if (summary) work.Add(LoadSummaryAsync());
        await Task.WhenAll(work);
        ApplyAccountLimits();
    }

    private async Task LoadSummaryAsync()
    {
        var ticket = _sections.Summary.Begin();
        RenderSections();
        var ok = await _vm.Cloud.RefreshAsync(CancellationToken.None);
        if (!_vm.Cloud.IsSignedIn) _sections.ResetAll();
        else if (ok && _vm.Cloud.State == CloudState.SignedIn) _sections.Summary.Succeed(ticket, DateTimeOffset.Now);
        else _sections.Summary.Fail(ticket, _vm.Cloud.LastRefreshError is { } ex ? CloudService.Describe(ex, includeReference: false) : T("svc_unavailable"));
        RefreshAccount();
    }

    private async Task LoadDevicesAsync()
    {
        var api = _vm.Cloud.Api;
        if (api == null || !_vm.Cloud.IsSignedIn) { DeviceList.ItemsSource = null; return; }
        var requestedFor = _vm.Cloud.Auth?.Session?.UserId;
        var ticket = _sections.Devices.Begin();
        RenderSections();
        try
        {
            var devices = await api.DevicesAsync(CancellationToken.None);
            if (_vm.Cloud.Auth?.Session?.UserId != requestedFor) return;   // the account changed while loading
            if (!_sections.Devices.Succeed(ticket, DateTimeOffset.Now)) return;
            ShowDevices(devices.Where(d => !d.Revoked).ToList());
        }
        catch (Exception ex) when (ex is BackendException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // The list already on screen stays (marked "saved data from …"); the correlation id goes to the log only.
            AppLog.Warn($"Devices request failed: {(ex as BackendException)?.Code ?? ex.GetType().Name} (ref {(ex as BackendException)?.CorrelationId})");
            if (_vm.Cloud.Auth?.Session?.UserId == requestedFor) _sections.Devices.Fail(ticket, CloudService.Describe(ex, includeReference: false));
        }
        finally { RenderSections(); }
    }

    private List<DeviceDto> _devices = new();
    private bool _deviceBusy;

    /// <summary>Rich device rows: this PC first, then the most recently seen.</summary>
    private void ShowDevices(List<DeviceDto> devices)
    {
        _devices = devices;
        bool IsThisPc(DeviceDto d) => d.IsCurrent || d.Id == _vm.Settings.CloudDeviceId;
        DeviceList.ItemsSource = devices.OrderByDescending(IsThisPc).ThenByDescending(d => d.LastSeenUtc)
            .Select(d => new DeviceRow(d, d.Name, F("acct_device_meta", d.ClientVersion, d.LastSeenUtc.ToLocalTime().ToString("g", Loc.Culture)),
                IsThisPc(d) ? Visibility.Visible : Visibility.Collapsed, F("acct_device_remove_name", d.Name), !_deviceBusy)).ToList();
        DevicesEmpty.Visibility = devices.Count == 0 && _sections.Devices.HasData ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SummaryRetry_Click(object sender, RoutedEventArgs e) { await LoadSummaryAsync(); ApplyAccountLimits(); }
    private async void DevicesRetry_Click(object sender, RoutedEventArgs e) => await LoadDevicesAsync();
    private async void BillingRetry_Click(object sender, RoutedEventArgs e) { if (_lastBillingAction != null) await RunBillingAsync(_lastBillingAction); }

    /// <summary>Plan limits for data that never leaves this PC: report retention (server value, applied locally).</summary>
    private void ApplyAccountLimits()
    {
        if (_vm.Cloud.Account?.Entitlements is { } e && Store != null)
        {
            var removed = Store.ApplyRetention(TimeSpan.FromDays(e.ReportRetentionDays), DateTime.UtcNow);
            if (removed > 0) { AppLog.Info($"Retention removed {removed} report(s)"); RefreshReports(SelectedReportId()); }
        }
    }

    private Func<Task<(bool Ok, string? Error)>>? _lastBillingAction;

    /// <summary>Billing actions (checkout, portal) report in the billing section; a retry repeats the failed action.</summary>
    private async Task RunBillingAsync(Func<Task<(bool Ok, string? Error)>> action)
    {
        if (_sections.Billing.IsLoading) return;
        _lastBillingAction = action;
        var ticket = _sections.Billing.Begin();
        RefreshAccount();
        var (ok, error) = await action();
        if (ok) _sections.Billing.Succeed(ticket, DateTimeOffset.Now);
        else _sections.Billing.Fail(ticket, error ?? T("err_generic"));
        RefreshAccount();
    }

    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        var pro = _vm.Cloud.Plans.FirstOrDefault(p => p.Purchasable);
        if (pro == null) return;
        await RunBillingAsync(() => _vm.Cloud.StartUpgradeAsync(pro.Code));
    }

    private async void Portal_Click(object sender, RoutedEventArgs e) => await RunBillingAsync(() => _vm.Cloud.OpenPortalAsync());

    private async void RefreshAccount_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAccountSectionsAsync(summary: true);
        if (_vm.Cloud.IsSignedIn && _sections.All.All(s => s.Status != SectionStatus.Failed)) Notify(T("toast_billing_refreshed"), ToastKind.Info);
        else if (!_vm.Cloud.IsSignedIn && _vm.Cloud.Message is { Length: > 0 } m) Notify(m, ToastKind.Warning);
    }

    /// <summary>Remove on a device row: confirmed (destructive, Cancel is the default) before the device is revoked.</summary>
    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DeviceRow { Device: var d } || _vm.Cloud.Api == null || _deviceBusy) return;
        if (!await AppDialog.ConfirmAsync(this, F("confirm_remove_device", d.Name), T("dlg_remove"), destructive: true) || _vm.Cloud.Api == null) return;
        _deviceBusy = true; ShowDevices(_devices);
        try { await _vm.Cloud.Api.RevokeDeviceAsync(d.Id, CancellationToken.None); Notify(T("toast_device_removed"), ToastKind.Success); }
        catch (Exception ex) when (ex is BackendException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Device removal failed: {(ex as BackendException)?.Code ?? ex.GetType().Name} (ref {(ex as BackendException)?.CorrelationId})");
            DevicesMessage.Show(T("acct_device_remove_failed"), MessageKind.Error, CloudService.Describe(ex, includeReference: false));
            return;
        }
        finally { _deviceBusy = false; ShowDevices(_devices); }
        await LoadDevicesAsync();
    }

    private async void ExportAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!await ReauthenticateAsync("reauth_reason_export", PrivacyMessage)) return;
        var dlg = new SaveFileDialog { FileName = "interview-assistant-account-export.json", Filter = T("file_filter_json") + " (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true || _vm.Cloud.Api == null) return;
        try
        {
            var json = await _vm.Cloud.Api.ExportAsync(CancellationToken.None);
            await File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            PrivacyMessage.Clear();
            Notify(T("toast_account_exported"), ToastKind.Success);
        }
        catch (BackendException ex) { AppLog.Warn($"Export failed: {ex.Code} (ref {ex.CorrelationId})"); PrivacyMessage.Show(CloudService.Describe(ex, includeReference: false), MessageKind.Error); }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Cloud.Api == null) return;
        if (!await AppDialog.ConfirmAsync(this, T("confirm_delete_account"), T("dlg_delete_account"), destructive: true)) return;
        if (!await ReauthenticateAsync("reauth_reason_delete", DangerMessage) || _vm.Cloud.Api == null) return;
        try
        {
            if (_vm.SessionActive) await _vm.StopAsync();
            var deleted = await _vm.Cloud.Api.DeleteAccountAsync(CancellationToken.None);
            var localRoot = _vm.Workspace?.Store.Root;
            var viewer = _vm.Workspace?.ViewerDir;
            _profileGuard.MarkClean(); _targetGuard.MarkClean();   // the account and its data are gone
            await _vm.SignOutAsync();
            // The service removes the sign-in too; if the auth provider was unreachable it finishes that automatically.
            RefreshAll();
            AuthMessage.Show(T(deleted?.Identity == "pending" ? "acct_deleted_identity_pending" : "acct_deleted"), MessageKind.Success);
            // The service data is gone; this PC's copy of the account's profiles, interviews and reports is the
            // user's to keep or remove.
            if (localRoot != null && await AppDialog.ConfirmAsync(this, T("confirm_delete_local_data"), T("dlg_delete"), destructive: true))
            {
                foreach (var dir in new[] { localRoot, viewer })
                    try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("Local data removal failed", ex); }
                Notify(T("toast_local_data_deleted"), ToastKind.Info);
            }
        }
        catch (BackendException ex) { AppLog.Warn($"Account deletion failed: {ex.Code} (ref {ex.CorrelationId})"); DangerMessage.Show(CloudService.Describe(ex, includeReference: false), MessageKind.Error); }
        RefreshAll();
    }

    /// <summary>Sign-out closes this account's workspace: pending profile/interview edits are asked about first.</summary>
    private async Task<bool> ConfirmSignOutAsync()
    {
        if (!await _nav.CanProceedAsync(ChangeReason.SignOut, _profileGuard, _targetGuard)) return false;
        return !_vm.SessionActive || await AppDialog.ConfirmAsync(this, T("confirm_sign_out_session"), T("dlg_sign_out"), destructive: true);
    }

    /// <summary>Sign out (header menu): the normal sign-out with its "Signed out" notification; the form starts at Sign in.</summary>
    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmSignOutAsync()) return;
        await _vm.SignOutAsync();
        _auth.Reset();
        EmailBox.Text = "";
        AuthMessage.Clear();
        RefreshAll();
        ShowPage("account");
    }

    /// <summary>
    /// Switch account (Security card and header menu): asks first, signs out QUIETLY (no "Signed out" notification — the
    /// page that follows says what happened) and opens the sign-in form in its "Sign in with another account" state with
    /// an empty e-mail field and the focus in it.
    /// </summary>
    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        var previous = _vm.Cloud.Account?.Email ?? _vm.Cloud.Auth?.Session?.Email ?? "";
        if (!await AppDialog.ConfirmAsync(this, F("switch_confirm_message", previous), T("switch_account"), title: T("switch_confirm_title"))) return;
        if (!await ConfirmSignOutAsync()) return;
        await _vm.SignOutAsync(quiet: true);
        _auth.StartSwitch(previous);
        EmailBox.Text = "";
        PasswordBox.Clear(); ConfirmBox.Clear();
        AuthMessage.Clear();
        RefreshAll();
        ShowPage("account");
        _ = Dispatcher.BeginInvoke(new Action(() => EmailBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ----- header account button and menu -----

    private Button[] MenuButtons => new[] { MenuManageAccount, MenuSettings, MenuSwitchAccount, MenuSignOut };

    /// <summary>Header button: avatar initial + e-mail (signed in) or "Sign in"; the menu shows the e-mail and the plan line.</summary>
    private void RefreshAccountMenu()
    {
        var c = _vm.Cloud;
        var signedIn = c.IsSignedIn;
        var email = c.Account?.Email ?? c.Auth?.Session?.Email ?? "";
        AccountAvatar.Visibility = AccountMenuChevron.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountAvatarText.Text = AccountMenu.Initial(email);
        // Signed out: "Sign in" (or the build's state, e.g. developer mode, when no account service is configured).
        AccountMenuText.Text = signedIn ? email : c.IsConfigured ? T("sign_in") : _vm.AccountLabel;
        AccountMenuText.FlowDirection = signedIn ? FlowDirection.LeftToRight : FlowDirection;   // an e-mail reads left-to-right in RTL too
        AccountMenuBtn.ToolTip = signedIn ? T("tt_account_menu") : T("tt_open_account");
        System.Windows.Automation.AutomationProperties.SetName(AccountMenuBtn, signedIn ? F("acct_menu_name", email) : AccountMenuText.Text);
        AccountMenuEmail.Text = email;
        AccountMenuPlan.Text = c.Account is { } a && signedIn ? MainViewModel.BillingLabel(a) : _vm.AccountLabel;
        for (int i = 0; i < AccountMenu.Items.Count; i++) MenuButtons[i].Content = T(AccountMenu.LabelKey(AccountMenu.Items[i]));
        if (!signedIn && IsAccountMenuOpen) CloseAccountMenu(returnFocus: false);
    }

    internal bool IsAccountMenuOpen => AccountMenuLayer.Visibility == Visibility.Visible;

    /// <summary>Enter, Space or a click: signed in, the menu opens with focus on its first entry; signed out, Account opens.</summary>
    private void AccountMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!AccountMenu.OpensMenu(_vm.Cloud.IsSignedIn)) { Navigate("account"); return; }
        if (IsAccountMenuOpen) CloseAccountMenu(returnFocus: true); else OpenAccountMenu();
    }

    internal void OpenAccountMenu()
    {
        RefreshAccountMenu();
        // Under the button, its end edge aligned with the button's end edge (both mirror in right-to-left layouts).
        var layer = (FrameworkElement)AccountMenuLayer.Parent;
        var corner = AccountMenuBtn.TranslatePoint(new Point(AccountMenuBtn.ActualWidth, AccountMenuBtn.ActualHeight), layer);
        AccountMenuCard.Margin = new Thickness(0, Math.Max(0, corner.Y + 4), Math.Max(8, layer.ActualWidth - corner.X), 0);
        AccountMenuLayer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => { if (IsAccountMenuOpen) MenuManageAccount.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    internal void CloseAccountMenu(bool returnFocus)
    {
        if (!IsAccountMenuOpen) return;
        AccountMenuLayer.Visibility = Visibility.Collapsed;
        if (returnFocus) AccountMenuBtn.Focus();
    }

    private void AccountMenuBackdrop_MouseDown(object sender, MouseButtonEventArgs e) { e.Handled = true; CloseAccountMenu(returnFocus: false); }

    /// <summary>Arrow keys move between the entries (wrapping), Home/End jump, Escape closes and returns focus to the button.</summary>
    private void AccountMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var buttons = MenuButtons;
        var current = Array.IndexOf(buttons, Keyboard.FocusedElement as Button);
        int? next = e.Key switch
        {
            Key.Down => AccountMenu.Move(current, buttons.Length, down: true),
            Key.Up => AccountMenu.Move(current, buttons.Length, down: false),
            Key.Home => 0,
            Key.End => buttons.Length - 1,
            _ => null,
        };
        if (e.Key == Key.Escape) { e.Handled = true; CloseAccountMenu(returnFocus: true); return; }
        if (next is int i && i >= 0) { e.Handled = true; buttons[i].Focus(); }
    }

    private void MenuManageAccount_Click(object sender, RoutedEventArgs e) { CloseAccountMenu(returnFocus: true); Navigate(AccountMenu.Route(AccountMenuItem.ManageAccount)!); }
    private void MenuSettings_Click(object sender, RoutedEventArgs e) { CloseAccountMenu(returnFocus: true); Navigate(AccountMenu.Route(AccountMenuItem.Settings)!); }
    private void MenuSwitchAccount_Click(object sender, RoutedEventArgs e) { CloseAccountMenu(returnFocus: true); SwitchAccount_Click(sender, e); }
    private void MenuSignOut_Click(object sender, RoutedEventArgs e) { CloseAccountMenu(returnFocus: true); SignOut_Click(sender, e); }
}
