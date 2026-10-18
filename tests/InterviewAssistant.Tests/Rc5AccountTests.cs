using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// rc.5 Account and authentication: the auth form state machine, the header account menu, the dashboard's plan view,
/// the delete-account rules and copy (UiState, unit-tested) and the wiring of the WPF pages (source level — WPF cannot
/// run here; the Windows self-test drives the real window).
/// </summary>
public class Rc5AccountTests
{
    private static readonly string[] Languages = { "en", "es", "fr", "de", "pt", "it", "ar", "fa", "zh", "hi" };

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", relative)).Replace("\r\n", "\n");

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (private|public|internal|protected) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private static string AccountPage(string xaml) =>
        xaml[xaml.IndexOf("x:Name=\"PageAccount\"", StringComparison.Ordinal)..xaml.IndexOf("<!-- ===== Settings", StringComparison.Ordinal)];

    // ---------------- auth form state machine ----------------

    [Fact]
    public void AuthForm_StartsAtSignIn_WithEmailAndPassword()
    {
        var s = new AuthFormState();
        Assert.Equal(AuthMode.SignIn, s.Mode);
        Assert.Equal(new[] { AuthField.Email, AuthField.Password }, s.VisibleFields);
        Assert.Equal("sign_in", s.PrimaryKey);
        Assert.Equal("auth_signin_title", s.HeadingKey);
        Assert.True(s.ShowsForgotLink && s.ShowsCreateLink && s.ShowsProviders);
        Assert.False(s.ShowsSignInLink || s.ShowsBackLink || s.ShowsResend || s.ShowsLegal);
    }

    [Theory]
    [InlineData(AuthMode.SignIn, new[] { AuthField.Email, AuthField.Password }, "sign_in")]
    [InlineData(AuthMode.CreateAccount, new[] { AuthField.Email, AuthField.Password, AuthField.ConfirmPassword }, "create_account")]
    [InlineData(AuthMode.ForgotPassword, new[] { AuthField.Email }, "auth_send_reset_link")]
    [InlineData(AuthMode.ForgotSent, new AuthField[0], "auth_back_to_sign_in")]
    [InlineData(AuthMode.SwitchAccount, new[] { AuthField.Email, AuthField.Password }, "sign_in")]
    public void AuthForm_EveryState_HasItsFieldsAndOnePrimaryButton(AuthMode mode, AuthField[] fields, string primary)
    {
        var s = Reach(mode);
        Assert.Equal(mode, s.Mode);
        Assert.Equal(fields, s.VisibleFields);
        Assert.Equal(primary, s.PrimaryKey);
        Assert.True(Loc.Has(s.HeadingKey), s.HeadingKey);
        if (s.SubtitleKey != null) Assert.True(Loc.Has(s.SubtitleKey), s.SubtitleKey);
        if (s.BusyKey != null) Assert.True(Loc.Has(s.BusyKey), s.BusyKey);
        Assert.True(Loc.Has(s.PrimaryKey));
    }

    [Fact]
    public void AuthForm_ForgotPassword_HasOnlyTheEmailField_AndABackLink()
    {
        var s = Reach(AuthMode.ForgotPassword);
        Assert.Equal(new[] { AuthField.Email }, s.VisibleFields);
        Assert.False(s.Shows(AuthField.Password) || s.Shows(AuthField.ConfirmPassword));
        Assert.True(s.ShowsBackLink);
        Assert.False(s.ShowsProviders || s.ShowsCreateLink || s.ShowsForgotLink);
        Assert.Equal("auth_sending_reset", s.BusyKey);
    }

    [Fact]
    public void AuthForm_Transitions_FollowTheLinks()
    {
        var s = new AuthFormState();
        Assert.False(s.ToSignIn());                       // already signed-in state: nothing to go back to
        Assert.True(s.ToCreateAccount()); Assert.Equal(AuthMode.CreateAccount, s.Mode);
        Assert.False(s.ToForgotPassword());               // no forgot link while creating an account
        Assert.True(s.ToSignIn()); Assert.Equal(AuthMode.SignIn, s.Mode);
        Assert.True(s.ToForgotPassword()); Assert.Equal(AuthMode.ForgotPassword, s.Mode);
        Assert.False(s.ToCreateAccount());
        Assert.True(s.ToSignIn()); Assert.Equal(AuthMode.SignIn, s.Mode);
        Assert.False(s.ResetRequested(DateTimeOffset.UtcNow));   // only from the forgot-password state
    }

