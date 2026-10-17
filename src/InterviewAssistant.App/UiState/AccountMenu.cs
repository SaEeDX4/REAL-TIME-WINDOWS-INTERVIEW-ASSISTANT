namespace InterviewAssistant.App.UiState;

public enum AccountMenuItem { ManageAccount, Settings, SwitchAccount, SignOut }

/// <summary>
/// The header account button and its menu (Home title bar). Signed in: avatar initial + e-mail, opening a menu with the
/// account e-mail, the plan line and Manage account / Settings / Switch account / Sign out. Signed out: the button
/// reads "Sign in" and goes straight to the Account page. Plain class (no WPF) so the rules are unit-tested.
/// </summary>
public static class AccountMenu
{
    /// <summary>Menu entries in display (and arrow-key) order.</summary>
    public static IReadOnlyList<AccountMenuItem> Items { get; } =
        new[] { AccountMenuItem.ManageAccount, AccountMenuItem.Settings, AccountMenuItem.SwitchAccount, AccountMenuItem.SignOut };

    public static string LabelKey(AccountMenuItem item) => item switch
    {
        AccountMenuItem.ManageAccount => "menu_manage_account",
        AccountMenuItem.Settings => "settings",
        AccountMenuItem.SwitchAccount => "switch_account",
        _ => "sign_out",
    };

    /// <summary>Home route each entry opens (null = an action on this page: switch account, sign out).</summary>
    public static string? Route(AccountMenuItem item) => item switch
    {
        AccountMenuItem.ManageAccount => "account",
        AccountMenuItem.Settings => "settings",
        _ => null,
    };

    /// <summary>The button opens the menu only for a signed-in account; otherwise it leads to the sign-in page.</summary>
    public static bool OpensMenu(bool signedIn) => signedIn;

    /// <summary>Avatar letter: the first letter or digit of the e-mail, upper-case ("?" when there is none).</summary>
    public static string Initial(string? email)
    {
        foreach (var ch in email ?? "")
            if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "?";
    }

    /// <summary>Arrow keys move through the entries and wrap around (Down after the last goes to the first).</summary>
    public static int Move(int current, int count, bool down)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return down ? 0 : count - 1;
        return ((down ? current + 1 : current - 1) % count + count) % count;
    }
}
