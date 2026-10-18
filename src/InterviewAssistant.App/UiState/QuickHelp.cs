namespace InterviewAssistant.App.UiState;

/// <summary>What the user is doing when they press "?" in the Live Assistant.</summary>
public enum QuickHelpState { SignedOut, Setup, Listening, Paused }

/// <summary>The one link the popover offers for its state.</summary>
public enum QuickHelpAction { None, SignIn, PreInterviewCheck }

/// <summary>A titled group of short tips (localization keys).</summary>
public sealed record QuickHelpSection(string TitleKey, IReadOnlyList<string> LineKeys);

/// <summary>A shortcut row: what it does (key) and the configured keys ("Ctrl+Alt+L", shown left-to-right).</summary>
public sealed record QuickHelpHotkey(string LabelKey, string Keys);

/// <summary>The user's configured hotkeys (Settings → Hotkeys).</summary>
public sealed record QuickHelpHotkeys(string StartPause, string ManualInput, string ToggleWindow, string Coach);

public sealed record QuickHelpContent(IReadOnlyList<QuickHelpSection> Sections, IReadOnlyList<QuickHelpHotkey> Hotkeys, QuickHelpAction Action)
{
    /// <summary>Every localization key the content uses (tests: all exist and are translated).</summary>
    public IEnumerable<string> Keys => Sections.SelectMany(s => s.LineKeys.Prepend(s.TitleKey)).Concat(Hotkeys.Select(h => h.LabelKey))
        .Concat(QuickHelp.ActionKey(Action) is { } a ? new[] { a } : Array.Empty<string>());
}

/// <summary>
/// Context-aware Quick help (the "?" popover, rc.5 — replaces the big first-use overlay): what to do right now, with
/// the hotkeys the user actually configured. Signed out → how to sign in; setup → start listening when the interview
/// begins, run the pre-interview check; listening / paused → what to do in the interview; an answer on screen adds
/// how to rework and browse it. There is no "Don't show again": it opens by itself once (first use), then only on "?".
/// </summary>
public static class QuickHelp
{
    /// <summary>A running interview wins over the sign-in state (an expired sign-in keeps the session running).</summary>
    public static QuickHelpState StateFor(bool signInRequired, bool listening, bool paused) =>
        paused ? QuickHelpState.Paused : listening ? QuickHelpState.Listening : signInRequired ? QuickHelpState.SignedOut : QuickHelpState.Setup;

    public static QuickHelpContent For(QuickHelpState state, bool answerShown, QuickHelpHotkeys hk)
    {
        var sections = new List<QuickHelpSection>();
        var keys = new List<QuickHelpHotkey>();
        var action = QuickHelpAction.None;
        switch (state)
        {
            case QuickHelpState.SignedOut:
                sections.Add(new("qh_title_signed_out", new[] { "qh_signed_out_1", "qh_signed_out_2" }));
                keys.Add(new("qh_hk_window", hk.ToggleWindow));
                action = QuickHelpAction.SignIn;
                break;
            case QuickHelpState.Setup:
                sections.Add(new("qh_title_setup", new[] { "qh_setup_1", "qh_setup_2", "qh_setup_3" }));
                keys.Add(new("qh_hk_start", hk.StartPause));
                keys.Add(new("qh_hk_type", hk.ManualInput));
                keys.Add(new("qh_hk_window", hk.ToggleWindow));
                action = QuickHelpAction.PreInterviewCheck;
                break;
            case QuickHelpState.Listening:
                sections.Add(new("qh_title_listening", new[] { "qh_listening_1", "qh_listening_2", "qh_listening_3" }));
                keys.Add(new("qh_hk_start", hk.StartPause));
                keys.Add(new("qh_hk_type", hk.ManualInput));
                keys.Add(new("qh_hk_coach", hk.Coach));
                break;
            default:
                sections.Add(new("qh_title_paused", new[] { "qh_paused_1", "qh_paused_2" }));
                keys.Add(new("qh_hk_start", hk.StartPause));
                keys.Add(new("qh_hk_type", hk.ManualInput));
                break;
        }
        if (answerShown && state != QuickHelpState.SignedOut)
            sections.Add(new("qh_title_answer", new[] { "qh_answer_1", "qh_answer_2" }));
        return new QuickHelpContent(sections, keys.Where(k => !string.IsNullOrWhiteSpace(k.Keys)).ToList(), action);
    }

    /// <summary>Label key of the action link (null: no link).</summary>
    public static string? ActionKey(QuickHelpAction a) => a switch
    {
        QuickHelpAction.SignIn => "sign_in",
        QuickHelpAction.PreInterviewCheck => "qh_action_check",
        _ => null,
    };

    /// <summary>Home deep link of the action (see <see cref="HomeRoute"/>).</summary>
    public static string? ActionRoute(QuickHelpAction a) => a switch
    {
        QuickHelpAction.SignIn => "account",
        QuickHelpAction.PreInterviewCheck => "preinterview",
        _ => null,
    };
}