    [Fact]
    public void AuthForm_Busy_BlocksEveryTransition()
    {
        var s = new AuthFormState();
        var changes = 0;
        s.Changed += () => changes++;
        s.SetBusy(true);
        Assert.False(s.ToCreateAccount());
        Assert.False(s.ToForgotPassword());
        Assert.Equal(AuthMode.SignIn, s.Mode);
        s.SetBusy(false);
        Assert.True(s.ToCreateAccount());
        Assert.Equal(3, changes);   // busy on, busy off, create account
    }

    [Fact]
    public void AuthForm_ResetSent_ResendAfterCooldown_BackToSignIn()
    {
        var t0 = DateTimeOffset.Parse("2026-10-17T10:00:00Z");
        var s = Reach(AuthMode.ForgotPassword);
        Assert.True(s.ResetRequested(t0));
        Assert.Equal(AuthMode.ForgotSent, s.Mode);
        Assert.Empty(s.VisibleFields);
        Assert.False(s.PrimarySubmits);
        Assert.True(s.ShowsResend);
        Assert.False(s.CanResend(t0.AddSeconds(5)));
        Assert.Equal(25, s.ResendSecondsLeft(t0.AddSeconds(5)));
        Assert.True(s.CanResend(t0 + AuthFormState.ResendCooldown));
        Assert.Equal(0, s.ResendSecondsLeft(t0 + AuthFormState.ResendCooldown));
        s.SetBusy(true);
        Assert.False(s.CanResend(t0.AddMinutes(5)));     // a resend is running
        s.SetBusy(false);
        Assert.True(s.ResetRequested(t0.AddMinutes(1)));  // resent: the cooldown starts again
        Assert.False(s.CanResend(t0.AddMinutes(1).AddSeconds(1)));
        Assert.True(s.ResetFailed());                     // link expired: back to the e-mail step
        Assert.Equal(AuthMode.ForgotPassword, s.Mode);
        Assert.Null(s.ResendAvailableAt);
        Assert.True(s.ToSignIn());
        Assert.Equal(AuthMode.SignIn, s.Mode);
    }

    [Fact]
    public void AuthForm_SwitchAccount_HasItsOwnHeadline_AndBackReturnsToIt()
    {
        var s = new AuthFormState();
        s.StartSwitch("old@example.test");
        Assert.Equal(AuthMode.SwitchAccount, s.Mode);
        Assert.Equal("sign_in_another", s.HeadingKey);
        Assert.Equal("Sign in with another account", Loc.TableFor("en")[s.HeadingKey]);
        Assert.Equal("auth_switch_subtitle", s.SubtitleKey);
        Assert.Equal("old@example.test", s.PreviousEmail);
        Assert.Equal(new[] { AuthField.Email, AuthField.Password }, s.VisibleFields);
        Assert.True(s.ToForgotPassword());
        Assert.True(s.ToSignIn());
        Assert.Equal(AuthMode.SwitchAccount, s.Mode);    // "Back to sign in" keeps the switch-account context
        Assert.True(s.ToCreateAccount());
        Assert.True(s.ToSignIn());
        Assert.Equal(AuthMode.SwitchAccount, s.Mode);
        s.Reset();                                        // signed in: a later sign-out starts at the plain sign-in
        Assert.Equal(AuthMode.SignIn, s.Mode);
        Assert.Null(s.PreviousEmail);
        new AuthFormState().StartSwitch(null);
        var plain = new AuthFormState(); plain.StartSwitch("  ");
        Assert.Equal("auth_switch_subtitle_plain", plain.SubtitleKey);
    }

