namespace InterviewAssistant.App.UiState;

/// <summary>The distinct states of the signed-out Account page's form.</summary>
public enum AuthMode
{
    /// <summary>E-mail + password, "Sign in".</summary>
    SignIn,
    /// <summary>E-mail + password + confirmation, password rules and the legal sentence, "Create account".</summary>
    CreateAccount,
    /// <summary>E-mail only, "Send reset link".</summary>
    ForgotPassword,
    /// <summary>The enumeration-safe notice, "Back to sign in" and "Resend" after a cooldown.</summary>
    ForgotSent,
    /// <summary>Sign in after "Switch account": its own headline, the e-mail starts empty.</summary>
    SwitchAccount,
}

public enum AuthField { Email, Password, ConfirmPassword }

/// <summary>
/// State machine of the shared auth form (one card, labels above fields, inline errors under the field, one primary
/// button, secondary links). Each state has its own heading, subtitle, visible fields and primary button; mode changes
/// happen through links ("New here? Create an account", "Already have an account? Sign in", "Back to sign in").
/// While a request runs (<see cref="Busy"/>) the state does not change. Plain class (no WPF) so it is unit-tested.
/// </summary>
public sealed class AuthFormState
{
    /// <summary>"Resend" becomes available this long after a reset link was requested.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Which sign-in flavour "Back to sign in" returns to (plain sign-in, or the switch-account sign-in).</summary>
    private AuthMode _signInMode = AuthMode.SignIn;

    public AuthMode Mode { get; private set; } = AuthMode.SignIn;
    /// <summary>The account that was left by "Switch account" (named in the subtitle; never pre-filled).</summary>
    public string? PreviousEmail { get; private set; }
    /// <summary>When "Resend" may be used again (ForgotSent only).</summary>
    public DateTimeOffset? ResendAvailableAt { get; private set; }
    public bool Busy { get; private set; }
    public event Action? Changed;

    public IReadOnlyList<AuthField> VisibleFields => FieldsFor(Mode);

    public static IReadOnlyList<AuthField> FieldsFor(AuthMode mode) => mode switch
    {
        AuthMode.SignIn or AuthMode.SwitchAccount => new[] { AuthField.Email, AuthField.Password },
        AuthMode.CreateAccount => new[] { AuthField.Email, AuthField.Password, AuthField.ConfirmPassword },
        AuthMode.ForgotPassword => new[] { AuthField.Email },
        _ => Array.Empty<AuthField>(),
    };

    public bool Shows(AuthField field) => VisibleFields.Contains(field);

    public string HeadingKey => Mode switch
    {
        AuthMode.CreateAccount => "auth_create_title",
        AuthMode.ForgotPassword => "auth_forgot_title",
        AuthMode.ForgotSent => "auth_forgot_sent_title",
        AuthMode.SwitchAccount => "sign_in_another",
        _ => "auth_signin_title",
    };

    /// <summary>Subtitle key; null = no subtitle (ForgotSent shows the neutral notice instead).</summary>
    public string? SubtitleKey => Mode switch
    {
        AuthMode.CreateAccount => "auth_create_subtitle",
        AuthMode.ForgotPassword => "auth_forgot_subtitle",
        AuthMode.ForgotSent => null,
        AuthMode.SwitchAccount => PreviousEmail is { Length: > 0 } ? "auth_switch_subtitle" : "auth_switch_subtitle_plain",
        _ => "auth_signin_subtitle",
    };

    /// <summary>The one primary button of the state.</summary>
    public string PrimaryKey => Mode switch
    {
        AuthMode.CreateAccount => "create_account",
        AuthMode.ForgotPassword => "auth_send_reset_link",
        AuthMode.ForgotSent => "auth_back_to_sign_in",
        _ => "sign_in",
    };

    /// <summary>Text of the primary button while its request runs; null = the state has no request.</summary>
    public string? BusyKey => Mode switch
    {
        AuthMode.SignIn or AuthMode.SwitchAccount => "acct_signing_in",
        AuthMode.CreateAccount => "acct_creating",
        AuthMode.ForgotPassword => "auth_sending_reset",
        _ => null,
    };

