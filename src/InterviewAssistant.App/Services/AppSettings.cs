using System.Text.Json;

namespace InterviewAssistant.App.Services;

public enum FontPreset { Compact, Normal, Large, ExtraLarge }

/// <summary>User preferences persisted as JSON in %APPDATA%. Never contains secrets (the API key lives in SecretStore).</summary>
public sealed class AppSettings
{
    public string? PlaybackDeviceId { get; set; }           // null = follow Windows default playback device
    public string AnswerModel { get; set; } = "gpt-4.1-mini";
    public string TranscriptionModel { get; set; } = "gpt-4o-transcribe";
    public string RealtimeProtocol { get; set; } = "auto";
    public FontPreset FontPreset { get; set; } = FontPreset.Large;
    public double WindowOpacity { get; set; } = 0.97;
    public double VadSensitivity { get; set; } = 1.0;      // turn-settle multiplier (higher = waits longer)
    public bool UseFastCache { get; set; } = true;
    public bool CompactMode { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowDiagnostics { get; set; }
    public bool SaveSessionTranscript { get; set; }          // privacy: off by default
    public string HotkeyToggleWindow { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyStartPause { get; set; } = "Ctrl+Alt+L";
    public string HotkeyManualInput { get; set; } = "Ctrl+Alt+Q";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 620;
    public double Height { get; set; } = 560;
    public bool FirstRunCompleted { get; set; }
    // Approximate unit prices for the cost estimate shown in diagnostics (editable; estimates only).
    public double PriceTranscribePerMinute { get; set; } = 0.006;
    public double PriceInputPerMTok { get; set; } = 0.40;
    public double PriceOutputPerMTok { get; set; } = 1.60;

    private static string FilePath => Path.Combine(AppPaths.Roaming, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn("Settings unreadable, using defaults: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true); // atomic replace: no half-written settings on crash
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save settings: " + ex.Message);
        }
    }

    public double AnswerFontSize => FontPreset switch { FontPreset.Compact => 16, FontPreset.Normal => 19, FontPreset.Large => 22, _ => 26 };
    public double QuestionFontSize => FontPreset switch { FontPreset.Compact => 14, FontPreset.Normal => 16, FontPreset.Large => 17, _ => 19 };
}