    private static AuthFormState Reach(AuthMode mode)
    {
        var s = new AuthFormState();
        switch (mode)
        {
            case AuthMode.CreateAccount: s.ToCreateAccount(); break;
            case AuthMode.ForgotPassword: s.ToForgotPassword(); break;
            case AuthMode.ForgotSent: s.ToForgotPassword(); s.ResetRequested(DateTimeOffset.UtcNow); break;
            case AuthMode.SwitchAccount: s.StartSwitch("a@b.test"); break;
        }
        return s;
    }

    // ---------------- header account menu ----------------

    [Fact]
    public void AccountMenu_Items_Order_Labels_AndKeyboard()
    {
        Assert.Equal(new[] { AccountMenuItem.ManageAccount, AccountMenuItem.Settings, AccountMenuItem.SwitchAccount, AccountMenuItem.SignOut }, AccountMenu.Items);
        foreach (var lang in Languages)
            foreach (var item in AccountMenu.Items)
                Assert.False(string.IsNullOrWhiteSpace(Loc.TableFor(lang)[AccountMenu.LabelKey(item)]));
        Assert.Equal("account", AccountMenu.Route(AccountMenuItem.ManageAccount));
        Assert.Equal("settings", AccountMenu.Route(AccountMenuItem.Settings));
        Assert.Null(AccountMenu.Route(AccountMenuItem.SignOut));
        Assert.Equal(1, AccountMenu.Move(0, 4, down: true));
        Assert.Equal(0, AccountMenu.Move(3, 4, down: true));     // wraps
        Assert.Equal(3, AccountMenu.Move(0, 4, down: false));
        Assert.Equal(0, AccountMenu.Move(-1, 4, down: true));    // nothing focused yet
        Assert.Equal(3, AccountMenu.Move(-1, 4, down: false));
        Assert.True(AccountMenu.OpensMenu(signedIn: true));
        Assert.False(AccountMenu.OpensMenu(signedIn: false));   // signed out: "Sign in" goes to the Account page
        Assert.Equal("S", AccountMenu.Initial("sam@example.test"));
        Assert.Equal("7", AccountMenu.Initial("_7x@example.test"));
        Assert.Equal("?", AccountMenu.Initial(""));
    }

    [Fact]
    public void Header_HasOneAccountButtonAndMenu_NoBadgeOrSignOutButton()
    {
        var xaml = App("HomeWindow.xaml");
        Assert.DoesNotContain("AccountBadge", xaml);
        Assert.DoesNotContain("HeaderSignOutBtn", xaml);
        Assert.Contains("x:Name=\"AccountMenuBtn\"", xaml);
        foreach (var (name, key) in new[] { ("MenuManageAccount", "menu_manage_account"), ("MenuSettings", "settings"), ("MenuSwitchAccount", "switch_account"), ("MenuSignOut", "sign_out") })
            Assert.Matches($@"x:Name=""{name}""[^>]*Content=""\{{v:Loc {key}\}}""", xaml);
        var cs = App("HomeWindow.Account.cs");
        var keys = Method(cs, "private void AccountMenu_PreviewKeyDown(");
        foreach (var k in new[] { "Key.Down", "Key.Up", "Key.Escape", "AccountMenu.Move(", "CloseAccountMenu(returnFocus: true)" }) Assert.Contains(k, keys);
        Assert.Contains("AccountMenu.OpensMenu(", Method(cs, "private void AccountMenuBtn_Click("));
        Assert.Contains("Navigate(\"account\")", Method(cs, "private void AccountMenuBtn_Click("));
        Assert.Contains("T(\"sign_in\")", Method(cs, "private void RefreshAccountMenu("));
    }

    // ---------------- Account page / switch account ----------------

    [Fact]
    public void AccountPage_HasNoSignOutButton_AndSwitchAccountIsNamedSo()
    {
        var page = AccountPage(App("HomeWindow.xaml"));
        Assert.DoesNotContain("SignOut_Click", page);
        Assert.DoesNotContain("{v:Loc sign_out}", page);
        Assert.Contains("Content=\"{v:Loc switch_account}\" Click=\"SwitchAccount_Click\"", page);
        Assert.Equal("Switch account", Loc.TableFor("en")["switch_account"]);
        // the signed-out form switches modes through links, not chips
        Assert.DoesNotContain("ModeSignInBtn", page);
        Assert.DoesNotContain("Btn.Chip", page);
        // danger zone: its own red-bordered card holding Delete account…
        var danger = page[page.IndexOf("x:Name=\"DangerZone\"", StringComparison.Ordinal)..];
        Assert.Contains("BorderBrush=\"{StaticResource B.Error}\"", danger[..200]);
        Assert.Contains("Click=\"DeleteAccount_Click\"", danger[..danger.IndexOf("</Border>", StringComparison.Ordinal)]);
    }