    /// <summary>The primary button submits the form (ForgotSent's primary only goes back to sign-in).</summary>
    public bool PrimarySubmits => Mode != AuthMode.ForgotSent;

    public bool ShowsForgotLink => Mode is AuthMode.SignIn or AuthMode.SwitchAccount;
    /// <summary>"New here? Create an account".</summary>
    public bool ShowsCreateLink => Mode is AuthMode.SignIn or AuthMode.SwitchAccount;
    /// <summary>"Already have an account? Sign in".</summary>
    public bool ShowsSignInLink => Mode == AuthMode.CreateAccount;
    /// <summary>"Back to sign in" as a link (in ForgotSent it is the primary button).</summary>
    public bool ShowsBackLink => Mode == AuthMode.ForgotPassword;
    public bool ShowsProviders => Mode is AuthMode.SignIn or AuthMode.CreateAccount or AuthMode.SwitchAccount;
    public bool ShowsPasswordRules => Mode == AuthMode.CreateAccount;
    public bool ShowsLegal => Mode == AuthMode.CreateAccount;
    public bool ShowsResend => Mode == AuthMode.ForgotSent;

    public bool CanResend(DateTimeOffset now) => Mode == AuthMode.ForgotSent && !Busy && (ResendAvailableAt is not { } at || now >= at);

    /// <summary>Whole seconds until "Resend" is available (0 = now).</summary>
    public int ResendSecondsLeft(DateTimeOffset now) =>
        Mode == AuthMode.ForgotSent && ResendAvailableAt is { } at && at > now ? (int)Math.Ceiling((at - now).TotalSeconds) : 0;

    // ---------------- transitions ----------------

    /// <summary>"New here? Create an account" (from either sign-in flavour).</summary>
    public bool ToCreateAccount() => Go(AuthMode.CreateAccount, Mode is AuthMode.SignIn or AuthMode.SwitchAccount);

    /// <summary>"Forgot password?" — the e-mail typed so far stays in the field.</summary>
    public bool ToForgotPassword() => Go(AuthMode.ForgotPassword, Mode is AuthMode.SignIn or AuthMode.SwitchAccount);

    /// <summary>"Already have an account? Sign in" / "Back to sign in": the sign-in flavour the user came from.</summary>
    public bool ToSignIn() => Go(_signInMode, Mode is AuthMode.CreateAccount or AuthMode.ForgotPassword or AuthMode.ForgotSent);

    /// <summary>The reset request was accepted (the same for known and unknown addresses), or "Resend" sent it again.</summary>
    public bool ResetRequested(DateTimeOffset now)
    {
        if (Mode is not (AuthMode.ForgotPassword or AuthMode.ForgotSent)) return false;
        ResendAvailableAt = now + ResendCooldown;
        Mode = AuthMode.ForgotSent;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The reset failed after it was sent (link not opened in time, new password not set): send again from the e-mail step.</summary>
    public bool ResetFailed() => Go(AuthMode.ForgotPassword, Mode is AuthMode.ForgotSent or AuthMode.ForgotPassword);

    /// <summary>"Switch account": the previous account was signed out quietly; sign in with another one.</summary>
    public void StartSwitch(string? previousEmail)
    {
        PreviousEmail = string.IsNullOrWhiteSpace(previousEmail) ? null : previousEmail.Trim();
        _signInMode = AuthMode.SwitchAccount;
        Busy = false;
        ResendAvailableAt = null;
        Mode = AuthMode.SwitchAccount;
        Changed?.Invoke();
    }

    /// <summary>Signed in, signed out normally or account deleted: back to the plain sign-in state.</summary>
    public void Reset()
    {
        PreviousEmail = null;
        _signInMode = AuthMode.SignIn;
        Busy = false;
        ResendAvailableAt = null;
        Mode = AuthMode.SignIn;
        Changed?.Invoke();
    }

    public void SetBusy(bool busy)
    {
        if (Busy == busy) return;
        Busy = busy;
        Changed?.Invoke();
    }

    private bool Go(AuthMode target, bool allowed)
    {
        if (!allowed || Busy) return false;
        if (target != AuthMode.ForgotSent) ResendAvailableAt = null;
        Mode = target;
        Changed?.Invoke();
        return true;
    }
}
