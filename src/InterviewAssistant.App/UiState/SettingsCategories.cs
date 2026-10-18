namespace InterviewAssistant.App.UiState;

/// <summary>One category of the Settings page: its id (also the deep-link section, "settings/&lt;id&gt;") and its texts.</summary>
public sealed record SettingsCategory(string Id)
{
    /// <summary>Title in the category list and as the heading of the category ("Audio").</summary>
    public string TitleKey => "set_nav_" + Id;
    /// <summary>One-line description under the title ("Meeting audio device and level").</summary>
    public string DescriptionKey => $"set_cat_{Id}_desc";
}

/// <summary>
/// The Settings page shows one category at a time (progressive disclosure), chosen in a list on the reading-order start
/// side. Deep links ("settings/audio" from the Live gear and the pre-interview check) select a category; anything unknown
/// selects the last used one, or Audio.
/// </summary>
public static class SettingsCategories
{
    public const string Audio = "audio", Answers = "answers", Listening = "listening", Window = "window",
        Hotkeys = "hotkeys", Privacy = "privacy", Updates = "updates";

    /// <summary>In list order.</summary>
    public static readonly IReadOnlyList<SettingsCategory> All =
        new[] { Audio, Answers, Listening, Window, Hotkeys, Privacy, Updates }.Select(id => new SettingsCategory(id)).ToArray();

    public static IReadOnlyList<string> Ids { get; } = All.Select(c => c.Id).ToArray();

    public static bool IsKnown(string? id) => id != null && Ids.Contains(id);

    /// <summary>Position in the list, or -1.</summary>
    public static int IndexOf(string? id)
    {
        for (var i = 0; i < Ids.Count; i++) if (Ids[i] == id) return i;
        return -1;
    }

    /// <summary>
    /// The category a deep-link section opens: the section itself when known (case and spaces ignored), otherwise the
    /// last used category when known, otherwise Audio.
    /// </summary>
    public static string Parse(string? section, string? lastUsed = null)
    {
        var s = section?.Trim().ToLowerInvariant();
        if (IsKnown(s)) return s!;
        return IsKnown(lastUsed) ? lastUsed! : Audio;
    }

    /// <summary>Keyboard Up/Down in the category list: the neighbour, clamped at both ends (no wrap-around).</summary>
    public static string Move(string current, int delta)
    {
        var i = IndexOf(current);
        if (i < 0) return Audio;
        return Ids[Math.Clamp(i + delta, 0, Ids.Count - 1)];
    }
}