    [Fact]
    public void SwitchAccount_ConfirmsThenSignsOutQuietly_NormalSignOutKeepsItsToast()
    {
        var cs = App("HomeWindow.Account.cs");
        var sw = Method(cs, "private async void SwitchAccount_Click(");
        var confirm = sw.IndexOf("AppDialog.ConfirmAsync(", StringComparison.Ordinal);
        var quiet = sw.IndexOf("SignOutAsync(quiet: true)", StringComparison.Ordinal);
        Assert.True(confirm > 0 && quiet > confirm, "asks before signing out quietly");
        Assert.Contains("switch_confirm_message", sw);
        Assert.Contains("_auth.StartSwitch(", sw);
        Assert.Contains("EmailBox.Text = \"\"", sw);
        Assert.Contains("EmailBox.Focus()", sw);
        var signOut = Method(cs, "private async void SignOut_Click(");
        Assert.Contains("_vm.SignOutAsync();", signOut);
        Assert.DoesNotContain("quiet", signOut);
        var vm = Method(App("ViewModels/MainViewModel.cs"), "public async Task SignOutAsync(");
        Assert.Contains("bool quiet = false", vm);
        Assert.Contains("if (!quiet) Toast(Loc.T(\"toast_signed_out\")", vm);
        Assert.Contains("{0}", Loc.TableFor("en")["switch_confirm_message"]);
    }

    [Fact]
    public void ForgotPassword_IsADedicatedState_WiredToTheStateMachine()
    {
        var cs = App("HomeWindow.Account.cs");
        var apply = Method(cs, "private void ApplyAuthState(");
        Assert.Contains("PasswordPanel.Visibility = Vis(s.Shows(AuthField.Password))", apply);
        Assert.Contains("ConfirmPanel.Visibility = Vis(s.Shows(AuthField.ConfirmPassword))", apply);
        Assert.Contains("EmailBox.IsReadOnly = s.Busy", apply);
        var forgot = Method(cs, "private void Forgot_Click(");
        Assert.Contains("_auth.ToForgotPassword()", forgot);
        Assert.DoesNotContain("EmailBox.Text =", forgot);   // pre-filled with what was typed
        var reset = Method(cs, "private async Task RequestResetAsync(");
        Assert.Contains("PasswordResetCopy.Requested", reset);
        Assert.Contains("_resetCts?.Cancel()", reset);       // a newer request replaces the pending wait
        Assert.Contains("auth_reset_link_expired", Method(cs, "private void ShowResetError("));
        Assert.Contains("dlg_retry", Method(cs, "private void ShowResetError("));
    }

    // ---------------- dashboard ----------------

    [Theory]
    [InlineData(899, 1)] [InlineData(900, 2)] [InlineData(1600, 2)] [InlineData(400, 1)]
    public void Dashboard_StacksBelowTheBreakpoint(double width, int columns) => Assert.Equal(columns, AccountLayout.DashboardColumns(width));

