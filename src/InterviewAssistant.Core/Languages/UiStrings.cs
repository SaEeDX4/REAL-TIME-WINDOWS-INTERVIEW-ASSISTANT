namespace InterviewAssistant.Core.Languages;

/// <summary>Compatibility wrapper over <see cref="Loc"/> (tables live in Languages/i18n/*.json).</summary>
public static class UiStrings
{
    public static readonly string[] Keys =
    {
        "status_ready", "status_listening", "status_speech", "status_finalizing", "status_answering", "status_paused", "status_reconnecting",
        "status_no_audio", "status_api_error", "status_stopped",
        "question", "answer", "coach", "waiting", "type_hint", "start", "pause", "resume",
        "shorter", "technical", "example", "full", "keywords", "structure", "adaptive_concise", "adaptive_rapid", "minutes_left",
        "sign_in", "sign_out", "account", "upgrade", "manage_billing", "profiles", "interviews", "reports", "settings",
        "prepare", "start_interview", "authorized_use", "ready",
        "status_quiet", "status_answer_ready", "detail_listening", "detail_paused",
    };

    public static IReadOnlyCollection<string> Languages => LanguageRegistry.All.Select(l => l.Code).ToList();

    public static string Get(string? lang, string key) => Loc.Get(lang, key);

    public static string Format(string? lang, string key, params object[] args) => Loc.Format(lang, key, args);

    public static bool IsComplete(string lang) => Loc.MissingKeys(lang).Count == 0;
}
