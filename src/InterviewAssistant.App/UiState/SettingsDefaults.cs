using static InterviewAssistant.App.UiState.SettingsCategories;

namespace InterviewAssistant.App.UiState;

/// <summary>
/// "Reset this section" and "Restore all defaults" on the Settings page. Each category owns an explicit list of preference
/// fields (AppSettings property names); a reset copies exactly those fields from a fresh <c>AppSettings</c>. Account,
/// session, workspace, device id, first-run and authorized-use state, window placement and the UI language are never
/// touched (<see cref="Protected"/>), and neither are values that the Settings page does not show (<see cref="NotOnSettingsPage"/>).
/// Every AppSettings property is in exactly one of these lists (checked by a unit test), so a new setting must be classified.
/// </summary>
public static class SettingsDefaults
{
    /// <summary>Preference fields per category, in category order.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FieldsByCategory = new Dictionary<string, IReadOnlyList<string>>
    {
        [Audio] = new[] { "PlaybackDeviceId" },
        [Answers] = new[] { "AnswerModel", "TranscriptionModel", "RealtimeProtocol", "FontPreset", "UseFastCache" },
        [Listening] = new[] { "VadSensitivity" },
        [Window] = new[] { "WindowOpacity", "AlwaysOnTop", "CompactMode", "AutoFitAnswer" },
        [Hotkeys] = new[] { "HotkeyToggleWindow", "HotkeyStartPause", "HotkeyManualInput", "HotkeyCoachToggle", "HotkeyResetAdaptive" },
        [Privacy] = new[] { "SaveSessionTranscript" },
        [Updates] = new[] { "UpdateChannel" },
    };

    /// <summary>Every preference field that "Restore all defaults" resets.</summary>
    public static IReadOnlyList<string> PreferenceFields { get; } = Ids.SelectMany(id => FieldsByCategory[id]).ToArray();

    /// <summary>Never changed by a reset: who the user is, what they agreed to, their data and where their windows are.</summary>
    public static readonly IReadOnlyList<string> Protected = new[]
    {
        "Mode", "CloudDeviceId", "ActiveProfileId", "ActiveTargetId",          // account, session and workspace
        "AuthorizedUseAcknowledged", "FirstRunCompleted",                       // first run and authorized use
        "UiLanguage",                                                           // app language (Home sidebar)
        "Left", "Top", "Width", "Height", "HomeLeft", "HomeTop", "HomeWidth", "HomeHeight", "HomeMaximized",   // window placement
        "FocusRestoreLeft", "FocusRestoreTop", "FocusRestoreWidth", "FocusRestoreHeight",                      // placement before Interview Focus
        "SettingsVersion",                                                      // file format
    };

    /// <summary>Also never changed by a reset: chosen elsewhere in the app (Live window, interview and report pickers, diagnostics).</summary>
    public static readonly IReadOnlyList<string> NotOnSettingsPage = new[]
    {
        "AnswerLanguage", "ReportLanguage", "StartInCoachMode", "ShowDiagnostics", "LiveIntroDismissed",
        "InterviewFocusPreset", "InterviewFocusIntroSeen",                        // Interview Focus (Live window → More)
        "PriceTranscribePerMinute", "PriceInputPerMTok", "PriceOutputPerMTok",
    };

    /// <summary>Changing one of these rebuilds the answer engine (live-safe during an interview).</summary>
    public static readonly IReadOnlyList<string> EngineFields = new[] { "AnswerModel", "TranscriptionModel", "RealtimeProtocol", "UseFastCache", "VadSensitivity" };
    /// <summary>Changing one of these (developer builds) applies only after the interview stops.</summary>
    public static readonly IReadOnlyList<string> ModelFields = new[] { "AnswerModel", "TranscriptionModel", "RealtimeProtocol" };

    /// <summary>The fields a reset of <paramref name="category"/> changes; null = all preference fields; unknown = none.</summary>
    public static IReadOnlyList<string> FieldsOf(string? category) =>
        category == null ? PreferenceFields : FieldsByCategory.TryGetValue(category, out var f) ? f : Array.Empty<string>();

    /// <summary>
    /// Copies the fields of <paramref name="category"/> (null = every preference field) from <paramref name="defaults"/>
    /// (a freshly constructed settings object) into <paramref name="target"/>. Returns the fields whose value changed.
    /// </summary>
    public static IReadOnlyList<string> Reset<T>(T target, T defaults, string? category) where T : class
    {
        var changed = new List<string>();
        foreach (var name in FieldsOf(category))
        {
            var p = typeof(T).GetProperty(name) ?? throw new InvalidOperationException($"{typeof(T).Name} has no setting {name}");
            var value = p.GetValue(defaults);
            if (Equals(p.GetValue(target), value)) continue;
            p.SetValue(target, value);
            changed.Add(name);
        }
        return changed;
    }

    /// <summary>What applying the changed fields needs: an engine rebuild, and whether model choices changed.</summary>
    public static (bool EngineChanged, bool ModelChanged) EffectOf(IEnumerable<string> changed)
    {
        var set = changed.ToHashSet();
        return (EngineFields.Any(set.Contains), ModelFields.Any(set.Contains));
    }
}