    [Fact]
    public void PlanView_BadgesAndMetrics()
    {
        foreach (var state in new[] { BillingStates.Trial, BillingStates.TrialExhausted, BillingStates.Active, BillingStates.Canceling, BillingStates.PastDue, BillingStates.Paused, BillingStates.Expired, BillingStates.Revoked })
            foreach (var lang in Languages)
                Assert.True(Loc.TableFor(lang).ContainsKey(PlanView.BadgeKey(state)), state);
        Assert.True(PlanView.IsPaid(BillingStates.Paused));
        Assert.False(PlanView.IsPaid(BillingStates.Trial));
        Assert.Equal(PlanTone.Warning, PlanView.BadgeTone(BillingStates.PastDue));
        var before = Loc.Language;
        Loc.Language = "en";
        try
        {
            var e = new Entitlements("pro", "Pro", "active", 5, 20, 600, 1, 3, 90, true, 30);
            var u = new UsageSummary(DateTime.UtcNow, new DateTime(2026, 11, 1), 150 * 60, 450 * 60, 4, 16, 0, 120 * 60, 180);
            var a = new AccountDto(Guid.NewGuid(), "a@b.test", "active", e, u, null, DateTime.UtcNow, BillingStates.Active);
            var m = PlanView.Metrics(a, d => d.ToString("yyyy-MM-dd"));
            Assert.Equal(new[] { "minutes_used", "minutes_remaining", "available_today", "preparations", "limits" }, m.Select(x => x.Id));
            Assert.Equal("150 of 600 min", m[0].Value);
            Assert.Equal(0.25, m[0].Progress);
            Assert.Equal("120 min", m[2].Value);
            Assert.Equal(0.2, m[3].Progress);
            Assert.Null(m[4].Progress);
        }
        finally { Loc.Language = before; }
        Assert.Equal(0, PlanView.Fraction(5, 0));
        Assert.Equal(1, PlanView.Fraction(9, 3));
    }

    // ---------------- delete account ----------------

    [Theory]
    [InlineData("active", false, true)]
    [InlineData("active", true, false)]    // already set to end: the service lets the deletion through
    [InlineData("trialing", false, true)]
    [InlineData("past_due", false, true)]
    [InlineData("paused", false, true)]
    [InlineData("canceled", false, false)]
    public void Deletion_SubscriptionRule_MirrorsTheService(string status, bool cancelAtPeriodEnd, bool blocks) =>
        Assert.Equal(blocks, AccountDeletion.SubscriptionBlocks(new SubscriptionDto("pro", status, null, cancelAtPeriodEnd, null)));

    [Fact]
    public void DeleteDialog_TextsExist_AndAreTranslated_InAllTenLanguages()
    {
        Assert.False(AccountDeletion.SubscriptionBlocks(null));
        var en = Loc.TableFor("en");
        Assert.Equal("Permanently delete your account?", en["del_title"]);
        Assert.Equal("This action cannot be undone.", en["del_irreversible"]);
        Assert.Equal("Keep account", en["del_keep"]);
        Assert.Equal("Delete permanently", en["del_confirm"]);
        Assert.Equal("Paddle keeps the billing records it is legally required to keep.", en["del_note_paddle"]);
        foreach (var lang in Languages)
        {
            var t = Loc.TableFor(lang);
            foreach (var k in AccountDeletion.DialogKeys)
            {
                Assert.True(t.TryGetValue(k, out var v) && v.Trim().Length > 0, $"{lang}: {k}");
                if (lang != "en") Assert.NotEqual(en[k], v);
            }
        }
        var dialog = App("Views/DeleteAccountDialog.cs");
        foreach (var k in AccountDeletion.DialogKeys) Assert.Contains($"\"{k}\"", dialog);
        Assert.Matches(@"del_keep""\), Result = AppDialogResult\.Cancel, Role = DialogButtonRole\.Secondary, IsCancel = true, IsDefault = true", dialog);
        Assert.Contains("Role = DialogButtonRole.Destructive", dialog);
    }

    [Fact]
    public void DeleteAccount_DialogThenReauthenticationThenDeletion_ThenASeparateLocalDataQuestion()
    {
        var del = Method(App("HomeWindow.Account.cs"), "private async void DeleteAccount_Click(");
        int At(string s) { var i = del.IndexOf(s, StringComparison.Ordinal); Assert.True(i >= 0, s); return i; }
        Assert.True(At("DeleteAccountDialog.ConfirmAsync(") < At("ReauthenticateAsync(\"reauth_reason_delete\""));
        Assert.True(At("ReauthenticateAsync(\"reauth_reason_delete\"") < At("DeleteAccountAsync("));
        Assert.True(At("DeleteAccountAsync(") < At("DeleteAccountDialog.AskDeleteLocalDataAsync("));
        Assert.DoesNotContain("MessageBox", del);
    }
}
