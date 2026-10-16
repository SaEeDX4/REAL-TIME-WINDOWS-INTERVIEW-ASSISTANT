namespace InterviewAssistant.App.UiState;

/// <summary>
/// The Live window's "n / total" history counter. Hidden (empty) whenever no history item is on screen: no history
/// at all, nothing selected yet, or a position that no longer exists after the engine was rebuilt (sign-out, account
/// switch, settings change). It can therefore never read "0 / 1" or "0 / 0".
/// </summary>
public static class HistoryCounter
{
    /// <summary>The 1-based position and the total to show, or null when the counter is hidden.</summary>
    public static (int Shown, int Total)? Numbers(int position, int count)
    {
        if (count <= 0 || position < 0 || position >= count) return null;
        return (position + 1, count);
    }

    /// <summary>Localized counter text ("3 / 8"), or "" when hidden.</summary>
    public static string Text(int position, int count) =>
        Numbers(position, count) is { } n ? Core.Languages.Loc.F("history_label", n.Shown, n.Total) : "";
}

/// <summary>
/// Arrow-key movement in a horizontal segmented control: Left/Right move to the visually adjacent segment, so in a
/// right-to-left layout (segments mirrored) Left moves forward. Stops at the ends (no wrap-around).
/// </summary>
public static class SegmentedNavigation
{
    public static int Move(int current, int count, bool leftKey, bool rightToLeft)
    {
        if (count <= 0) return -1;
        var forward = leftKey == rightToLeft;   // LTR: Right = forward; RTL: Left = forward
        return Math.Clamp(current + (forward ? 1 : -1), 0, count - 1);
    }
}
